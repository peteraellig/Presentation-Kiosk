# Kiosk3 3.24 · © Peter Aellig ·
# Streamdeck Driver
# The server supplies the same configured slots used by Remote Control.

import time
import requests
import threading
import queue
from kiosk_runtime import SERVER_URL, instance_lock
import socketio
import platform
from PIL import Image, ImageDraw, ImageFont
from StreamDeck.DeviceManager import DeviceManager
from StreamDeck.ImageHelpers import PILHelper

STREAMDECK_INPUT_LOCK = True  # Gesperrt beim Start



print("\n-------------------------------------------------------------")
print("\n     Kiosk3 3.24 · © Peter Aellig · Stream Deck           ")
print("\n-------------------------------------------------------------")


def safe_close_deck():
    global deck
    old, deck = deck, None
    if old is not None:
        try:
            old.reset()
        except Exception:
            pass
        try:
            old.close()
        except Exception as error:
            print(f"Stream Deck close: {error}")


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
    
    global deck
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
        print(f"Buttons geladen: {len(button_mapping)}")
        deck.set_key_callback(make_button_callback(deck))

        # Initialzustand rendern
        refresh_keys()

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



# === Globale Objekte ===
deck = None
update_queue = queue.Queue()
sio = socketio.Client()
button_mapping = {}

# === Socket.IO Events ===
@sio.event
def connect():
    print("Verbunden mit app.py")
    sio.emit("register_streamdeck")

@sio.event
def disconnect(reason=None):
    global confirmed_media, connection_generation
    connection_generation += 1
    on_input_lock(True)
    confirmed_media = None
    refresh_keys()
    print("! Verbindung zu app.py getrennt")

confirmed_media = None


@sio.on("presentation_status")
def on_status(data):
    global confirmed_media
    screen = data.get("screen") or {}
    media = screen.get("media")
    if media != confirmed_media:
        confirmed_media = media
        refresh_keys()


def refresh_keys():
    media = confirmed_media or {}
    for key, button in button_mapping.items():
        same_slot = not media.get("button_id") or button["id"] == media["button_id"]
        matches = same_slot and button["type"] == media.get("type") and (
            button["type"] == "playlist" or button.get("src") == media.get("src"))
        if matches:
            update_queue.put(key)
            return
    update_queue.put(-1)


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

    # Höhe und Breite jeder Zeile
    for line in lines:
        bbox = draw.textbbox((0, 0), line, font=font)
        h = bbox[3] - bbox[1]
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
    global STREAMDECK_INPUT_LOCK, input_generation
    if STREAMDECK_INPUT_LOCK != bool(data):
        input_generation += 1
    STREAMDECK_INPUT_LOCK = bool(data)
    print(f"StreamDeck Input {'gesperrt' if STREAMDECK_INPUT_LOCK else 'freigegeben'}")

    brightness = 15 if STREAMDECK_INPUT_LOCK else 85
    set_streamdeck_brightness(brightness)



def update_key_labels_threadsafe(active_key):
    if deck is None:
        return

    key_count = deck.key_count()
    for key in range(key_count):
        if key in button_mapping:
            label = button_mapping[key]["label"].replace(" ", "\n")
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
        if key is None:
            break  # Shutdown-Signal
        try:
            update_key_labels_threadsafe(key)
        except Exception as error:
            print(f"Stream Deck display: {error}")
        finally:
            update_queue.task_done()


input_generation = 0
connection_generation = 0
presses = queue.Queue()


def send_presses():
    while True:
        device, connection, generation, button_id = presses.get()
        try:
            if device is not deck or connection != connection_generation or not sio.connected:
                continue
            if button_id is None:
                response = requests.post(SERVER_URL + "/_kiosk/streamdeck/toggle",
                                         headers={"X-Kiosk-Deck": "1"}, timeout=2)
            elif generation == input_generation and not STREAMDECK_INPUT_LOCK:
                response = requests.post(SERVER_URL + "/api", params={"button": button_id}, timeout=2)
            else:
                continue
            response.raise_for_status()
        except requests.RequestException as error:
            print(f"Stream Deck command failed: {error}")
        finally:
            presses.task_done()


def make_button_callback(device):
    rows, cols = device.key_layout()
    corners = {0, cols - 1, (rows - 1) * cols, rows * cols - 1} if rows >= 2 and cols >= 2 else set()
    held = set()
    single_press = None
    toggled = False
    gesture_connection = None

    def button_callback(source, key, down):
        nonlocal single_press, toggled, gesture_connection
        if source is not deck:
            return
        button = button_mapping.get(key)
        command = None
        if button and not STREAMDECK_INPUT_LOCK and sio.connected:
            command = (source, connection_generation, input_generation, button["id"])
        if key not in corners:
            if down and command:
                presses.put(command)
            return
        if down and key not in held:
            if not held:
                toggled = False
                gesture_connection = connection_generation if sio.connected else None
                single_press = command
            else:
                single_press = None
            held.add(key)
            if held == corners and not toggled:
                toggled = True
                if sio.connected and gesture_connection == connection_generation:
                    presses.put((source, gesture_connection, input_generation, None))
        elif not down and key in held:
            held.remove(key)
            if not held and single_press:
                presses.put(single_press)
            single_press = None

    return button_callback


# Main
def main():
    # Worker starten (darf auch ohne Deck laufen)
    threading.Thread(target=send_presses, daemon=True).start()
    threading.Thread(target=update_worker, daemon=True).start()

    # Beim Start einmal versuchen, ein Deck zu initialisieren
    if not try_init_deck():
        print("! Kein Stream Deck gefunden – warte auf Anschluss...")

    try:
        while True:
            time.sleep(1)

            if deck is None:
                if try_init_deck():
                    print("Stream Deck verbunden und initialisiert.")
                continue
            try:
                attached = {device.id() for device in DeviceManager().enumerate()}
                if deck.id() in attached:
                    continue
            except Exception as error:
                print(f"Stream Deck status: {error}")
            safe_close_deck()
            print("Stream Deck getrennt – warte auf neues Gerät.")
    except KeyboardInterrupt:
        print("Beende...")
    finally:
        update_queue.put(None)
        safe_close_deck()


# Update 
@sio.on("reload_config")
def on_reload_config():
    sio.emit("register_streamdeck")


@sio.on("deck_layout")
def on_layout(buttons):
    global button_mapping
    button_mapping = {key: button for key, button in enumerate(b for b in buttons if b.get("enabled"))}
    refresh_keys()


def maintain_connection():
    while True:
        try:
            sio.connect(SERVER_URL, wait_timeout=5)
            sio.wait()  # includes Socket.IO's retries after a transport loss
        except (socketio.exceptions.ConnectionError, OSError) as error:
            print(f"Kiosk3-Server nicht erreichbar: {error}")
        time.sleep(2)


if __name__ == "__main__":
    with instance_lock("streamdeck") as first:
        if first:
            threading.Thread(target=maintain_connection, daemon=True).start()
            main()
