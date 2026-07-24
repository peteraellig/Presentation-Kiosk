# Presentation Kiosk 1.4.7

Presentation Kiosk ist ein lokales Alert- und Präsentationssystem für temporäre
Veranstaltungen. Es kann Videos, Bilder, Slideshows und Video-Playlists anzeigen
und über eine Remote-Oberfläche oder ein Elgato Stream Deck bedient werden.

## Systemvoraussetzungen

- Windows 10 oder Windows 11, 64 Bit
- Administratorrechte für die Installation
- Internetzugang während der Installation, sofern kein vollständiges
  Offline-Paket vorbereitet wurde
- Optional: kompatibles Elgato Stream Deck

Das System verwendet ausdrücklich **Python 3.12.6**. Neuere Python-Versionen
sollen für dieses Projekt nicht verwendet werden, da der StreamDeck-Zusatz damit
nicht zuverlässig funktioniert.

## Installation

1. Den vollständigen Ordner `kiosk` nach `C:\kiosk` kopieren.
2. `C:\kiosk\Installer\Install_Kiosk.cmd` per Doppelklick starten.
3. Die Windows-Abfrage für Administratorrechte bestätigen.
4. Warten, bis der Installer die erfolgreiche Installation meldet.

Der Installer erledigt automatisch:

- Installation von Python 3.12.6
- Installation der Visual-C++-Laufzeit
- Erstellung der Python-Umgebung `C:\kiosk\.venv`
- Installation der festgeschriebenen Python-Paketversionen
- Installation und Einrichtung von HIDAPI für das Stream Deck
- Installation von Firefox
- Firefox-Richtlinie für automatischen Videostart mit Ton
- Erstellung der Startdatei `kiosk.bat`
- Erstellung der Desktop-Verknüpfung **Firefox Kiosk**
- abschließenden Python- und StreamDeck-Funktionstest

Die dauerhafte PowerShell-Ausführungsrichtlinie wird dabei nicht verändert.
`Install_Kiosk.cmd` startet nur den benötigten Installationsprozess mit einer
temporären Ausnahme.

Das Installationsprotokoll befindet sich unter:

```text
C:\kiosk\Installer\Install_Kiosk.log
```

## Online- und Offline-Installation

Fehlende Installationsdateien werden beim ersten Online-Lauf in diesem Ordner
gespeichert:

```text
C:\kiosk\Installer\Packages
```

Für eine Installation ohne Internet müssen dort die in
`Installer\Packages\README.txt` beschriebenen Installationsdateien und
Python-Wheels vorhanden sein.

## Programm starten

Das komplette System wird mit folgender Datei gestartet:

```text
C:\kiosk\kiosk.bat
```

Dabei werden gestartet:

1. der Kiosk-Server `app.py`,
2. der StreamDeck-Treiber `app_streamdeck.py`,
3. Firefox im privaten Vollbild-Kioskmodus.

Alternativ öffnet die Desktop-Verknüpfung **Firefox Kiosk** ausdrücklich Firefox.
Ein im Terminal angeklickter Weblink wird dagegen mit dem Windows-Standardbrowser
geöffnet und sollte deshalb nicht zum Starten der Präsentation verwendet werden.

## Bedienoberflächen

Auf dem Kiosk-Rechner:

```text
Präsentation: http://localhost:53100/
Präsentation: http://localhost:53100/presentation
Remote:       http://localhost:53100/remote
Admin:        http://localhost:53100/admin
```

Remote und Admin können von einem anderen Gerät über die im Server-Terminal
angezeigte IP-Adresse geöffnet werden, zum Beispiel:

```text
http://192.168.1.20:53100/remote
http://192.168.1.20:53100/admin
```

Standardmäßig sind Remote und Admin passwortgeschützt. Benutzername und Passwort
sind in `app.py` konfiguriert.

Die StreamDeck-API `/api` akzeptiert nur lokale Zugriffe vom Kiosk-Rechner. Das
Stream Deck funktioniert dadurch weiterhin, externe Geräte können die API aber
nicht direkt aufrufen.

## Medienordner

Alle Medien liegen unter `C:\kiosk\static`.

| Ordner | Inhalt |
|---|---|
| `static\videos` | einzelne Videos, beispielsweise `video1.mp4` |
| `static\videos_playlist` | Videos für die automatisch fortlaufende Playlist |
| `static\bild1` bis `static\bild10` | Bilder für einzelne Alarm-Slideshows |
| `static\Message Templates` | Vorlagen zur Erstellung neuer Alarmmeldungen |

