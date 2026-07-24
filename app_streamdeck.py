# Presentation System v1.4.7 · July 2026 · Peter Aellig ·
# Streamdeck Driver
# NOTE: if you change the Port Adress in the main application, you have to change it in all applications!
# Streamdeck sends Kiosk-API commands, buttons are generated from the database

import time
import requests
import threading
import queue
import socketio
import json
import os
import platform
import signal
import sys
from PIL import Image, ImageDraw, ImageFont
from StreamDeck.DeviceManager import DeviceManager
from StreamDeck.ImageHelpers import PILHelper

CONFIG_PATH = os.path.join(os.path.dirname(__file__), 'button_config.json')
STREAMDECK_INPUT_LOCK = True  # Gesperrt beim Start

# change PORT HERE AND AT THE END OF THIS FILE
API_BASE_URL = "http://localhost:53100/api?Function="

print("\n-------------------------------------------------------------")
print("\n     Presentation Kiosk Streamdeck Driver 1.4.7 Peter Aellig ")
print("\n-------------------------------------------------------------")


def safe_close_deck():
    global deck
    try:
        if deck:
            deck.reset()
            deck.close()
    except Exception:
        pass
    finally:
        deck = None

def set_streamdeck_brightness(percent):
    global deck
    if deck is not None:
        try:
            deck.set_brightness(percent)
            print(f"Helligkeit {percent}%")
        except Exception as e:
            print(f"Fehler beim setzen der Helligkeit: {e}")


def try_init_deck():
    
    # Versucht ein Stream Deck zu finden und zu initialisieren.
    # Gibt True zurück, wenn initialisiert wurde, sonst False.
    
    global deck, button_mapping
    try:
        decks = DeviceManager().enumerate()
        if not decks:
            return False

        deck = decks[0]
        deck.open()
        deck.reset()
        brightness = 15 if STREAMDECK_INPUT_LOCK else 85
        deck.set_brightness(brightness)


        print(f"Stream Deck erkannt: {deck.key_count()} Tasten")

        # Buttons laden & Callback setzen
        button_mapping = load_button_mapping()
        print(f"Buttons geladen: {len(button_mapping)}")
        deck.set_key_callback(button_callback)

        # Initialzustand rendern
        update_queue.put(-1)

        # Dem Server mitteilen, dass ein Streamdeck "da" ist
        try:
            sio.emit("register_streamdeck")
        except Exception:
            pass

        return True
    except Exception as e:
        print(f"! Initialisierung fehlgeschlagen: {e}")
        safe_close_deck()
        return False



# === Button-Konfiguration: Taste → (API-Funktion, Anzeigetext) ===
# es sind mehrzeilige Labels möglich , ein Abstand im Namen erzeugt einen CRLF"

def load_button_mapping():
    try:
        with open(CONFIG_PATH, 'r') as f:
            config = json.load(f)
    except:
        return {}

    mapping = {}
    physical_key = 0  # Start bei Taste 0

    for btn in config.get("buttons", []):
        if btn.get("enabled", False):
            label = btn.get("label", "").replace(" ", "\n")
            typ = btn.get("type", "")
            src = btn.get("src", "")
            if typ == "slideshow":
                func = src.rstrip("/")
            elif typ == "video":
                func = os.path.splitext(os.path.basename(src))[0]
            elif typ == "playlist":
                func = "playlist"
            else:
                continue
            mapping[physical_key] = (func, label)
            physical_key += 1  # nächste Taste

    return mapping


# === Globale Objekte ===
deck = None
last_active_key = None
update_queue = queue.Queue()
sio = socketio.Client()
button_mapping = {}

# === Socket.IO Events ===
@sio.event
def connect():
    print("Verbunden mit app.py")
    sio.emit("register_streamdeck")

@sio.event
def disconnect():
    print("! Verbindung zu app.py getrennt")

@sio.on("show_media")
def on_show_media(data):

    # reagiere nur, wenn Stream Deck verbunden ist

    if deck is None:
        return

    if not button_mapping:
        return

    src = data.get("src", "")
    typ = data.get("type")

    for key, (api_func, label) in button_mapping.items():
        is_active = (
            (typ == "video" and api_func in src) or
            (typ == "slideshow" and api_func == src.rstrip("/")) or
            (typ == "playlist" and api_func == "playlist") or
            (typ == "reset" and api_func == "reset")
        )

        if is_active:
            update_queue.put(key)
            break

