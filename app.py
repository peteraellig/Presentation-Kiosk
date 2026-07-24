#Kiosk 1.4.7

import os
import sys
import socket
import json
import time
import ipaddress
import threading
import shutil

import psutil   

from datetime import datetime, time as dtime, timedelta
from flask import Flask, jsonify, request, Response, abort, send_from_directory
from flask_socketio import SocketIO, emit

last_net = psutil.net_io_counters()


# ─── Port festlegen ─────────────────────────────────────────────
port = 53100  # Hier ändern, wenn ein anderer Port gewünscht ist

# ─── Schutz vor doppeltem Start ────────────────────────────────
def is_port_in_use(p):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        return s.connect_ex(("127.0.0.1", p)) == 0

if is_port_in_use(port):
    print(f"[X] app.py läuft bereits (Port {port} ist belegt).")
    sys.exit(1)
# ───----------------------------------------------------------------


# ─── bereistellen der lokalen IP ,falls der Computer nicht an einem DHCP Netzwerk hängt, kommt nur localhost zurück ───────────────────────────────────────────────

APP_START_TS = time.time()

def print_terminal_header(ip, port, ips):
    width = shutil.get_terminal_size((80, 20)).columns
    sep = "-" * width
    print("\n-------------------------------------------------------------")
    print("\n          Presentation Kiosk 1.4.7 Peter Aellig              ")
    print("\n-------------------------------------------------------------")
    print("\n             You can access the display at:                  ")
    print(f"   Presentation:  http://{ip}:{port}")
    print(f"   Admin:         http://{ip}:{port}/admin")
    print(f"   Remote:        http://{ip}:{port}/remote")
    if len(ips) > 1:
        print("  (Further recognised IPs: " + ", ".join(ips) + ")")
    print("-------------------------------------------------------------\n")

# aktualisiert anzeige alle 10 sekunden
def refresh_terminal_every_10s():
    while True:
        # Bildschirm leeren
        os.system('cls' if os.name == 'nt' else 'clear')

        # Kopf anzeigen
        print_terminal_header(ip, port, ips)

        # Zeit + Uptime
        now = datetime.now()
        uptime = int(time.time() - APP_START_TS)
        days, rem = divmod(uptime, 86400)
        hours, rem = divmod(rem, 3600)
        minutes, seconds = divmod(rem, 60)

        print(f"   Current date/time: {now.strftime('%d-%m-%Y %H:%M:%S')}")
        print(f"   Uptime: {days} days, {hours:02}:{minutes:02}:{seconds:02}")

        # 🔹 CPU- und RAM-Auslastung
        global last_net

        process = psutil.Process(os.getpid())
        cpu = psutil.cpu_percent(interval=None)
        mem = process.memory_info().rss / (1024 * 1024)  # in MB

        # Netzwerktraffic
        net = psutil.net_io_counters()

        sent_speed = (net.bytes_sent - last_net.bytes_sent) / 1024 / 1024 / 10
        recv_speed = (net.bytes_recv - last_net.bytes_recv) / 1024 / 1024 / 10

        last_net = net

        print(f"   CPU-load: {cpu:4.1f}% | used RAM: {mem:5.1f} MB | Net: ↑{sent_speed:.2f} MB/s ↓{recv_speed:.2f} MB/s")

        # nur alle 10 Sekunden aktualisieren
        time.sleep(10)

 
def get_local_ipv4_addresses():
    """Gibt eine Liste lokaler IPv4-Adressen zurück (ohne 127.0.0.1)."""
    addrs = set()
    try:
        for res in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET, socket.SOCK_DGRAM):
            addrs.add(res[4][0])
    except Exception:
        pass
    try:
        for res in socket.getaddrinfo(None, 0, socket.AF_INET, socket.SOCK_DGRAM):
            addrs.add(res[4][0])
    except Exception:
        pass

    # Filtern: keine Loopback/unspecified
    candidates = []
    for a in addrs:
        ip = ipaddress.ip_address(a)
        if not ip.is_loopback and not ip.is_unspecified:
            candidates.append(a)

    # Private Adressen bevorzugen
    priv = [a for a in candidates if ipaddress.ip_address(a).is_private]
    return priv or candidates or ["127.0.0.1"]