Neue Medien können vor Ort in die vorgesehenen Ordner kopiert werden. Im
Admin-Panel werden fehlende Dateien oder leere Medienordner rot markiert. Ein
fehlendes Medium kann in der Remote-Oberfläche nicht gestartet werden; stattdessen
erscheint eine genaue Fehlermeldung.

Die Belegung und Aktivierung der Bedienknöpfe wird in folgender Datei gespeichert:

```text
C:\kiosk\button_config.json
```

Beim Speichern über das Admin-Panel wird die vorherige Version automatisch unter
`C:\kiosk\Backups` gesichert. Es werden die letzten 20 Sicherungen aufbewahrt.

## Erklärung der automatisch erzeugten Ordner

### `.venv`

```text
C:\kiosk\.venv
```

Dies ist die lokale Python-Umgebung des Kiosk-Programms. Sie enthält Python-
Verknüpfungen und sämtliche benötigten Pakete in den geprüften Versionen.

- Der Ordner wird vom Installer automatisch erstellt.
- Er darf nicht manuell bearbeitet werden.
- Er muss nicht in ein Installationspaket aufgenommen werden.
- Er sollte nicht zwischen verschiedenen Computern kopiert werden.
- Bei einer Beschädigung kann `.venv` gelöscht und durch erneutes Ausführen von
  `Installer\Install_Kiosk.cmd` neu erstellt werden.

### `.vs`

```text
C:\kiosk\.vs
```

Dieser Ordner stammt von Microsoft Visual Studio. Er enthält ausschließlich
benutzerspezifische Entwicklungsdaten, Suchindizes und Fenster-Einstellungen.

- Für den Betrieb des Kiosks wird er nicht benötigt.
- Er muss nicht mit dem Softwarepaket ausgeliefert werden.
- Er kann bei geschlossenem Visual Studio gefahrlos gelöscht werden.
- Visual Studio erstellt ihn bei Bedarf automatisch neu.

### `__pycache__`

```text
C:\kiosk\__pycache__
```

Python legt hier automatisch vorkompilierte Zwischendateien an, damit Module
schneller geladen werden können.

- Der Ordner gehört nicht zum eigentlichen Programm.
- Er muss nicht mitgeliefert oder gesichert werden.
- Er kann bei beendetem Kiosk gefahrlos gelöscht werden.
- Python erstellt ihn beim nächsten Start automatisch neu.

### `Backups`

```text
C:\kiosk\Backups
```

Dieser Ordner enthält automatisch erzeugte Sicherungen von
`button_config.json`. Für eine vollständige Sicherung der vor Ort erstellten
Konfiguration sollte dieser Ordner zusammen mit `button_config.json` und den
Medienordnern kopiert werden.

### `Installer`

Enthält den gemeinsamen Installer, die festgeschriebenen Python-Paketversionen
und optional die Dateien für eine Offline-Installation.

### `Manual`

Enthält vorhandene Bedienungs- und Systemdokumentationen. Ältere Dokumente können
noch die Versionsnummer 1.4.6 tragen.

## Was gehört in das Softwarepaket?

Mitgeliefert werden sollten:

- `app.py`
- `app_streamdeck.py`
- `button_config.json`
- `kiosk.bat`
- `static` mit allen benötigten Medien und HTML-Dateien
- `Installer`
- `README.md`
- optional `Manual`, Logos und weitere Dokumentation

Nicht mitgeliefert werden müssen:

- `.venv`
- `.vs`
- `__pycache__`
- temporäre Dateien
- `Thumbs.db`

## Kurzer Funktionstest

Nach einer Installation sollten folgende Punkte geprüft werden:

1. `kiosk.bat` startet beide Terminal-Tabs.
2. Firefox öffnet sich im Vollbild-Kioskmodus.
3. Das Startvideo läuft mit Ton.
4. Remote und Admin sind über die angezeigte Netzwerkadresse erreichbar.
5. Ein vorhandenes Video und eine Slideshow lassen sich starten.
6. Ein fehlendes Medium wird markiert und blockiert.
7. Das Stream Deck lässt sich sperren und freigeben.
8. Das Stream Deck funktioniert nach Abziehen und erneutem Anstecken weiter.
9. Beim Speichern der Konfiguration wird unter `Backups` eine Sicherung erstellt.

## Beenden

Firefox kann im Kioskmodus mit `Alt` + `F4` beendet werden. Anschließend die
beiden Terminal-Tabs beziehungsweise das Windows-Terminal schließen, um Server
und StreamDeck-Treiber zu beenden.

