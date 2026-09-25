# Aryan eBook Library

A portable eBook library and reader for Windows 10 and 11 (C#, WinUI 3, Windows App SDK 2.5.1, .NET 10).

It catalogues EPUB, PDF, MOBI, AZW3, CBZ and CBR books across every drive you keep them on, reads them in its own readers, and keeps your highlights, notes and reading history. It only ever **reads** your book files: nothing in them is changed, renamed or moved.

## What it does

- **Library:** grid and list views, search, filters (format, language, decade, publisher, rating, tags), saved shelves, My lists, series, authors, tags, duplicates, missing books, and books that need details.
- **Readers:** PDF (PDFium), EPUB, MOBI and AZW3 (foliate-js in WebView2), and comics (CBZ, CBR), with highlights in five colours, clipped areas, page notes and a reading log. Right-click a word for its meaning in English, Myanmar or Hindi, offline.
- **Details:** read from the book itself, Calibre's `metadata.opf`, the copyright page and the file name. Online lookups on Open Library, Wikidata, Wikipedia and (with your own free key) Google Books only fill what is missing, are off until you turn them on, and send only a title, author or ISBN.
- **Drives:** each drive is known by its hardware ID, so a changed drive letter breaks nothing, and books on an unplugged drive stay in the library marked offline. Your favourites, ratings, lists, notes and highlights also travel with the drive, in small files beside the books.
- **Your data:** one portable folder, `AryanLibrary-Data`, beside the app. Backup and restore to one file, catalogue export to CSV. Before a new version upgrades the database, the old one is copied to `AryanLibrary-Data\Backups`.

No accounts, no telemetry. The log (`aryan.log`) stays on your computer.

## Install

Run `AryanEbookLibrary-Setup-<version>.exe`. Its first page asks how you want Aryan:

- **Install for me:** for your user only (no administrator rights), with a Start menu entry, and removable from Settings > Apps.
- **Portable:** copied into any folder you choose, such as a USB drive or `D:\`. Nothing is written to Windows: no Start menu entry, no uninstaller. The library lives in `AryanLibrary-Data` beside the app, so the whole folder can move to another drive or PC. Running Setup again on the same folder updates the app and keeps the library.

Either way Setup never touches `AryanLibrary-Data`, so updating keeps your library. A portable copy can also be made silently: `AryanEbookLibrary-Setup-<version>.exe /VERYSILENT /PORTABLE /DIR="E:\Aryan"`.

## Build

Visual Studio 2026 with the .NET desktop and Windows App SDK workloads, the .NET 10 SDK, and Rust (the reading core in `native\reader_core` builds with `cargo` as part of the app build). For the installer, Inno Setup 6.

```
MSBuild AryanEbookLibrary.csproj -t:Build -p:Configuration=Release -p:Platform=x64
pwsh -File tools\build_installer.ps1
```

`tools\build_installer.ps1` writes `dist\AryanEbookLibrary-Setup-<version>.exe` and never overwrites an existing one: raise `<Version>` in the project first.

## Tests

```
dotnet test tests\AryanEbookLibrary.Tests -p:Platform=x64
```

See `tests\README.md` for the checks that run against a real library and the off-screen checks of the running app.

## Licence

MIT, see `LICENSE`. Aryan includes other people's software and data under their own licences: see `THIRD-PARTY-NOTICES.txt` and the `Licenses` folder beside the app, or Settings > About.
