"""Shared addresses and Windows instance locks for the Kiosk3 processes."""
import json
import os
if os.name == "nt":
    import msvcrt
else:
    import fcntl
from contextlib import contextmanager
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import ProxyHandler, build_opener

ROOT = Path(__file__).resolve().parent
PORT = 53101
SERVER_URL = f"http://127.0.0.1:{PORT}"
_local = build_opener(ProxyHandler({}))


def ready_status(*, recognize_older=False):
    try:
        with _local.open(SERVER_URL + "/_kiosk/ready", timeout=1) as response:
            status = json.load(response)
        if isinstance(status, dict) and status.get("service") == "Kiosk3":
            return status
    except HTTPError as error:
        if recognize_older and error.code == 404:
            return {"older": True}
    except (URLError, OSError, ValueError):
        pass
    return None


@contextmanager
def instance_lock(component):
    # Windows releases the byte-range lock even when a process is killed.
    with (ROOT / ("." + component + ".lock")).open("a+b") as handle:
        handle.seek(0)
        try:
            if os.name == "nt":
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except (PermissionError, BlockingIOError):
            yield False
        else:
            try:
                yield True
            finally:
                handle.seek(0)
                if os.name == "nt":
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(handle, fcntl.LOCK_UN)