def get_server_ip():
    """Bevorzugt private LAN-IP; fällt auf 127.0.0.1 zurück, wenn nichts anderes da ist."""
    return get_local_ipv4_addresses()[0]

ip = get_server_ip()
ips = get_local_ipv4_addresses()

print_terminal_header(ip, port, ips)

# ─── Konfiguration ───────────────────────────────────────────────
password_protection = True  # Setze auf False, wenn kein Passwort benötigt wird (z.B. bei internem Netzwerk wie ZeroTier)
API_protection = False  # Setze auf False, wenn kein Passwort benötigt wird (z.B. bei internem Netzwerk wie ZeroTier)
USERNAME = "admin"
PASSWORD = "password123"
STREAMDECK_INPUT_LOCK = True  # StreamDeck beim Serverstart gesperrt

# Standard-Medium beim Serverstart (z.B. Sponsorclip oder Startbild)
DEFAULT_PROGRAM = {"type": "video", "src": "videos/video1.mp4"}

# Zeitgesteuerter Reset auf ein definiertes Medium (z.B. nachts)
SCHEDULE_TIME = "02:00"  # Uhrzeit im Format HH:MM
SCHEDULE_PROGRAM = {"type": "video", "src": "videos/video1.mp4"}  # Medium, das dann gezeigt wird
# ──────────────────────────────────────────────────────────

# Flask- und SocketIO-Initialisierung
app = Flask(__name__, static_folder="static")
socketio = SocketIO(app, manage_session=False)

CONFIG_PATH = os.path.join(os.path.dirname(__file__), 'button_config.json')
BACKUP_DIR = os.path.join(os.path.dirname(__file__), 'Backups')

IMAGE_EXTENSIONS = ('.jpg', '.jpeg', '.png', '.gif', '.bmp')
VIDEO_EXTENSIONS = ('.mp4', '.webm', '.mov')

def check_media_available(media):
    media_type = media.get("type", "")
    src = media.get("src", "")
    label = media.get("label") or src or media_type

    if media_type == "playlist":
        src = "videos_playlist/"
    if not src:
        return False, f"Medium '{label}' fehlt: Es ist kein Ordner oder Dateiname eingetragen."

    static_root = os.path.abspath(app.static_folder)
    media_path = os.path.abspath(os.path.join(static_root, src.replace("/", os.sep)))
    try:
        if os.path.commonpath([static_root, media_path]) != static_root:
            return False, f"Medium '{label}' hat einen ungültigen Pfad: {src}"
    except ValueError:
        return False, f"Medium '{label}' hat einen ungültigen Pfad: {src}"

    if media_type == "slideshow":
        if not os.path.isdir(media_path):
            return False, f"Medium '{label}' fehlt: Ordner 'static/{src}' wurde nicht gefunden."
        if not any(
            name.lower().endswith(IMAGE_EXTENSIONS)
            for name in os.listdir(media_path)
            if os.path.isfile(os.path.join(media_path, name))
        ):
            return False, f"Medium '{label}' fehlt: Ordner 'static/{src}' enthält keine Bilder."
    elif media_type == "playlist":
        if not os.path.isdir(media_path):
            return False, "Medium 'Playlist' fehlt: Ordner 'static/videos_playlist/' wurde nicht gefunden."
        if not any(
            name.lower().endswith(VIDEO_EXTENSIONS)
            for name in os.listdir(media_path)
            if os.path.isfile(os.path.join(media_path, name))
        ):
            return False, "Medium 'Playlist' fehlt: Ordner 'static/videos_playlist/' enthält keine Videos."
    elif media_type in ("video", "image") and not os.path.isfile(media_path):
        return False, f"Medium '{label}' fehlt: Datei 'static/{src}' wurde nicht gefunden."

    return True, ""