# streamdeckdriver prüft ob es auf einen raspi oder windows läuft und passt die fonts an
def render_key_image(deck, label, bg_color=(0, 0, 0)):
    size = deck.key_image_format()['size']
    image = PILHelper.create_image(deck, background=bg_color)
    draw = ImageDraw.Draw(image)

    try:
        # Arial Bold wird am schönsten
        if platform.system() == "Windows":
            # Bold-Variante: calibrib.ttf, normal: calibri.ttf
            #font = ImageFont.truetype(r"C:\Windows\Fonts\arial.ttf", 15)
            font = ImageFont.truetype(r"C:\Windows\Fonts\arialbd.ttf", 15)
            #font = ImageFont.truetype(r"C:\Windows\Fonts\calibri.ttf", 15)
            #font = ImageFont.truetype(r"C:\Windows\Fonts\calibrib.ttf", 15)
        else:
            # standard font von Raspi, wird am schönsten
            font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 12)
    except OSError:
        font = ImageFont.load_default()


    # Mehrzeilige Labels
    lines = label.split('\n')
    line_heights = []
    max_width = 0

    # Höhe und Breite jeder Zeile
    for line in lines:
        bbox = draw.textbbox((0, 0), line, font=font)
        w = bbox[2] - bbox[0]
        h = bbox[3] - bbox[1]
        max_width = max(max_width, w)
        line_heights.append(h)

    total_height = sum(line_heights) + (len(lines) - 1) * 2  # 2px Zeilenabstand
    y = (size[1] - total_height) // 2

    # jede Zeile zentriert
    for i, line in enumerate(lines):
        bbox = draw.textbbox((0, 0), line, font=font)
        w = bbox[2] - bbox[0]
        h = bbox[3] - bbox[1]
        x = (size[0] - w) // 2
        draw.text((x, y), line, font=font, fill=(255, 255, 255))
        y += h + 2

    return PILHelper.to_native_format(deck, image)

@sio.on("set_streamdeck_input_lock")
def on_input_lock(data):
    global STREAMDECK_INPUT_LOCK
    STREAMDECK_INPUT_LOCK = bool(data)
    print(f"StreamDeck Input {'gesperrt' if STREAMDECK_INPUT_LOCK else 'freigegeben'}")

    brightness = 15 if STREAMDECK_INPUT_LOCK else 85
    set_streamdeck_brightness(brightness)



def update_key_labels_threadsafe(active_key):
    global last_active_key
    if deck is None:
        return
    last_active_key = active_key

    key_count = deck.key_count()
    for key in range(key_count):
        if key in button_mapping:
            api_func, label = button_mapping[key]
            color = (255, 0, 0) if key == active_key else (0, 0, 0)
        else:
            label = ""
            color = (0, 0, 0)
        image = render_key_image(deck, label, bg_color=color)
        deck.set_key_image(key, image)



# === Worker-Thread für Updates ===
def update_worker():
    while True:
        key = update_queue.get()
        try:
            if key is None:
                break  # Shutdown-Signal
            update_key_labels_threadsafe(key)
        except Exception as e:
            print(f"! StreamDeck-Anzeige konnte nicht aktualisiert werden: {e}")
            safe_close_deck()
        finally:
            update_queue.task_done()


# Tastendruck-Callback 
# http wird zuerst geschickt, ohne überprüfung, um die reaktionszeit kleinstmöglich zu halten
def _http_fire_and_forget(url: str, timeout: float = 0.3):
    # nicht-blockierender HTTP-Call mit kurzem Timeout
    def _run():
        try:
            requests.get(url, timeout=timeout)
        except Exception as e:
            print(f"!  API-Fehler: {e}")
    threading.Thread(target=_run, daemon=True).start()

def button_callback(deck_obj, key, state):
    if not state:
        return  # Nur auf Tastendruck reagieren

    if STREAMDECK_INPUT_LOCK:
        print("Eingabe gesperrt – keine Aktion")
        return

    if key in button_mapping:
        api_func, label = button_mapping[key]

        # Nur wenn nicht gesperrt → Button einfärben
        update_queue.put(key)

        # API aufrufen (nur wenn nicht gesperrt)
        url = f"{API_BASE_URL}{api_func}"
        _http_fire_and_forget(url, timeout=0.3)
        print(f"→ {label} (API: {api_func}) aktiviert")



# Main
def main():
    # Worker starten (darf auch ohne Deck laufen)
    threading.Thread(target=update_worker, daemon=True).start()

    # Beim Start einmal versuchen, ein Deck zu initialisieren
    if not try_init_deck():
        print("! Kein Stream Deck gefunden – warte auf Anschluss...")

    try:
        while True:
            time.sleep(1)

            if not sio.connected and getattr(sio.eio, "state", "disconnected") == "disconnected":
                try:
                    sio.connect("http://localhost:53100", wait_timeout=3)
                except Exception:
                    pass

            # Prüfen, ob ein Deck angeschlossen/entfernt wurde
            try:
                decks = DeviceManager().enumerate()
            except Exception as e:
                print(f"! enumerate() Fehler: {e}")
                decks = []

            if deck is None:
                # Noch keins aktiv → versuchen zu initialisieren
                if try_init_deck():
                    print("Stream Deck verbunden und initialisiert.")
            else:
                # Es war eines aktiv – prüfen, ob es verschwunden ist
                try:
                    deck_connected = deck.connected()
                except Exception:
                    deck_connected = False
                if not deck_connected:
                    print("! Stream Deck getrennt – wechsle in Wartemodus.")
                    safe_close_deck()
                    print("! Warte auf neues Stream Deck...")
    except KeyboardInterrupt:
        print("Beende...")
    finally:
        update_queue.put(None)
        safe_close_deck()


# Update 
@sio.on("reload_config")
def on_reload_config():
    global button_mapping
    button_mapping = load_button_mapping()
    update_queue.put(-1)

# Start
if __name__ == "__main__":
    try:
        sio.connect("http://localhost:53100", wait_timeout=3)
    except Exception as e:
        print(f"! Socket.IO noch nicht erreichbar: {e}")
        print("! Verbindung wird automatisch erneut versucht.")
    main()
