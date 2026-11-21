#Kiosk 1.4.6

import os
import posixpath
import sys
import socket
import json
import time
import ipaddress
import threading
import tempfile
import re
import secrets
import atexit
import subprocess

import psutil   

from datetime import datetime, time as dtime, timedelta
from flask import Flask, jsonify, request, Response, abort, send_from_directory
from flask import session, redirect
import kiosk_preferences
from werkzeug.security import generate_password_hash, check_password_hash
from kiosk_runtime import PORT, instance_lock, ready_status
from admin_access import admin_access, require_admin, signed_in
from flask_socketio import SocketIO, emit, join_room

last_net = psutil.net_io_counters()


# ─── Port festlegen ─────────────────────────────────────────────
port = PORT

# ─── Schutz vor doppeltem Start ────────────────────────────────
def is_port_in_use(p):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        return s.connect_ex(("127.0.0.1", p)) == 0

# ───----------------------------------------------------------------


# ─── bereistellen der lokalen IP ,falls der Computer nicht an einem DHCP Netzwerk hängt, kommt nur localhost zurück ───────────────────────────────────────────────

APP_START_TS = time.time()

def print_terminal_header(ip, port, ips):
    print("\n-------------------------------------------------------------")
    print("\n          Presentation System Kiosk 2.0 Peter Aellig          ")
    print("\n-------------------------------------------------------------")
    print("\n             You can access the display at:                  ")
    print(f"   Presentation:  http://{ip}:{port}")
    print(f"   Admin:         http://{ip}:{port}/admin")
    print(f"   Remote:        http://{ip}:{port}/remote")
    print("   (Admin login required on this computer and on LAN clients)")
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

ips = get_local_ipv4_addresses()
ip = ips[0]

print_terminal_header(ip, port, ips)

# ─── Konfiguration ───────────────────────────────────────────────
# Admin-Zugang wird separat in admin_access.py verwaltet.
password_protection = True  # Setze auf False, wenn kein Passwort benötigt wird (z.B. bei internem Netzwerk wie ZeroTier)
API_protection = False  # Setze auf False, wenn kein Passwort benötigt wird (z.B. bei internem Netzwerk wie ZeroTier)
USERNAME = "admin"
PASSWORD = "password123"

# Standard-Medium beim Serverstart (z.B. Sponsorclip oder Startbild)
DEFAULT_PROGRAM = {"type": "video", "src": "videos/video1.mp4"}

# Zeitgesteuerter Reset auf ein definiertes Medium (z.B. nachts)
SCHEDULE_TIME = "02:00"  # Uhrzeit im Format HH:MM
SCHEDULE_PROGRAM = {"type": "video", "src": "videos/video1.mp4"}  # Medium, das dann gezeigt wird
# ──────────────────────────────────────────────────────────

preferences = kiosk_preferences.load()
remote_credentials = preferences.get("remote", {})
schedule = preferences.get("schedule", {"enabled": True, "time": SCHEDULE_TIME, "target": "default"})


schedule_changed_at = datetime.now()


def remote_authorized():
    return bool(session.get("remote_operator") and
                session.get("remote_version") == remote_credentials.get("version"))


# Flask- und SocketIO-Initialisierung
app = Flask(__name__, static_folder="static")
app.secret_key = secrets.token_bytes(32)
app.config.update(SESSION_COOKIE_NAME="kiosk3_session", SESSION_COOKIE_HTTPONLY=True,
                  SESSION_COOKIE_SAMESITE="Lax")
app.register_blueprint(admin_access)
VIEWS = os.path.join(app.root_path, "views")


@app.after_request
def private_pages(response):
    if request.path.startswith(("/admin", "/remote")) or request.endpoint in {
        "api_settings", "omt_settings", "browse", "save_button_config"
    }:
        response.headers["Cache-Control"] = "no-store"
    return response


socketio = SocketIO(app, manage_session=False)

CONFIG_PATH = os.path.join(os.path.dirname(__file__), 'button_config.json')

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
        { "enabled": False, "label": "Video4",               "type": "video",     "src": "videos/video4.mp4" },
        { "enabled": False, "label": "Video5",               "type": "video",     "src": "videos/video5.mp4" },
        { "enabled": False, "label": "Playlist",             "type": "playlist",  "src": "" }
    ]
}