def backup_button_config():
    if not os.path.isfile(CONFIG_PATH):
        return
    os.makedirs(BACKUP_DIR, exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    backup_path = os.path.join(BACKUP_DIR, f"button_config_{timestamp}.json")
    shutil.copy2(CONFIG_PATH, backup_path)

    backups = sorted(
        (
            os.path.join(BACKUP_DIR, name)
            for name in os.listdir(BACKUP_DIR)
            if name.startswith("button_config_") and name.endswith(".json")
        ),
        key=os.path.getmtime,
        reverse=True
    )
    for old_backup in backups[20:]:
        os.remove(old_backup)

STANDARD_BUTTON_CONFIG = {
    "buttons": [
        { "enabled": True,  "label": "PreshowSponsorloop",   "type": "video",     "src": "videos/video1.mp4" },
        { "enabled": True,  "label": "Evacuation",           "type": "slideshow", "src": "bild1/" },
        { "enabled": True,  "label": "Broadcastcut off",     "type": "slideshow", "src": "bild2/" },
        { "enabled": True,  "label": "Artist missing",       "type": "slideshow", "src": "bild3/" },
        { "enabled": True,  "label": "Lockdown",             "type": "slideshow", "src": "bild4/" },
        { "enabled": False, "label": "Notice 5",             "type": "slideshow", "src": "bild5/" },
        { "enabled": False, "label": "Notice 6",             "type": "slideshow", "src": "bild6/" },
        { "enabled": False, "label": "Notice 7",             "type": "slideshow", "src": "bild7/" },
        { "enabled": False, "label": "Notice 8",             "type": "slideshow", "src": "bild8/" },
        { "enabled": False, "label": "Notice 9",             "type": "slideshow", "src": "bild9/" },
        { "enabled": False, "label": "Notice 10",            "type": "slideshow", "src": "bild10/" },
        { "enabled": False, "label": "Video2",               "type": "video",     "src": "videos/video2.mp4" },
        { "enabled": False, "label": "Video3",               "type": "video",     "src": "videos/video3.mp4" },
        { "enabled": False, "label": "Video4",               "type": "video",     "src": "videos/video3.mp4" },
        { "enabled": False, "label": "Video5",               "type": "video",     "src": "videos/video3.mp4" },
        { "enabled": False, "label": "Playlist",             "type": "playlist",  "src": "" }
    ]
}

def ensure_button_config_exists():
    if not os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, 'w') as f:
            json.dump(STANDARD_BUTTON_CONFIG, f, indent=2)
        print("✔️ Standard button_config.json wurde neu erstellt.")
    else:
        try:
            with open(CONFIG_PATH, 'r') as f:
                json.load(f)
        except Exception:
            with open(CONFIG_PATH, 'w') as f:
                json.dump(STANDARD_BUTTON_CONFIG, f, indent=2)
            print("✔️ Fehlerhafte button_config.json durch Standard ersetzt.")

ensure_button_config_exists()

# Globaler Zustand
current_media = DEFAULT_PROGRAM.copy()
presentation_clients = set()  # Verbindet registrierte Präsentationsclients
remote_clients = set()        # Verbindet registrierte Remote-Clients

# ─── HTTP-Routen ────────────────────────────────────────────────
@app.route('/')
@app.route('/presentation')
@app.route('/presentation.html')
def presentation():
    # Hauptanzeige für den Präsentationsscreen
    return app.send_static_file('presentation.html')

def authenticate():
    # Liefert eine 401-Fehlermeldung mit Basic-Auth-Challenge
    return Response(
        'Authentication required', 401,
        {'WWW-Authenticate': 'Basic realm="Login Required"'}
    )

def check_auth(auth):
    # Überprüft Benutzernamen und Passwort
    return auth and auth.username == USERNAME and auth.password == PASSWORD


@app.route('/remote')
def remote():
    # Remote-Oberfläche mit optionalem Passwortschutz
    if password_protection:
        auth = request.authorization
        if not check_auth(auth):
            return authenticate()
    return app.send_static_file('remote.html')

@app.route('/admin')
def admin_panel():
    # Admin-Oberfläche mit Statusübersicht
    if password_protection:
        auth = request.authorization
        if not check_auth(auth):
            return authenticate()
    return app.send_static_file('admin.html')

