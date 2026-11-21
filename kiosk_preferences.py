from contextlib import closing
import json
import sqlite3

import admin_access


def load():
    with closing(sqlite3.connect(admin_access.DATABASE)) as db:
        db.execute("CREATE TABLE IF NOT EXISTS preferences (name TEXT PRIMARY KEY, value TEXT NOT NULL)")
        return {key: json.loads(value) for key, value in db.execute("SELECT name, value FROM preferences")}


def save(name, value):
    with closing(sqlite3.connect(admin_access.DATABASE)) as db, db:
        db.execute("INSERT OR REPLACE INTO preferences VALUES (?, ?)", (name, json.dumps(value)))
