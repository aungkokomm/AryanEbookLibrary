"""Does to a scratch Covers folder what a scan does: rewrites cover files in place while the app shows them.

Usage: rewrite_covers.py <scratch AryanLibrary-Data> <seconds> [count]
The covers of the books first by title are rewritten (truncate, then write, like File.WriteAllBytesAsync) until time is up.
"""
import os, sqlite3, sys, tempfile, time

data, seconds = sys.argv[1], float(sys.argv[2])
count = int(sys.argv[3]) if len(sys.argv) > 3 else 200
assert os.path.abspath(data).lower().startswith(tempfile.gettempdir().lower()), data
con = sqlite3.connect("file:" + os.path.join(data, "library.db").replace(chr(92), "/") + "?mode=ro", uri=True)
rows = con.execute("SELECT title, cover_file FROM books WHERE cover_file IS NOT NULL AND cover_file <> ''").fetchall()
con.close()
rows.sort(key=lambda r: (r[0] or "").casefold())
files = [os.path.join(data, "Covers", c) for _, c in rows[:count] if os.path.exists(os.path.join(data, "Covers", c))]
blobs = {f: open(f, "rb").read() for f in files}

end = time.time() + seconds
passes = 0
while time.time() < end:
    for f, b in blobs.items():
        try:
            with open(f, "wb") as out:
                out.write(b[: len(b) // 2])
                out.flush()
                out.write(b[len(b) // 2 :])
        except OSError:
            pass   # the app has it open without sharing: a scan would fail the same way
    passes += 1
print(f"rewrote {len(files)} covers {passes} times")