@app.route('/filelist')
def filelist():
    # Gibt eine Liste gültiger Medien-Dateien im gewünschten Unterordner zurück
    folder = request.args.get('folder', '')
    safe_folder = os.path.normpath(folder)
    if safe_folder.startswith('..') or os.path.isabs(safe_folder):
        abort(400, description="Ungültiger Ordner")

    full_path = os.path.join(app.static_folder, safe_folder)
    if not os.path.isdir(full_path):
        return jsonify([]), 404

    try:
        allowed_extensions = ('.jpg', '.jpeg', '.png', '.gif', '.bmp',
                              '.mp4', '.webm', '.mov')
        files = [
            f for f in os.listdir(full_path)
            if f.strip() and os.path.isfile(os.path.join(full_path, f))
            and f.lower().endswith(allowed_extensions)
        ]
        files.sort()
        return jsonify(files)
    except Exception as e:
        print("Fehler in /filelist:", e)
        return jsonify([])

@app.route('/api')
def api_control():
    # Die StreamDeck-Anwendung läuft auf demselben Rechner und verwendet
    # localhost. API-Aufrufe aus dem Veranstaltungsnetz werden abgewiesen.
    if request.remote_addr not in ("127.0.0.1", "::1"):
        return jsonify({
            "status": "error",
            "message": "API-Zugriff ist nur lokal erlaubt."
        }), 403

    if API_protection:
        auth = request.authorization
        if not check_auth(auth):
            return authenticate()

    func = request.args.get('Function', '').lower()

    mapping = {
        'bild1': {"type": "slideshow", "src": "bild1/"},
        'bild2': {"type": "slideshow", "src": "bild2/"},
        'bild3': {"type": "slideshow", "src": "bild3/"},
        'bild4': {"type": "slideshow", "src": "bild4/"},
        'bild5': {"type": "slideshow", "src": "bild5/"},
        'bild6': {"type": "slideshow", "src": "bild6/"},
        'bild7': {"type": "slideshow", "src": "bild7/"},
        'bild8': {"type": "slideshow", "src": "bild8/"},
        'bild9': {"type": "slideshow", "src": "bild9/"},
        'bild10': {"type": "slideshow", "src": "bild10/"},
        'video1': {"type": "video", "src": "videos/video1.mp4"},
        'video2': {"type": "video", "src": "videos/video2.mp4"},
        'video3': {"type": "video", "src": "videos/video3.mp4"},
        'video4': {"type": "video", "src": "videos/video4.mp4"},
        'video5': {"type": "video", "src": "videos/video5.mp4"},
        'video6': {"type": "video", "src": "videos/video6.mp4"},
        'video7': {"type": "video", "src": "videos/video7.mp4"},
        'video8': {"type": "video", "src": "videos/video8.mp4"},
        'video9': {"type": "video", "src": "videos/video9.mp4"},
        'video10': {"type": "video", "src": "videos/video10.mp4"},
        'playlist': {"type": "playlist"},
        'reset': DEFAULT_PROGRAM
    }

    if func not in mapping:
        return jsonify({"status": "error", "message": f"Unbekannte Funktion: {func}"}), 400

    media = mapping[func]
    media_available, error_message = check_media_available(media)
    if not media_available:
        return jsonify({"status": "error", "message": error_message}), 404

    global current_media
    current_media = media

    try:
        socketio.emit('show_media', media)
    except Exception as e:
        print("Fehler bei socketio.emit:", e)
        return jsonify({"status": "error", "message": str(e)}), 500

    return jsonify({"status": "ok", "selected": media})


# ─── WebSocket-Events ───────────────────────────────────────────────
@socketio.on('connect')
def on_connect():
    # Wird beim Aufbau einer Socket-Verbindung aufgerufen (z.B. von Remote oder Präsentation)
    if not getattr(on_connect, 'scheduler_started', False):
        socketio.start_background_task(schedule_thread)
        on_connect.scheduler_started = True

    # Vorläufiger Status bei Verbindung
    emit('presentation_status', {
        'active': bool(presentation_clients),
        'remote': bool(remote_clients)
    }, broadcast=True)