def write_json(path, data):
    tmp = tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=os.path.dirname(path),
                                      delete=False)
    try:
        with tmp:
            json.dump(data, tmp, indent=2)
        os.replace(tmp.name, path)
    finally:
        if os.path.exists(tmp.name):
            os.unlink(tmp.name)


def valid_button_config(data):
    return (isinstance(data, dict) and isinstance(data.get("buttons"), list)
            and all(isinstance(b, dict) and isinstance(b.get("enabled"), bool)
                    and isinstance(b.get("label"), str)
                    and b.get("type") in ("video", "slideshow", "image", "playlist")
                    and isinstance(b.get("src", ""), str) for b in data["buttons"]))


def ensure_button_config_exists():
    if not os.path.exists(CONFIG_PATH):
        write_json(CONFIG_PATH, STANDARD_BUTTON_CONFIG)
        print("Standard button_config.json wurde erstellt.")


ensure_button_config_exists()

# Globaler Zustand
DEFAULT_SLIDE_SECONDS = 3
slide_seconds = preferences.get("slide_seconds", {})
slide_settings_lock = threading.Lock()


def folder_key(src):
    return posixpath.normpath(src.replace("\\", "/")).lower()


def playback_media(media, revision):
    result = dict(media, revision=revision)
    if result["type"] == "slideshow":
        src = result.get("src", "")
        if isinstance(src, str):
            result["seconds"] = slide_seconds.get(folder_key(src), DEFAULT_SLIDE_SECONDS)
    return result


revision = 0
current_media = playback_media(DEFAULT_PROGRAM, revision)
STREAMDECK_INPUT_LOCK = True  # StreamDeck beim Start gesperrt (wie in app_streamdeck.py)

# "Lokal" = Anfrage kommt von diesem Computer, auch wenn er über seine eigene LAN-IP aufgerufen wird
def _all_own_addresses():
    found = {"127.0.0.1", "::1"}
    for addrs in psutil.net_if_addrs().values():
        for a in addrs:
            if a.family in (socket.AF_INET, socket.AF_INET6):
                found.add(a.address.split("%")[0])
    found.update(ips)
    return found | {"::ffff:" + a for a in found}

LOCAL_ADDRS = _all_own_addresses()

outputs = {}  # Output state by Socket.IO connection
remote_clients = set()        # Verbindet registrierte Remote-Clients

# ─── HTTP-Routen ────────────────────────────────────────────────
@app.get("/_kiosk/ready")
def kiosk_ready():
    if not ipaddress.ip_address(request.remote_addr).is_loopback:
        abort(403)
    return jsonify(service="Kiosk3", pid=os.getpid(), screen=output_status()["active"])


@app.route('/')
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
    if password_protection or remote_credentials:
        auth = request.authorization
        if remote_credentials:
            valid = (auth and auth.username == USERNAME and isinstance(auth.password, str) and
                     check_password_hash(remote_credentials["hash"], auth.password))
        else:
            valid = check_auth(auth)
        if not valid:
            return authenticate()
    session['remote_operator'] = True
    session['remote_version'] = remote_credentials.get('version')
    return send_from_directory(VIEWS, 'remote.html')

@app.route('/admin')
def admin_panel():
    if not signed_in():
        return redirect("/admin/login")
    return send_from_directory(VIEWS, 'admin.html')

@app.route('/remote.html')
def remote_alias():
    return remote()

@app.route('/filelist')
def filelist():
    # Gibt eine Liste gültiger Medien-Dateien im gewünschten Unterordner zurück
    folder = request.args.get('folder', '')
    safe_folder = os.path.normpath(folder)
    full_path = os.path.abspath(os.path.join(app.static_folder, safe_folder))
    static_root = os.path.abspath(app.static_folder)
    if os.path.isabs(safe_folder) or not (full_path == static_root or full_path.startswith(static_root + os.sep)):
        abort(400, description="Ungültiger Ordner")

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

