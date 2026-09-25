# Tests

## AryanEbookLibrary.Tests (run before every release)

Fast tests of the logic that needs no window, on made-up data. They compile the app's own source files, so they test the code that ships.

```
dotnet test tests\AryanEbookLibrary.Tests -p:Platform=x64
```

Covered so far: author fields and what is not a person, copyright pages, file names, Zawgyi, Google Books answers and how online details merge, the Authors offer, tags, the reading log, and the database upgrade with its backup.

## RealLibrary (checks against a real library)

Console programs that proved features against the real library. They only **read** the real library: each works on a copy, or on files it makes in the folder given as its first argument. Some paths inside are specific to the development machine (`D:\My Ebooks Data`).

- `ScanChecks <copy of library.db> <mode>`: the scanner and its modes (`enrich`, `retitle`, `google`, `shelves`, `reading`, `tagrename`, `moved`, `tags`, `zawgyi`, ...).
- `NotesChecks <work folder>`: highlights and notes: saving, the file beside the books, backup.
- `ListsChecks <work folder>`: My lists, sidecar sync, backup, copying a list's files.

## UI (off-screen checks of the running app)

PowerShell scripts that start a copy of the app off-screen and drive it with UI Automation. They refuse any app folder outside the Windows temp folder, so they can never touch the copy you use.

- `make_offline_copy.py <real AryanLibrary-Data> <temp copy>\AryanLibrary-Data`: a copy of a real library whose drives never count as connected, so nothing is scanned and no sidecar is written.
- `startups.ps1 -App <temp copy> [-Rewrite]`: starts it 20 times and counts the starts where it dies by itself. `-Rewrite` rewrites covers during start-up, as a scan does (the 0xC000027B crash fixed in 1.0).
- `single_instance.ps1 -App <copy> -OtherApp <another copy>`: one running copy per library.
- `session_test.ps1 -App <copy> -Out <folder>`: a normal close, a kill, and Windows ending the session.
- `first_run.ps1 -App <new copy> -Books <folder of books> -Out <folder>`: a brand-new library, from the empty page's button through the real folder picker to the books.
