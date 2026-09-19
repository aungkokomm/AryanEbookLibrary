# Aryan eBook Library

Portable, offline-aware eBook catalog for Windows (C# + WinUI 3), built on the same architecture as CineLibrary.
Formats: EPUB, PDF, MOBI/AZW/AZW3, CBZ, CBR. No AI, no cloud, no telemetry. It only reads your files, it never modifies them.

## Build

Requirements: Visual Studio 2026 with the ".NET desktop development" and "Windows App SDK C# templates" workloads, .NET 10 SDK. Uses Windows App SDK 2.5.1.

1. Open `AryanEbookLibrary.sln`, set platform to **x64**, press F5.
2. Portable release: run `.\publish.ps1` in PowerShell. Output: `publish\AryanEbookLibrary-portable.zip` (self-contained, no install).

Data lives in `AryanLibrary-Data\` next to the exe (SQLite index, covers, settings, log).

## Architecture (CineLibrary to Aryan mapping)

| CineLibrary idea | Here |
|---|---|
| Reader, not scraper (`.nfo` + poster) | `Services/Metadata`: embedded EPUB/PDF/MOBI/comic metadata + Calibre `metadata.opf` / `cover.jpg` |
| Local SQLite index, portable data folder | `Services/Database.cs`, `LibraryRepository.cs`, `AppPaths.cs` (`AryanLibrary-Data`) |
| Multi-drive, offline-aware, volume serial IDs | `Services/DriveRegistry.cs` (paths stored relative to the drive root, drive letters can change) |
| Unplugged drives stay visible, marked OFFLINE | `Book.IsAvailable`, 3-second drive watcher in `LibraryViewModel` |
| Play button only when drive is connected | Open button disabled when offline (`BookLauncher`) |
| Personal state travels with the drive | `StateSyncService`: `.aryan-library.json` sidecar at each library folder root, merged newest-wins |
| Backup/restore to one JSON | `BackupService` |
| Continue Watching / Recently Added / Surprise Me | Continue Reading / Recently Added / Surprise me (filter-aware) |
| Grid + list views, sort/filter/search | `Views/LibraryPage` |
| Per-item watched / favorite / notes / tags | Status, progress, rating, favorite, tags, notes in `BookDetailsDialog` |
| MVVM layering (Models, Services, ViewModels, Views) | Same folders |

## Metadata order

1. Embedded (EPUB OPF, PDF Info dictionary + page-1 render as cover, MOBI/EXTH incl. cover record, CBZ/CBR `ComicInfo.xml` + first page).
2. Calibre `metadata.opf` and `cover.jpg` in the same folder override embedded values, but only when the folder holds a single book (several formats of one book is fine).
3. Filename as title fallback.

## Known limits / next steps

- Opening a book uses the default app for the file type (built-in reader is not included).
- Reading progress is manual (the external reader owns the real position).
- Calibre-style folders with the same book in several formats show one entry per file.
- PDF title/author come from a fast scan of the Info dictionary; PDFs with compressed object streams fall back to the filename.
- Ideas: series grouping view, duplicate detection by ISBN, Burmese user guide, installer.