@app.route('/browse')
def browse():
    # Ordner-/Dateiliste unterhalb von static/ für das Auswahlfenster im Admin (nur localhost)
    require_admin()
    rel = request.args.get('path', '').replace('\\', '/').strip('/')
    static_root = os.path.abspath(app.static_folder)
    full = os.path.abspath(os.path.join(static_root, rel))
    if not (full == static_root or full.startswith(static_root + os.sep)) or not os.path.isdir(full):
        abort(400, description="Ungültiger Pfad")
    media_ext = ('.jpg', '.jpeg', '.png', '.gif', '.bmp', '.mp4', '.webm', '.mov')
    dirs, files = [], []
    for name in sorted(os.listdir(full), key=str.lower):
        p = os.path.join(full, name)
        if os.path.isdir(p):
            dirs.append(name)
        elif name.lower().endswith(media_ext):
            files.append(name)
    return jsonify({"path": rel, "dirs": dirs, "files": files})

# ─── API-Zugriff (Schalter + erlaubte IPs, im Admin einstellbar) ──────
API_SETTINGS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'api_settings.json')
API_SETTINGS = {"enabled": True, "restrict": False, "allowed": []}

api_settings_lock = threading.Lock()

def load_api_settings():
    try:
        with open(API_SETTINGS_PATH, 'r') as f:
            data = json.load(f)
        API_SETTINGS["enabled"] = bool(data.get("enabled", True))
        API_SETTINGS["restrict"] = bool(data.get("restrict", False))
        API_SETTINGS["allowed"] = [str(x) for x in data.get("allowed", [])]
    except FileNotFoundError:
        pass
    except Exception as e:
        print("api_settings.json unlesbar, Standard wird verwendet:", e)

load_api_settings()

def client_ip_allowed(addr):
    if addr in LOCAL_ADDRS:
        return True
    try:
        client = ipaddress.ip_address(addr.replace("::ffff:", ""))
    except ValueError:
        return False
    for entry in API_SETTINGS["allowed"]:
        try:
            if client in ipaddress.ip_network(entry, strict=False):
                return True
        except ValueError:
            continue
    return False

# ─── OMT output (Presenter as OMT video + audio, started and stopped from the admin page) ──
OMT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'omt')
OMT_EXE = os.path.join(OMT_DIR, 'PresenterOmt.exe')
OMT_SETTINGS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'omt_settings.json')
OMT_FORMATS = ["1080i50", "1080i59.94", "1080i60", "1080p50", "1080p59.94", "1080p60", "720p50", "720p59.94", "720p60"]
OMT_SETTINGS = {"enabled": False, "name": "Kiosk Presenter", "format": "1080i50", "gain_db": -5.0}
omt_proc = None
omt_status = {"fps": 0, "connections": 0, "message": ""}
omt_lock = threading.Lock()

def load_omt_settings():
    try:
        with open(OMT_SETTINGS_PATH, 'r') as f:
            data = json.load(f)
        OMT_SETTINGS["enabled"] = bool(data.get("enabled", False))
        OMT_SETTINGS["name"] = str(data.get("name", OMT_SETTINGS["name"]))[:60] or "Kiosk Presenter"
        if data.get("format") in OMT_FORMATS:
            OMT_SETTINGS["format"] = data["format"]
        OMT_SETTINGS["gain_db"] = max(-60.0, min(12.0, float(data.get("gain_db", -5.0))))
    except FileNotFoundError:
        pass
    except Exception as e:
        print("omt_settings.json unlesbar, Standard wird verwendet:", e)

load_omt_settings()

def _omt_reader(proc):
    # Reads the console output of PresenterOmt: "50 fps ... connections 2 ..." once per second
    for line in proc.stdout:
        line = line.strip()
        m = re.match(r'^(\d+) fps .*connections (\d+)', line)
        with omt_lock:
            if m:
                omt_status["fps"] = int(m.group(1))
                omt_status["connections"] = int(m.group(2))
            elif line:
                omt_status["message"] = line[:200]
    with omt_lock:
        omt_status["fps"] = 0
        omt_status["connections"] = 0

def omt_running():
    return omt_proc is not None and omt_proc.poll() is None

def stop_omt():
    global omt_proc
    proc = omt_proc
    omt_proc = None
    if proc is not None and proc.poll() is None:
        proc.terminate()
        try:
            proc.wait(timeout=3)
        except Exception:
            proc.kill()
    with omt_lock:
        omt_status["fps"] = 0
        omt_status["connections"] = 0

