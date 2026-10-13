"""Content fingerprint of the generated legacy DB and truth log, to prove a rebuild on another machine is identical.

Hashes the logical SQL dump (not file bytes: SQLite versions may lay out pages differently), skipping the build
timestamp. Usage: python scripts/fingerprint_legacy.py [data/legacy.sqlite data/legacy_truth.sqlite]
"""
import hashlib
import sqlite3
import sys

paths = sys.argv[1:] or ["data/legacy.sqlite", "data/legacy_truth.sqlite"]
for path in paths:
    h = hashlib.sha256()
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    for line in con.iterdump():
        if "built_at" not in line:
            h.update(line.encode("utf-8"))
            h.update(b"\n")
    con.close()
    print(f"{h.hexdigest()}  {path}")
