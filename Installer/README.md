# Presentation Kiosk 1.4.7 — Installation Manual

Presentation Kiosk is a local alert and presentation system for temporary
events. It can display videos, images, slideshows, and video playlists and can
be controlled through a web-based remote interface or an Elgato Stream Deck.

## System requirements

- 64-bit Windows 10 or Windows 11
- Administrator rights for installation
- Internet access during installation unless a complete offline package has
  been prepared
- Optional: a compatible Elgato Stream Deck

The system explicitly uses **Python 3.12.6**. Newer Python versions should not
be used for this project because the Stream Deck integration may not work
reliably with them.

## Installation

1. Copy the complete `kiosk` folder to `C:\kiosk`.
2. Double-click `C:\kiosk\Installer\Install_Kiosk.cmd`.
3. Confirm the Windows administrator prompt.
4. Wait until the installer reports that the installation has completed.

The installer automatically:

- installs Python 3.12.6;
- installs the Microsoft Visual C++ runtime;
- creates the Python environment at `C:\kiosk\.venv`;
- installs the tested and pinned Python package versions;
- installs and configures HIDAPI for the Stream Deck;
- installs Mozilla Firefox;
- configures Firefox to allow video autoplay with sound;
- creates `kiosk.bat`;
- creates the **Firefox Kiosk** desktop shortcut;
- performs a final Python and Stream Deck test.

The permanent PowerShell execution policy is not changed. The CMD launcher
uses an execution-policy exception only for the installer process.

The installation log is written to:

```text
C:\kiosk\Installer\Install_Kiosk.log
```

## Online and offline installation

During an online installation, missing installers are downloaded to:

```text
C:\kiosk\Installer\Packages
```

For an installation without internet access, that directory must contain the
installers and Python wheels described in:

```text
C:\kiosk\Installer\Packages\README.txt
```

## Starting the system

Start the complete system with:

```text
C:\kiosk\kiosk.bat
```

This starts:

1. the Kiosk server, `app.py`;
2. the Stream Deck driver, `app_streamdeck.py`;
3. Firefox in private full-screen kiosk mode.

The **Firefox Kiosk** desktop shortcut can also be used to open the
presentation explicitly in Firefox. Do not click the URL shown in the terminal
to start the presentation: Windows opens terminal links in the default browser,
which may be Chrome and may block autoplay with sound.

## Web interfaces

On the Kiosk computer:

```text
Presentation: http://localhost:53100/
Presentation: http://localhost:53100/presentation
Remote:       http://localhost:53100/remote
Admin:        http://localhost:53100/admin
```

Remote and Admin can be opened from another device by replacing `localhost`
with the IP address displayed in the server terminal, for example:

```text
http://192.168.1.20:53100/remote
http://192.168.1.20:53100/admin
```

Remote and Admin are password-protected by default. The username and password
are configured in `app.py`.

The Stream Deck endpoint `/api` accepts requests only from the local Kiosk
computer. The local Stream Deck integration therefore continues to work, while
other devices cannot call this API directly.

## Media directories

All presentation media is stored below `C:\kiosk\static`.

| Directory | Purpose |
|---|---|
| `static\videos` | Individual videos such as `video1.mp4` |
| `static\videos_playlist` | Videos played as a continuous playlist |
| `static\bild1` through `static\bild10` | Images used for alert slideshows |
| `static\Message Templates` | Templates for creating new alert graphics |

Media can be created or copied into these directories at the event location.
The Admin panel highlights missing files and empty media directories in red.
The Remote interface blocks a missing medium and displays an exact error
message instead of activating it.

Button assignments and enabled states are stored in:

```text
C:\kiosk\button_config.json
```

Whenever this configuration is saved through the Admin panel, the previous
version is backed up automatically in `C:\kiosk\Backups`. The latest 20 backups
are retained.

## Automatically generated directories

### `.venv`

```text
C:\kiosk\.venv
```

This is the local Python environment used by the Kiosk. It contains the Python
links and all required packages in their tested versions.

- It is created automatically by the installer.
- It should not be edited manually.
- It does not need to be included in a distribution package.
- It should not be copied between computers.
- If it becomes damaged, delete `.venv` while the Kiosk is stopped and run
  `Installer\Install_Kiosk.cmd` again.

### `.vs`

```text
C:\kiosk\.vs
```

This directory is created by Microsoft Visual Studio and contains
user-specific development indexes and window settings.

- It is not required to run the Kiosk.
- It should not be included in the software package.
- It can be deleted safely while Visual Studio is closed.
- Visual Studio recreates it when necessary.

### `__pycache__`

```text
C:\kiosk\__pycache__
```

Python stores automatically compiled cache files in this directory.

- It is not part of the application.
- It does not need to be distributed or backed up.
- It can be deleted safely while the Kiosk is stopped.
- Python recreates it automatically.

### `Backups`

```text
C:\kiosk\Backups
```

This directory contains automatic backups of `button_config.json`. For a
complete backup of an event setup, copy this directory together with
`button_config.json` and the event media.

### `Installer`

Contains the combined installer, the pinned Python package list, the
installation log, and optionally the files required for an offline
installation.

### `Manual`

Contains the available user and system documentation. Older documents may
still refer to version 1.4.6.

## Files required in the software package

Include:

- `app.py`
- `app_streamdeck.py`
- `button_config.json`
- `kiosk.bat`
- `static` with the required HTML files, images, and media
- `Installer`
- `README.md`
- optionally `Manual`, logos, and additional documentation

The following files and directories do not need to be distributed:

- `.venv`
- `.vs`
- `__pycache__`
- temporary files
- `Thumbs.db`

## Post-installation test

After installation, verify the following:

1. `kiosk.bat` opens both terminal tabs.
2. Firefox opens in full-screen kiosk mode.
3. The start video plays with sound.
4. Remote and Admin are available through the displayed network address.
5. An available video and an available slideshow can be activated.
6. A missing medium is highlighted and blocked.
7. Stream Deck input can be locked and unlocked.
8. The Stream Deck reconnects after it is unplugged and plugged in again.
9. Saving the button configuration creates a file in `Backups`.

## Stopping the system

Press `Alt` + `F4` to close Firefox in kiosk mode. Then close both terminal tabs
or the Windows Terminal window to stop the server and the Stream Deck driver.