def start_omt():
    global omt_proc
    stop_omt()
    if not os.path.isfile(OMT_EXE):
        with omt_lock:
            omt_status["message"] = "PresenterOmt.exe not found in the omt folder"
        return
    # omt=1: the presenter page hides the server-IP badge for this client
    args = [OMT_EXE, "--name", OMT_SETTINGS["name"], "--format", OMT_SETTINGS["format"],
            "--gain-db", str(OMT_SETTINGS["gain_db"]), "--url", f"http://127.0.0.1:{port}/?omt=1",
            "--parent-pid", str(os.getpid())]
    flags = 0x08000000 if os.name == 'nt' else 0   # CREATE_NO_WINDOW
    omt_proc = subprocess.Popen(args, cwd=OMT_DIR, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding='utf-8', errors='replace', creationflags=flags)
    with omt_lock:
        omt_status["message"] = "starting"
    threading.Thread(target=_omt_reader, args=(omt_proc,), daemon=True).start()

omt_control = threading.Lock()


def restore_omt_when_ready():
    while True:
        status = ready_status()
        if status and status.get("pid") == os.getpid():
            break
        time.sleep(.25)
    with omt_control:
        if OMT_SETTINGS["enabled"] and not omt_running():
            try:
                start_omt()
            except OSError as error:
                with omt_lock:
                    omt_status["message"] = str(error)


atexit.register(stop_omt)

@app.route('/omt_settings', methods=['GET', 'POST'])
def omt_settings():
    require_admin()
    if request.method == 'POST':
        data = request.get_json(silent=True)
        if not isinstance(data, dict):
            return jsonify({"status": "error", "message": "Invalid format"}), 400
        if data.get("format") not in OMT_FORMATS:
            return jsonify({"status": "error", "message": "Unknown format"}), 400
        name = str(data.get("name", "")).strip()[:60]
        if not name:
            return jsonify({"status": "error", "message": "Name must not be empty"}), 400
        try:
            gain = max(-60.0, min(12.0, float(data.get("gain_db", -5.0))))
        except (TypeError, ValueError):
            return jsonify({"status": "error", "message": "Invalid audio level"}), 400
        settings = {"enabled": bool(data.get("enabled", False)), "name": name,
                    "format": data["format"], "gain_db": gain}
        with omt_control:
            write_json(OMT_SETTINGS_PATH, settings)
            OMT_SETTINGS.update(settings)
            if OMT_SETTINGS["enabled"]:
                start_omt()      # new settings take effect with a restart of the output
            else:
                stop_omt()
    with omt_lock:
        status = dict(omt_status)
    status["running"] = omt_running()
    status["exe_found"] = os.path.isfile(OMT_EXE)
    return jsonify({"settings": OMT_SETTINGS, "formats": OMT_FORMATS, "status": status})

@app.route('/licence/<path:name>')
def licence_file(name):
    # License texts of third-party components (shown/linked on the admin page)
    return send_from_directory(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'licence'), name,
                               mimetype='text/plain')

@app.route('/api_settings', methods=['GET', 'POST'])
def api_settings():
    require_admin()
    if request.method == 'GET':
        return jsonify(API_SETTINGS)
    data = request.get_json(silent=True)
    if not isinstance(data, dict):
        return jsonify({"status": "error", "message": "Invalid format"}), 400
    if not isinstance(data.get("allowed", []), list):
        return jsonify({"status": "error", "message": "Expected a list of IP ranges"}), 400
    allowed = []
    for entry in data.get("allowed", []):
        entry = str(entry).strip()
        if not entry:
            continue
        try:
            ipaddress.ip_network(entry, strict=False)
        except ValueError:
            return jsonify({"status": "error", "message": f"Invalid IP or range: {entry}"}), 400
        allowed.append(entry)
    settings = {"enabled": bool(data.get("enabled", True)),
                "restrict": bool(data.get("restrict", False)), "allowed": allowed}
    with api_settings_lock:
        write_json(API_SETTINGS_PATH, settings)
        API_SETTINGS.update(settings)
    return jsonify({"status": "ok"})

@app.route('/api', methods=['GET', 'POST'])
def api_control():
    if not API_SETTINGS["enabled"]:
        return jsonify({"status": "error", "message": "API is switched off"}), 403
    if API_SETTINGS["restrict"] and not client_ip_allowed(request.remote_addr):
        return jsonify({"status": "error", "message": "IP not allowed"}), 403
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

    if "button" in request.args:
        if request.method != "POST":
            abort(405)
        if not STREAMDECK_INPUT_LOCK and request.remote_addr in {"127.0.0.1", "::1"}:
            return select_button(request.args["button"])
        return jsonify({"status": "error", "message": "Stream Deck unavailable or locked"}), 403
    if func not in mapping:
        return jsonify({"status": "error", "message": f"Unbekannte Funktion: {func}"}), 400
    media = mapping[func]
    switch_media(media)
    return jsonify({"status": "ok", "selected": media})


