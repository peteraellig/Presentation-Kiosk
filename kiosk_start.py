"""Start only missing Kiosk3 components, without touching an existing output."""
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import psutil
from kiosk_runtime import PORT, ROOT, instance_lock, ready_status

PROFILE = Path(os.environ["LOCALAPPDATA"]) / "Kiosk3" / "Firefox"
CHROME_PROFILE = PROFILE.parent / "Chrome"
FIREFOX = Path(os.environ["ProgramFiles"]) / "Mozilla Firefox" / "firefox.exe"


def prepare_firefox_profile(profile):
    profile.mkdir(parents=True, exist_ok=True)
    user_js = profile / "user.js"
    setting = (ROOT / "firefox-profile" / "user.js").read_bytes().strip()
    contents = user_js.read_bytes() if user_js.exists() else b""
    if contents.rstrip().endswith(setting):
        return
    # Put the kiosk preference last so earlier user.js entries and comments survive.
    with user_js.open("ab") as prefs:
        prefs.write(b"\n" + setting + b"\n")


def running_component(name, argument):
    expected = Path(argument).resolve()
    for process in psutil.process_iter(["name", "cmdline"]):
        try:
            if (process.info["name"] or "").lower() not in name:
                continue
            args = (process.info["cmdline"] or [])[1:]
            for arg in args:
                arg = arg.removeprefix("--user-data-dir=")
                if os.path.basename(arg).lower() != expected.name.lower():
                    continue
                path = Path(arg)
                if not path.is_absolute():
                    path = Path(process.cwd()) / path
                if path.resolve() == expected:
                    return process.pid
        except (psutil.Error, OSError, ValueError):
            continue
    return None


def python_process(script):
    return running_component({"python.exe", "pythonw.exe"}, ROOT / script)


def start_python(script):
    return subprocess.Popen([sys.executable, str(ROOT / script)], cwd=ROOT,
                            creationflags=subprocess.CREATE_NEW_CONSOLE)


def chrome_command(profile, url):
    for base in ("ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"):
        if os.environ.get(base):
            exe = Path(os.environ[base]) / "Google/Chrome/Application/chrome.exe"
            if exe.is_file():
                return [str(exe), f"--user-data-dir={profile}", "--no-first-run",
                        "--no-default-browser-check", "--autoplay-policy=no-user-gesture-required",
                        "--kiosk", url]
    raise RuntimeError("Google Chrome ist nicht installiert.")


def start_missing(use_chrome=False):
    server_pid = python_process("app.py")
    status = ready_status(recognize_older=bool(server_pid))
    if status and status.get("older"):
        print("Kiosk3 läuft bereits. Die neue Startlogik gilt ab dem nächsten regulären Neustart.")
        return
    started = None
    if status is None and server_pid is None:
        with socket.socket() as probe:
            probe.settimeout(1)
            if probe.connect_ex(("127.0.0.1", PORT)) == 0:
                raise RuntimeError(f"Port {PORT} ist bereits belegt.")
        started = start_python("app.py")
        server_pid = started.pid
    deadline = time.monotonic() + 35
    while status is None and time.monotonic() < deadline:
        if started is not None and started.poll() is not None:
            raise RuntimeError("Kiosk3-Server beim Start beendet. Bitte Serverausgabe prüfen.")
        time.sleep(.25)
        status = ready_status()
    if status is None:
        raise RuntimeError("Der vorhandene Server meldet keine Startbereitschaft. "
                           "Nach einem Update den bisherigen Server einmal regulär beenden.")
    if status.get("pid") != server_pid:
        raise RuntimeError("Der antwortende Server gehört nicht zu dieser Kiosk3-Kopie.")
    if not python_process("app_streamdeck.py"):
        start_python("app_streamdeck.py")
    if (status["screen"] or running_component({"firefox.exe"}, PROFILE)
            or running_component({"chrome.exe"}, CHROME_PROFILE)):
        return
    if use_chrome:
        subprocess.Popen(chrome_command(CHROME_PROFILE, f"http://localhost:{PORT}"), cwd=ROOT)
        return
    if not FIREFOX.is_file():
        raise RuntimeError(f"Firefox fehlt: {FIREFOX}")
    prepare_firefox_profile(PROFILE)
    subprocess.Popen([str(FIREFOX), "-no-remote", "-profile", str(PROFILE),
                      "--kiosk", f"http://localhost:{PORT}"], cwd=ROOT)


if __name__ == "__main__":
    try:
        with instance_lock("launcher") as first:
            if first:
                start_missing(use_chrome=sys.argv[1:] == ["--chrome"])
    except (OSError, RuntimeError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