@socketio.on('register_presentation')
def handle_register_presentation():
    presentation_clients.add(request.sid)
    emit('presentation_status', {
        'active': True,
        'remote': bool(remote_clients)
    }, broadcast=True)
    emit('show_media', current_media)
    emit('server_info', {
        'ip': get_server_ip(),
        'port': port,   # <--- Port mitsenden
        'started_at': APP_START_TS
    })


@socketio.on('register_remote')
def handle_register_remote():
    # Remote-Client explizit registrieren
    remote_clients.add(request.sid)
    emit('presentation_status', {
        'active': bool(presentation_clients),
        'remote': True
    }, broadcast=True)
    emit('show_media', current_media)

@socketio.on('register_streamdeck')
def handle_register_streamdeck():
    remote_clients.add(request.sid)
    emit('show_media', current_media)  # aktuellen Medienstatus sofort an das StreamDeck senden
    emit('set_streamdeck_input_lock', STREAMDECK_INPUT_LOCK, room=request.sid)

@socketio.on('disconnect')
def on_disconnect():
    # Client-Verbindung wurde getrennt
    presentation_clients.discard(request.sid)
    remote_clients.discard(request.sid)
    emit('presentation_status', {
        'active': bool(presentation_clients),
        'remote': bool(remote_clients)
    }, broadcast=True)

@socketio.on('show_media')
def handle_show_media(data):
    # Neues Medium wird angefordert (von Remote oder Scheduler)
    global current_media
    valid_types = {"image", "video", "slideshow", "playlist"}
    if "type" not in data or data["type"] not in valid_types:
        print("Ungültiger Medientyp:", data)
        return
    media_available, error_message = check_media_available(data)
    if not media_available:
        print(error_message)
        emit('media_error', {'message': error_message})
        return
    current_media = data
    emit('show_media', data, broadcast=True, include_self=True)

@socketio.on('slideshow_image')
def handle_slideshow_image(data):
    # Wird vom Präsentationsbildschirm gesendet, um dem Remote aktuelle Slideshow-Bilder zu zeigen
    emit('slideshow_image', data, broadcast=True, include_self=True)

@socketio.on('get_status')
def handle_get_status():
    # Remote fragt den aktuellen Medienstatus ab
    emit('show_media', current_media)

@socketio.on('get_presentation_status')
def handle_get_presentation_status():
    # Remote oder Admin fragt Verbindungsstatus ab
    emit('presentation_status', {
        'active': bool(presentation_clients),
        'remote': bool(remote_clients)
    })

@socketio.on('set_schedule_program')
def handle_set_schedule_program(data):
    # Remote kann das geplante Standard-Medium (z.B. Sponsorvideo) ändern
    global SCHEDULE_PROGRAM
    SCHEDULE_PROGRAM = data
    emit('schedule_program', SCHEDULE_PROGRAM, broadcast=True)

@socketio.on('get_schedule_program')
def handle_get_schedule_program():
    # Remote fragt den aktuell geplanten Medieninhalt ab
    emit('schedule_program', SCHEDULE_PROGRAM)

@socketio.on('get_schedule_time')
def handle_get_schedule_time():
    emit('schedule_time', SCHEDULE_TIME)

@socketio.on('heartbeat_request')
def handle_heartbeat():
    emit('heartbeat_response', {'timestamp': int(time.time())})

@socketio.on('play_default_program')
def handle_play_default_program():
    global current_media
    current_media = DEFAULT_PROGRAM
    emit('show_media', DEFAULT_PROGRAM, broadcast=True)

@socketio.on('set_streamdeck_input_lock')
def handle_lock_change(data):
    global STREAMDECK_INPUT_LOCK
    STREAMDECK_INPUT_LOCK = bool(data.get("locked", True))
    print(f"StreamDeck Lock ist  {'AKTIVIERT' if STREAMDECK_INPUT_LOCK else 'DEAKTIVIERT'}")
    socketio.emit("set_streamdeck_input_lock", STREAMDECK_INPUT_LOCK)