button_config_lock = threading.Lock()


def configured_buttons():
    try:
        with button_config_lock, open(CONFIG_PATH, encoding="utf-8-sig") as config:
            data = json.load(config)
        if not valid_button_config(data):
            raise ValueError("Ungültige Tastenbelegung")
    except (FileNotFoundError, ValueError) as error:
        print(f"Tastenbelegung: {error}. Standardbelegung wird verwendet.")
        data = STANDARD_BUTTON_CONFIG
    buttons = data["buttons"]
    # The admin edits fixed slots. Disabling an earlier slot never renumbers its ID.
    return [dict(button, id=f"slot-{index}") for index, button in enumerate(buttons, 1)]


def select_button(button_id):
    button = next((b for b in configured_buttons() if b["id"] == button_id and b.get("enabled")), None)
    if button is None:
        return {"status": "error", "message": "Button is disabled or no longer exists"}, 409
    media = {"type": button["type"], "src": button.get("src", ""), "button_id": button_id}
    switch_media(media)
    return {"status": "ok", "selected": media}, 200


@socketio.on("select_button")
def handle_select_button(button_id):
    if not (signed_in() or remote_authorized()):
        return {"status": "error", "message": "Remote login required"}
    result, _ = select_button(button_id)
    return result


def output_status():
    def output(role):
        return next(({k: v for k, v in state.items() if k != "previous"}
                     for state in outputs.values() if state["role"] == role), None)
    screen = output("screen")
    omt = output("omt")
    return {"active": screen is not None, "omt": omt is not None, "remote": bool(remote_clients),
            "screen": screen, "omt_output": omt}


def publish_status():
    socketio.emit("presentation_status", output_status())


def switch_media(media):
    global current_media, revision
    revision += 1
    current_media = playback_media(media, revision)
    for state in outputs.values():
        state.update(phase="loading", message="")
    publish_status()
    socketio.emit("show_media", current_media, to="outputs")


@socketio.on("register_presentation")
def handle_register_presentation(role="screen"):
    if role not in ("screen", "omt"):
        return
    if role == "screen" and request.remote_addr not in LOCAL_ADDRS:
        role = "viewer"
    join_room("outputs")
    outputs[request.sid] = {"role": role, "phase": "loading", "media": None, "file": "", "message": ""}
    publish_status()
    emit("show_media", current_media)
    emit("server_info", {"ip": get_server_ip(), "port": port, "started_at": APP_START_TS})


@socketio.on("playback_report")
def handle_playback_report(data):
    state = outputs.get(request.sid)
    if state is None or not isinstance(data, dict):
        return
    phase = data.get("phase")
    if data.get("revision") != revision:
        # The previous slideshow can still advance while a replacement loads or fails.
        previous = state["media"]
        if phase == "playing" and previous and data.get("revision") == previous["revision"]:
            state["file"] = str(data.get("file", ""))[:1024]
            publish_status()
        return
    if phase == "restored":
        previous = state.get("previous")
        if not previous or data.get("restored") != previous["revision"]:
            return
        state.update(phase="error", media=previous, file=str(data.get("file", ""))[:1024],
                     message=str(data.get("message", "Playback interrupted"))[:512])
    elif phase == "playing":
        if state["media"] != current_media:
            state["previous"] = state["media"]
        state.update(phase=phase, media=current_media.copy(), file=str(data.get("file", ""))[:1024], message="")
    elif phase == "error":
        state.update(phase=phase, message=str(data.get("message", "Media error"))[:512])
    else:
        return
    publish_status()


@socketio.on("register_remote")
def handle_register_remote():
    authorized = bool(remote_authorized() or signed_in())
    if authorized:
        remote_clients.add(request.sid)
        publish_status()
    return {"authorized": authorized}


@socketio.on("register_streamdeck")
def handle_register_streamdeck():
    emit("presentation_status", output_status())
    emit("set_streamdeck_input_lock", STREAMDECK_INPUT_LOCK)
    emit("deck_layout", configured_buttons())


