# Presentation Kiosk 1.4.7

Ein lokales Alert- und Präsentationssystem für temporäre Events, spontane
Organisationsformen und Ad-hoc-Krisenmanagement.

Das System zeigt Videos, Bilder, Slideshows und Playlists auf einem
Präsentationsbildschirm. Inhalte lassen sich zentral über eine einfache
Weboberfläche oder optional über ein Elgato Stream Deck steuern. Änderungen
werden in Echtzeit an alle verbundenen Clients übertragen.

> Ziel des Projekts ist eine einfache, selbsterklärende und ortsunabhängige
> Bedienung, die möglichst ohne vorherige Schulung funktioniert.

## Installation und Start

Die vollständige englische Installationsanleitung befindet sich unter
[Installer/README.md](Installer/README.md).

Kurzfassung:

1. Den Projektordner nach `C:\kiosk` kopieren.
2. `Installer\Install_Kiosk.cmd` als Administrator starten.
3. Nach abgeschlossener Installation `C:\kiosk\kiosk.bat` ausführen.
4. Das automatisch geöffnete Firefox-Kioskfenster verwenden.

Das Projekt verwendet ausdrücklich **Python 3.12.6** und festgeschriebene,
getestete Paketversionen.

## Oberflächen

### Präsentation

Die Präsentationsoberfläche läuft in Firefox im Vollbild-Kioskmodus. Sie
empfängt Medienwechsel über Socket.IO und zeigt einzelne Bilder, Videos,
Slideshows oder Playlists an.

![Presentation output](docs/images/presentation.png)

Lokale Adressen:

- `http://localhost:53100/`
- `http://localhost:53100/presentation`
- `http://localhost:53100/presentation.html`

### Remote-Steuerung

Die browserbasierte Remote-Oberfläche stellt die aktivierten Medien als
übersichtliche Schaltflächen dar. Änderungen werden sofort auf der Präsentation
ausgeführt. Für Slideshows und Videos wird eine Vorschau angezeigt.

![Remote control](docs/images/remote.png)

Adresse:

```text
http://<KIOSK-IP>:53100/remote
```

Fehlende Medien werden gekennzeichnet und können nicht gestartet werden.
Stattdessen erscheint eine Meldung mit dem erwarteten Datei- oder Ordnerpfad.

### Admin-Dashboard

Das Admin-Dashboard dient zur technischen Kontrolle und Konfiguration. Es zeigt
unter anderem:

- aktuell aktives Medium,
- Verbindungsstatus von Präsentation und Remote,
- Server-Heartbeat,
- geplantes Standardmedium,
- Button-Belegung,
- StreamDeck-Sperrstatus.

![Admin dashboard](docs/images/admin.png)

Adresse:

```text
http://<KIOSK-IP>:53100/admin
```

Fehlende Mediendateien und leere Medienordner werden im Admin-Panel farblich
markiert. Beim Speichern wird die bisherige `button_config.json` automatisch
unter `Backups` gesichert. Die letzten 20 Sicherungen bleiben erhalten.

### Passwortschutz

Remote- und Admin-Oberfläche sind standardmäßig durch Benutzername und Passwort
geschützt. Die Zugangsdaten müssen vor einem produktiven Einsatz in `app.py`
angepasst werden.

![Password prompt](docs/images/password.png)

## Systemarchitektur

Das Kiosk-System ist modular aufgebaut:

```text
Remote / Admin / Stream Deck
             │
             ▼
      Flask + Socket.IO
           app.py
             │
             ▼
    Präsentationsbrowser
```

### Server – `app.py`

Der Python-Server ist das Herzstück des Systems:

- stellt Präsentation, Remote und Admin bereit,
- verwaltet das aktuell aktive Medium,
- synchronisiert Clients in Echtzeit über Socket.IO,
- überwacht Verbindungen per Heartbeat,
- startet zu einer festgelegten Uhrzeit ein Standardmedium,
- prüft, ob konfigurierte Medien vorhanden sind,
- sichert Änderungen an der Button-Konfiguration.

Der Server lauscht standardmäßig auf Port `53100`.

### Präsentationsoberfläche – `static/presentation.html`

- läuft im Browser auf dem Präsentationsrechner,
- startet und stoppt Bilder, Videos, Slideshows und Playlists,
- zeigt beim Start kurz IP-Adresse und Port,
- meldet den aktuellen Präsentationsstatus an den Server.

### Remote-Oberfläche – `static/remote.html`

- lädt aktivierte Buttons dynamisch aus `button_config.json`,
- startet Medien über Socket.IO,
- zeigt Vorschauen und den aktuellen Präsentationsstatus,
- blockiert konfigurierte, aber fehlende Medien.

### Admin-Oberfläche – `static/admin.html`

- zeigt Server- und Clientstatus,
- bearbeitet Button-Belegung, Beschriftung, Typ und Medienpfad,
- markiert fehlende Medien,
- sperrt oder entsperrt die StreamDeck-Bedienung.

### StreamDeck-Treiber – `app_streamdeck.py`

Der separate Python-Prozess bindet ein kompatibles Elgato Stream Deck ein:

