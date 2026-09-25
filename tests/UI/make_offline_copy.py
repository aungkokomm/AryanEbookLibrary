"""A copy of the real library whose drives can never be online: no scan, no sidecar is ever written to real folders.

Usage: make_offline_copy.py <real AryanLibrary-Data> <scratch AryanLibrary-Data>
Reads the real data folder only; covers are hard to rebuild, so they are copied, never moved.
"""
import json, os, shutil, sqlite3, sys, tempfile

real, dest = sys.argv[1], sys.argv[2]
assert os.path.abspath(dest).lower().startswith(tempfile.gettempdir().lower()), dest
os.makedirs(dest, exist_ok=True)

# A consistent copy even while the real app has a WAL open.
src = sqlite3.connect("file:" + os.path.join(real, "library.db").replace(chr(92), "/") + "?mode=ro", uri=True)
db = os.path.join(dest, "library.db")
for suffix in ("", "-wal", "-shm"):
    if os.path.exists(db + suffix):
        os.remove(db + suffix)
out = sqlite3.connect(db)
src.backup(out)
src.close()

# Every drive gets an id no volume has, so DriveRegistry never finds it connected.
for table, column in (("drives", "id"), ("folders", "drive_id"), ("books", "drive_id"),
                      ("book_state", "drive_id"), ("annotations", "drive_id")):
    out.execute(f"UPDATE {table} SET {column} = 'OFFLINE-' || {column} WHERE {column} NOT LIKE 'OFFLINE-%'")
out.commit()
print("drives now:", out.execute("SELECT id, label FROM drives").fetchall())
out.close()

with open(os.path.join(real, "settings.json"), encoding="utf-8") as f:
    settings = json.load(f)
settings["AutoScanOnStart"] = False
settings["LookupOnline"] = False
settings["GoogleBooksKey"] = ""
with open(os.path.join(dest, "settings.json"), "w", encoding="utf-8") as f:
    json.dump(settings, f, indent=2)

covers = os.path.join(dest, "Covers")
if not os.path.isdir(covers):
    shutil.copytree(os.path.join(real, "Covers"), covers)
print("covers:", len(os.listdir(covers)))