@socketio.on("disconnect")
def on_disconnect(reason=None):
    outputs.pop(request.sid, None)
    remote_clients.discard(request.sid)
    publish_status()


@socketio.on("show_media")
def handle_show_media(data):
    if not (signed_in() or remote_authorized()):
        return
    if isinstance(data, dict) and data.get("type") in {"image", "video", "slideshow", "playlist"}:
        switch_media({"type": data["type"], "src": data.get("src", "")})


@socketio.on("get_status")
@socketio.on("get_presentation_status")
def handle_get_presentation_status():
    emit("presentation_status", output_status())


def scheduled_media():
    target = schedule["target"]
    if target == "default":
        return SCHEDULE_PROGRAM
    if target == "custom":
        return schedule["media"]
    for button in configured_buttons():
        if button["id"] == target and button.get("enabled") and button["type"] in {"video", "playlist"}:
            return {"type": button["type"], "src": button.get("src", ""), "button_id": target}
    return None


def save_schedule(settings):
    global schedule, schedule_changed_at
    kiosk_preferences.save("schedule", settings)
    schedule_changed_at = datetime.now()
    schedule = settings
    socketio.emit("schedule_time", schedule["time"] if schedule["enabled"] else "Off")
    socketio.emit("schedule_program", scheduled_media() or {})


@app.route("/admin/slide-times", methods=["GET", "POST"])
def slide_times():
    global slide_seconds
    require_admin()
    folders = {f"bild{i}" for i in range(1, 11)}
    folders.update(folder_key(b["src"]) for b in configured_buttons()
                   if b["type"] == "slideshow" and b.get("src"))
    scheduled = scheduled_media()
    if scheduled and scheduled["type"] == "slideshow" and scheduled.get("src"):
        folders.add(folder_key(scheduled["src"]))
    with slide_settings_lock:
        folders.update(slide_seconds)
        if request.method == "POST":
            data = request.get_json(silent=True)
            if not isinstance(data, dict) or not isinstance(data.get("folder"), str):
                return jsonify(message="Select a slideshow folder."), 400
            folder = folder_key(data["folder"])
            seconds = data.get("seconds")
            if folder not in folders or type(seconds) is not int or not 1 <= seconds <= 10:
                return jsonify(message="Select a folder and enter 1 to 10 whole seconds."), 400
            updated = {**slide_seconds, folder: seconds}
            kiosk_preferences.save("slide_seconds", updated)
            slide_seconds = updated
        return jsonify({f: slide_seconds.get(f, DEFAULT_SLIDE_SECONDS) for f in sorted(folders)})


@app.post("/admin/remote-password")
def change_remote_password():
    global remote_credentials
    require_admin()
    data = request.get_json(silent=True)
    password = data.get("password") if isinstance(data, dict) else None
    if not isinstance(password, str) or not password.strip() or not 8 <= len(password) <= 256:
        return jsonify(message="Enter 8 to 256 characters."), 400
    credentials = {"hash": generate_password_hash(password), "version": secrets.token_hex(16)}
    kiosk_preferences.save("remote", credentials)
    remote_credentials = credentials
    socketio.emit("remote_credentials_changed")
    return jsonify(status="ok")


@app.route("/admin/schedule", methods=["GET", "POST"])
def schedule_settings():
    require_admin()
    choices = {"default": "Default program (" + (SCHEDULE_PROGRAM.get("src") or SCHEDULE_PROGRAM["type"]) + ")"}
    for button in configured_buttons():
        if button.get("enabled") and button["type"] in {"video", "playlist"}:
            choices[button["id"]] = button["label"]
    if schedule["target"] == "custom":
        choices["custom"] = "Custom program"
    if request.method == "POST":
        data = request.get_json(silent=True)
        if not isinstance(data, dict) or type(data.get("enabled")) is not bool:
            return jsonify(message="Invalid schedule setting."), 400
        when, target = data.get("time"), data.get("target")
        if not isinstance(when, str) or not re.fullmatch(r"(?:[01][0-9]|2[0-3]):[0-5][0-9]", when):
            return jsonify(message="Enter a valid time."), 400
        if not isinstance(target, str) or target not in choices:
            return jsonify(message="Select an available program."), 400
        settings = {"enabled": data["enabled"], "time": when, "target": target}
        if target == "custom":
            settings["media"] = schedule["media"]
        save_schedule(settings)
    return jsonify(settings=schedule, choices=choices)


