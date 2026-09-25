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
- `small_library.ps1 -Work <temp folder>`: a fresh copy of the Release build and a folder holding one small PDF; make its library with `first_run.ps1`.
- `status_bar.ps1 -App <small library copy> -Out <folder>`: the status bar hides a few seconds after the online lookups end, even when they end with Google refusing (the 1.0.3 fix). Sends one title to Open Library, Wikidata and Google.
- `moved_files.ps1 -Work <temp folder>`: a favorite with a note is moved into a subfolder, then renamed there; after each, the start-up scan keeps it one book at the new path with its favorite and note, and the folder's sidecar names the new path.
- `installer_portable.ps1 -Setup <setup exe> -Work <temp folder>`: a silent portable copy leaves no trace in Windows, and running Setup again on it keeps its library.
- `installer_pages.ps1 -Setup <setup exe> -Out <folder> -Mode portable|install|switch`: walks the installer's pages off-screen for each choice and cancels at the Ready page, so nothing is installed.
- `installer_update.ps1 -Setup <setup exe> -Work <temp folder> -Out <folder>`: updates a portable copy the way a user does, by Browsing to the folder that holds it; Setup must ask, update the copy in place and leave its library byte for byte.