- liest die Belegung aus `button_config.json`,
- rendert Beschriftung und Aktivstatus auf den Tasten,
- sendet lokale API-Befehle an den Server,
- verbindet sich nach einem Server- oder USB-Unterbruch automatisch neu,
- bleibt beim Start standardmäßig für Eingaben gesperrt und dient zunächst als
  Statusanzeige.

## Kommunikation

### Socket.IO

Socket.IO übernimmt die Echtzeitkommunikation zwischen Server, Präsentation,
Remote, Admin und StreamDeck-Treiber. Wichtige Ereignisse sind:

- `show_media` – Medium wechseln,
- `slideshow_image` – aktuelles Slideshow-Bild melden,
- `heartbeat_request` / `heartbeat_response` – Verbindung prüfen,
- `reload_config` – Button-Belegung neu laden,
- `set_streamdeck_input_lock` – StreamDeck sperren oder freigeben.

### Lokale HTTP-API

Das Stream Deck verwendet:

```text
http://localhost:53100/api?Function=<FUNKTION>
```

Beispiele für Funktionen sind `video1`, `bild3`, `playlist` und `reset`.

Die API akzeptiert aus Sicherheitsgründen nur Aufrufe vom lokalen
Kiosk-Rechner (`127.0.0.1` oder `::1`). Externe Geräte können diese API nicht
direkt verwenden.

## Medien und Button-Belegung

Alle Präsentationsmedien liegen unter `static`:

| Pfad | Verwendung |
|---|---|
| `static/videos` | einzelne Videos |
| `static/videos_playlist` | fortlaufende Video-Playlist |
| `static/bild1` bis `static/bild10` | Bilder für Meldungen und Slideshows |
| `static/Message Templates` | Vorlagen für neue Meldungsgrafiken |

Die Datei `button_config.json` definiert:

- ob ein Button aktiviert ist,
- seine Beschriftung,
- den Medientyp (`video`, `slideshow` oder `playlist`),
- den zugehörigen Datei- oder Ordnerpfad.

Medien können direkt vor Ort erstellt und in die vorbereiteten Ordner kopiert
werden. Nur die für das jeweilige Event benötigten Einträge müssen im
Admin-Panel aktiviert werden.

## Scheduler

Der Server kann täglich zu einer festgelegten Uhrzeit automatisch ein
Standardmedium starten. Dies eignet sich beispielsweise dafür, nach einem Event
oder einer Alarmmeldung wieder zum Sponsor- beziehungsweise Werbeloop
zurückzukehren.

Die Einstellungen `SCHEDULE_TIME` und `SCHEDULE_PROGRAM` befinden sich in
`app.py`.

## SDI-Ausgabe

Falls eine SDI-Ausgabe benötigt wird, kann das HDMI-Signal des
Präsentationsrechners über einen HDMI-zu-SDI-Wandler in eine bestehende
Videoinfrastruktur eingespeist werden.

Typischer Ablauf unter Windows 11:

1. Präsentationsrechner per HDMI mit dem HDMI-zu-SDI-Wandler verbinden.
2. Unter **Anzeigeeinstellungen** den betreffenden HDMI-Ausgang auswählen.
3. Als Auflösung beispielsweise `1920 × 1080` einstellen.
4. Unter **Erweiterte Anzeige** die Adaptereigenschaften öffnen.
5. Über **Alle Modi auflisten** nach Möglichkeit `1080i, 50 Hz` wählen.

Welche Modi verfügbar sind, hängt von Grafikkarte und Treiber ab. Falls nur
`1080p50` angeboten wird, ist für eine echte `1080i50`-Ausgabe ein geeigneter
Cross-Converter erforderlich.

## Verwendete Technologien

- Python 3.12.6
- Flask
- Flask-SocketIO
- Socket.IO
- Eventlet
- psutil
- Pillow
- StreamDeck SDK
- HIDAPI
- HTML, CSS und JavaScript
- Mozilla Firefox im Kioskmodus

Die vollständigen geprüften Paketversionen stehen in
`Installer/requirements.txt`.

## Projektstruktur

```text
C:\kiosk
├── app.py
├── app_streamdeck.py
├── button_config.json
├── kiosk.bat
├── Installer\
├── Manual\
├── static\
├── docs\images\
├── Backups\        # automatisch erzeugt
├── .venv\          # automatisch erzeugt
├── .vs\            # nur Visual-Studio-Daten
└── __pycache__\    # automatisch erzeugter Python-Cache
```

`.venv`, `.vs`, `__pycache__`, `Backups` und temporäre Dateien werden nicht im
Git-Repository benötigt.

## Sicherheitshinweise

- Standard-Zugangsdaten vor dem Einsatz ändern.
- Remote und Admin nur in einem vertrauenswürdigen Produktionsnetz verwenden.
- Die StreamDeck-API ist ausschließlich lokal erreichbar.
- Die StreamDeck-Eingabe bleibt standardmäßig gesperrt, bis sie im Admin-Panel
  freigegeben wird.

## Dokumentation

- [English installation manual](Installer/README.md)
- [Offline package preparation](Installer/Packages/README.txt)
- Weitere Word- und PDF-Dokumente befinden sich unter `Manual`.

## Lizenz

Für dieses Projekt ist derzeit keine separate Lizenzdatei hinterlegt.