@socketio.on('set_schedule_program')
def handle_set_schedule_program(data):
    if signed_in() and isinstance(data, dict) and data.get("type") in {"video", "image", "slideshow", "playlist"}:
        save_schedule(dict(schedule, target="custom", media={"type": data["type"], "src": data.get("src", "")}))


@socketio.on('get_schedule_program')
def handle_get_schedule_program():
    emit('schedule_program', scheduled_media() or {})


@socketio.on('get_schedule_time')
def handle_get_schedule_time():
    emit('schedule_time', schedule["time"] if schedule["enabled"] else "Off")

@socketio.on('heartbeat_request')
def handle_heartbeat():
    emit('heartbeat_response', {'timestamp': int(time.time())})

@socketio.on('play_default_program')
def handle_play_default_program():
    if not signed_in():
        return
    switch_media(DEFAULT_PROGRAM)

deck_lock_guard = threading.Lock()


def update_deck_lock(locked=None):
    global STREAMDECK_INPUT_LOCK
    with deck_lock_guard:
        STREAMDECK_INPUT_LOCK = not STREAMDECK_INPUT_LOCK if locked is None else locked
        socketio.emit("set_streamdeck_input_lock", STREAMDECK_INPUT_LOCK)
        return STREAMDECK_INPUT_LOCK


@app.post("/_kiosk/streamdeck/toggle")
def toggle_deck_lock():
    if request.remote_addr not in {"127.0.0.1", "::1"} or request.headers.get("X-Kiosk-Deck") != "1":
        abort(403)
    return jsonify(locked=update_deck_lock())


@socketio.on('set_streamdeck_input_lock')
def handle_lock_change(data):
    if signed_in() and isinstance(data, dict) and isinstance(data.get("locked"), bool):
        update_deck_lock(data["locked"])


# ─── Button-Thread ───────────────────────────────────────────────

@app.route('/button_config', methods=['GET'])
def get_button_config():
    return jsonify(buttons=configured_buttons())

@app.route('/button_config', methods=['POST'])
def save_button_config():
    require_admin()
    data = request.get_json(silent=True)
    if not valid_button_config(data):
        return jsonify({"status": "error", "message": "Invalid format"}), 400
    with button_config_lock:
        write_json(CONFIG_PATH, data)
    socketio.emit("reload_config")
    return jsonify({"status": "ok"})

@socketio.on('reload_clients')
def handle_reload_clients():
    if not signed_in():
        return
    emit('reload_config', broadcast=True)

@socketio.on('get_streamdeck_input_lock')
def handle_get_lock():
    emit('set_streamdeck_input_lock', STREAMDECK_INPUT_LOCK)


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
    previous = datetime.now()
    previous_settings = schedule
    last_day = None
    while True:
        socketio.sleep(1)
        now = datetime.now()
        settings = schedule
        due = datetime.combine(now.date(), dtime.fromisoformat(settings["time"]))
        if due > now:
            due -= timedelta(days=1)
        since = previous if settings is previous_settings else max(previous, schedule_changed_at)
        if settings["enabled"] and since < due <= now and last_day != due.date():
            last_day = due.date()
            try:
                media = scheduled_media()
                if media:
                    switch_media(media)
                else:
                    print("Zeitplan: Zieltaste ist nicht verfügbar.")
            except (OSError, ValueError, KeyError) as error:
                print(f"Zeitplan: {error}")
        previous, previous_settings = now, settings


# ───────────────────────────────────────────────────────
# Definiert Netzwerke und Port (0.0.0.0 = alle verfügbaren Netzwerkadapter)
def run_server():
    if is_port_in_use(port):
        print(f"[X] app.py läuft bereits (Port {port} ist belegt).")
        sys.exit(1)
    # nur ein Thread für den Refresh
    threading.Thread(target=refresh_terminal_every_10s, daemon=True).start()
    if OMT_SETTINGS["enabled"]:
        threading.Thread(target=restore_omt_when_ready, daemon=True).start()
    socketio.start_background_task(schedule_thread)
    socketio.run(app, host="0.0.0.0", port=port)


if __name__ == "__main__":
    with instance_lock("server") as first:
        if first:
            run_server()



# ─── Presentation System v1.4.6 · July 2025 · Peter Aellig
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