@socketio.on('get_streamdeck_input_lock')
def handle_get_streamdeck_input_lock():
    emit("set_streamdeck_input_lock", STREAMDECK_INPUT_LOCK)


# ─── Button-Thread ───────────────────────────────────────────────

@app.route('/button_config', methods=['GET'])
def get_button_config():
    if not os.path.exists(CONFIG_PATH):
        return jsonify({"buttons": []})
    with open(CONFIG_PATH, 'r') as f:
        config = json.load(f)
    for button in config.get("buttons", []):
        available, message = check_media_available(button)
        button["media_available"] = available
        button["media_message"] = message
    return jsonify(config)

@app.route('/button_config', methods=['POST'])
def save_button_config():
    data = request.get_json()
    if not isinstance(data, dict) or 'buttons' not in data:
        return jsonify({"status": "error", "message": "Invalid format"}), 400
    backup_button_config()
    temp_path = CONFIG_PATH + ".tmp"
    with open(temp_path, 'w') as f:
        json.dump(data, f, indent=2)
        f.flush()
        os.fsync(f.fileno())
    os.replace(temp_path, CONFIG_PATH)
    return jsonify({"status": "ok"})

@socketio.on('reload_clients')
def handle_reload_clients():
    emit('reload_config', broadcast=True)


# ─── Scheduler-Thread ───────────────────────────────────────────────
# Falls es zu einer Evakuierung kommt, sorgt der Scheduler dafür,
# dass am nächsten Morgen die Evakuierungsanzeige nicht mehr läuft.
# bis dahin sollten die Probleme behoben sein :-)
# oder einfach um nach dem Event immer wieder mit Sponsorvideo zu starten.

# In the event of an evacuation, the scheduler ensures
# that the evacuation display is no longer running the next morning.
# By then, the problems should have been resolved :-)
# Or simply to start again with the sponsor video after the event.

def schedule_thread():
    """
    Scheduler, führt jeden Tag zur definierten Uhrzeit (SCHEDULE_TIME) ein bestimmtes Medium aus.
    """
    while True:
        now = datetime.now()
        schedule_hour, schedule_minute = map(int, SCHEDULE_TIME.split(":"))
        next_run = datetime.combine(now.date(), dtime(schedule_hour, schedule_minute))
        if next_run <= now:
            next_run += timedelta(days=1)
        seconds = (next_run - now).total_seconds()
        socketio.sleep(seconds)

        global current_media
        current_media = SCHEDULE_PROGRAM
        socketio.emit('show_media', SCHEDULE_PROGRAM)

# ───────────────────────────────────────────────────────
# Definiert Netzwerke und Port (0.0.0.0 = alle verfügbaren Netzwerkadapter)
if __name__ == '__main__':
    # nur ein Thread für den Refresh
    threading.Thread(target=refresh_terminal_every_10s, daemon=True).start()
    socketio.run(app, host="0.0.0.0", port=port)


# ─── Presentation System v1.4.7 · July 2026 · Peter Aellig
# ─── Changelog
# 1.3.0 Playlist-Loop ergänzt
# 1.3.1 Fehler im Playlist-Loop behoben
# 1.3.2 Passwortabfrage an/abschaltbar
# 1.3.3 Zeitgesteuerter Start-Slide
# 1.3.4 Remote zeigt aktuellen Zustand bei Connect
# 1.3.5 Standardprogramm: Slideshow oder Einzelbild; kommentiert
# 1.3.6 Admin panel hinzugefügt
# 1.3.9 API hinzugefügt
# 1.4.0 Streamdeck hinzugefügt
# 1.4.1 div. kosmetische Anpassungen Streamdeck
# 1.4.2 Button und Contentbelegung in Admin Panel hinzugefügt.
# 1.4.3 IP anzeige beim Start
# 1.4.4 vermeide Doppelstart, cleanup terminal window
# 1.4.5 ip&port anzeige in IP badge, presentation html
# 1.4.6 added Streamdeck keypress-lock
# 1.4.7 improved installer, media validation, backups and StreamDeck reliability
