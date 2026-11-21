import ipaddress
import secrets
import sqlite3
import threading
import time
from contextlib import closing
from pathlib import Path
from urllib.parse import urlsplit

from flask import Blueprint, abort, redirect, render_template_string, request, session
from werkzeug.security import check_password_hash, generate_password_hash


admin_access = Blueprint("admin_access", __name__)
DATABASE = Path(__file__).with_name("admin_access.sqlite3")
SESSION_SECONDS = 8 * 60 * 60
_sessions = {}
_session_lock = threading.Lock()


def password_hash():
    with closing(sqlite3.connect(DATABASE)) as db:
        db.execute("CREATE TABLE IF NOT EXISTS admin (id INTEGER PRIMARY KEY CHECK(id=1), hash TEXT NOT NULL)")
        row = db.execute("SELECT hash FROM admin WHERE id=1").fetchone()
    return row[0] if row else None


def local_setup():
    host = urlsplit(request.host_url).hostname
    return (ipaddress.ip_address(request.remote_addr).is_loopback
            and host in {"localhost", "127.0.0.1", "::1"})


def signed_in():
    with _session_lock:
        return _sessions.get(session.get("admin_token"), 0) > time.monotonic()


def require_admin():
    if not signed_in():
        abort(401)
    if request.method == "POST" and request.headers.get("X-Kiosk-Admin") != "1":
        abort(403)


PAGE = """<!doctype html><html lang="de"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Kiosk 3 – Administration</title><link rel="stylesheet" href="/static/style.css">
<main class="wrap"><section class="card"><h1>{{ title }}</h1>
{% if message %}<p role="alert">{{ message }}</p>{% endif %}
{% if form %}<form method="post">
<input type="hidden" name="csrf" value="{{ csrf }}">
<p><label>Admin-Passwort <input name="password" type="password" required maxlength="256"
 autocomplete="{{ 'new-password' if setup else 'current-password' }}" autofocus></label></p>
{% if setup %}<p><label>Passwort wiederholen <input name="confirm" type="password"
 required maxlength="256" autocomplete="new-password"></label></p>{% endif %}
<button>{{ 'Passwort festlegen' if setup else 'Anmelden' }}</button></form>{% endif %}
</section></main></html>"""


@admin_access.route("/admin/login", methods=["GET", "POST"])
def login():
    if signed_in():
        return redirect("/admin")
    stored = password_hash()
    setup = stored is None
    if setup and not local_setup():
        return render_template_string(PAGE, title="Kiosk 3", form=False,
            message="Das erste Admin-Passwort muss am Kiosk-PC über localhost eingerichtet werden."), 403
    title = "Admin-Passwort einrichten" if setup else "Admin-Anmeldung"
    message = "Bitte mindestens 8 Zeichen verwenden." if setup else ""
    status = 200
    csrf = session.setdefault("login_csrf", secrets.token_hex(32))
    if request.method == "POST":
        if request.form.get("csrf") != csrf:
            abort(403)
        password = request.form.get("password", "")
        if setup:
            if not 8 <= len(password) <= 256 or not password.strip() or password != request.form.get("confirm"):
                message, status = "Mindestens 8 Zeichen eingeben und identisch wiederholen.", 400
            else:
                with closing(sqlite3.connect(DATABASE)) as db, db:
                    result = db.execute("INSERT OR IGNORE INTO admin VALUES (1, ?)", (generate_password_hash(password),))
                if not result.rowcount:
                    abort(409)
                session.pop("login_csrf", None)
                return redirect("/admin/login")
        elif password.strip() and len(password) <= 256 and check_password_hash(stored, password):
            token = secrets.token_urlsafe(32)
            now = time.monotonic()
            with _session_lock:
                for key in [k for k, expiry in _sessions.items() if expiry <= now]:
                    del _sessions[key]
                _sessions[token] = now + SESSION_SECONDS
            session.pop("login_csrf", None)
            session["admin_token"] = token
            return redirect("/admin")
        else:
            message, status = "Das Passwort ist ungültig.", 401
    return render_template_string(PAGE, title=title, message=message, csrf=csrf, setup=setup, form=True), status


@admin_access.post("/admin/logout")
def logout():
    require_admin()
    with _session_lock:
        _sessions.pop(session.pop("admin_token", None), None)
    return "", 204
