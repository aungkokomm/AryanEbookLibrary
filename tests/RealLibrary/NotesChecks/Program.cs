using System.Text.Json;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.Data.Sqlite;

// Proves the data side of highlights and notes against real files:
//   1. a copy of the installed library (v7) migrates to v8 and loads the same books;
//   2. annotations save, load in book order, delete (leaving a marker) and follow a moved file;
//   3. the highlights file beside the books carries them, the markers too, and another computer reads them;
//      a newer edit wins, an older one does not; a drive coming back gets the ones made while it was away;
//   4. the backup carries them into a fresh library.
var work = args[0];
Directory.CreateDirectory(work);
var fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
long Version(string db)
{
    using var c = new SqliteConnection($"Data Source={db};Pooling=False"); c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; return (long)cmd.ExecuteScalar()!;
}

// ---- 1. migration of a copy of the real library ----
var installed = @"D:\My Ebooks Data\AryanLibrary-Data\library.db";   // where the user keeps Aryan now
var copy = Path.Combine(work, "real-copy.db");
foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" }) if (File.Exists(f)) File.Delete(f);
using (var src = new SqliteConnection($"Data Source={installed};Mode=ReadOnly;Pooling=False"))
using (var dst = new SqliteConnection($"Data Source={copy};Pooling=False"))
{
    src.Open(); dst.Open();
    src.BackupDatabase(dst);   // a consistent snapshot, read only on the real file
}
var fromVersion = Version(copy);
int beforeBooks;
using (var raw = new SqliteConnection($"Data Source={copy};Pooling=False"))
{
    raw.Open();
    using var cmd = raw.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM books WHERE is_missing = 0";
    beforeBooks = Convert.ToInt32(cmd.ExecuteScalar());
}
using (var db = new Database(copy))
{
    var repo = new LibraryRepository(db);
    var store = new AnnotationStore(db);
    // (When 0.18 shipped this also proved the v7 to v8 upgrade on the real library, with no annotations yet.)
    Check(Version(copy) == Database.LatestVersion, $"real library opens at the latest layout (v{fromVersion} to v{Version(copy)})");
    Check(repo.LoadAll().Count == beforeBooks, $"all {beforeBooks:N0} books load");
    Console.WriteLine($"  annotations in the real library: {store.All(withDeleted: true).Count}");
}

// ---- a small library on a real folder, so the files beside the books have somewhere to live ----
var root = Path.Combine(work, "drive");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(Path.Combine(root, "Books", "Sub"));
foreach (var f in new[] { "Books\\a.epub", "Books\\b.pdf", "Books\\Sub\\c.cbz" })
    File.WriteAllText(Path.Combine(root, f), "x");
var ident = DriveRegistry.Identify(root) ?? throw new Exception("cannot identify the drive");
var marksFile = Path.Combine(root, "Books", StateSyncService.HighlightsName);
var stateFile = Path.Combine(root, "Books", StateSyncService.SidecarName);

(Database Db, LibraryRepository Repo, AnnotationStore Store, StateSyncService Sync, LibraryFolder Folder, string DriveRel) Library(string name)
{
    var file = Path.Combine(work, name);
    foreach (var f in new[] { file, file + "-wal", file + "-shm" }) if (File.Exists(f)) File.Delete(f);
    var db = new Database(file);
    var repo = new LibraryRepository(db);
    var store = new AnnotationStore(db);
    repo.AddDrive(ident.Id, "Test drive", ident.Root);
    DriveRegistry.Refresh(repo.GetDrives());
    var driveRel = Path.GetRelativePath(ident.Root, Path.Combine(root, "Books"));
    var folder = repo.AddFolder(ident.Id, driveRel);
    foreach (var n in new[] { "a.epub", "b.pdf", "Sub\\c.cbz" })
    {
        var rel = Path.Combine(driveRel, n);
        repo.UpsertBook(new Book { FolderId = folder.Id, DriveId = ident.Id, RelPath = rel, Format = FormatHelper.FromPath(rel), Title = Path.GetFileNameWithoutExtension(n) });
    }
    return (db, repo, store, new StateSyncService(repo, store), folder, driveRel);
}

var home = Library("home.db");
Book Get(LibraryRepository repo, string name) => repo.LoadAll().Single(b => b.RelPath.EndsWith(name));
void Save(AnnotationStore store, Book b, Annotation a) { a.UpdatedUtc = DateTime.UtcNow; store.Upsert(b.StateKey, b.DriveId, b.RelPath, a); Thread.Sleep(5); }

// ---- 2. saving, order, delete, move ----
Check(Version(Path.Combine(work, "home.db")) == 8, "fresh database is v8");
var epub = Get(home.Repo, "a.epub");
var pdf = Get(home.Repo, "b.pdf");
var comic = Get(home.Repo, "c.cbz");
var late = new Annotation { Kind = AnnotationKind.Highlight, Anchor = "epub1:epubcfi(/6/8!/4/2,/1:10,/1:40)", Page = 30, Position = 0.6, Quote = "a later passage", Color = 2, Chapter = "Two" };
var early = new Annotation { Kind = AnnotationKind.Highlight, Anchor = "epub1:epubcfi(/6/4!/4/2,/1:0,/1:20)", Page = 3, Position = 0.1, Quote = "an early passage", Before = "before it ", After = " after it", Color = 1, Note = "Remember this." };
var pageNote = new Annotation { Kind = AnnotationKind.PageNote, Anchor = "page1:5", Page = 5, Position = 0.5, Note = "Nice panel on this page." };
var area = new Annotation { Kind = AnnotationKind.Area, Anchor = Annotation.MakeAnchor("comicarea1", 5, 0.1, 0.2, 0.5, 0.25), Page = 5, Position = 0.5, Color = 5 };
var pdfMark = new Annotation { Kind = AnnotationKind.Highlight, Anchor = "pdf1:2:10:2:42", Page = 2, Position = 0.02, Quote = "PDF words", Color = 3 };
Save(home.Store, epub, late);
Save(home.Store, epub, early);
Save(home.Store, comic, pageNote);
Save(home.Store, comic, area);
Save(home.Store, pdf, pdfMark);
var list = home.Store.ForBook(epub.StateKey);
Check(list.Select(x => x.Quote).SequenceEqual(new[] { "an early passage", "a later passage" }), "a book's highlights come in book order, not the order made");
Check(list[0].Note == "Remember this." && list[0].Before == "before it " && list[0].After == " after it" && list[0].Color == 1, "every field survives a save and load");
Check(Annotation.AnchorNumbers(home.Store.ForBook(comic.StateKey).Single(x => x.Kind == AnnotationKind.Area).Anchor, "comicarea1") is [5, 0.1, 0.2, 0.5, 0.25], "an area's page and rectangle read back");
Check(home.Store.All().Count == 5, "five annotations in all");

var gone = home.Store.ForBook(epub.StateKey).Single(x => x.Quote == "a later passage");
gone.Deleted = true;
Save(home.Store, epub, gone);
Check(home.Store.ForBook(epub.StateKey).Count == 1 && home.Store.All().Count == 4, "a deleted one is not shown");
Check(home.Store.All(withDeleted: true).Count == 5, "but its marker is kept");

var movedRel = Path.Combine(home.DriveRel, "Sub", "c-moved.cbz");
File.Move(Path.Combine(root, "Books", "Sub", "c.cbz"), Path.Combine(root, "Books", "Sub", "c-moved.cbz"));
home.Repo.MoveBook(comic.Id, comic.DriveId, comic.RelPath, movedRel, home.Folder.Id);
var movedComic = Get(home.Repo, "c-moved.cbz");
Check(home.Store.ForBook(movedComic.StateKey).Count == 2 && home.Store.ForBook(comic.StateKey).Count == 0, "a moved file keeps its area and page note");

// ---- 3. the file beside the books ----
home.Sync.Flush(home.Folder.Id);
Check(File.Exists(marksFile), "the highlights file is written beside the books");
Check(!File.Exists(stateFile), "no book state yet, so no state file");
var json = File.ReadAllText(marksFile);
Check(json.Contains("an early passage") && json.Contains("Remember this.") && json.Contains("Nice panel on this page.") && json.Contains("c-moved.cbz"), "it carries the quotes, notes and the moved path");
Check(json.Contains("\"Deleted\": true"), "and the deleted one's marker");
Check(!json.Contains("HasNote") && !json.Contains("PageNumber"), "no working-out fields in the file");

// Another computer reading the same drive.
var laptop = Library("laptop.db");
var came = laptop.Sync.Import(laptop.Folder);
Check(came == 5, $"the other computer brings in all five, the marker too ({came})");
// Its books table still has c.cbz at the old path (it has not scanned since the move); the moved annotations are
// keyed to the new path, as the file says.
Check(laptop.Store.ForBook(Get(laptop.Repo, "a.epub").StateKey).Single().Quote == "an early passage", "the other computer shows the live highlight, not the deleted one");
Check(laptop.Sync.Import(laptop.Folder) == 0, "reading the same file again brings nothing new");

// The laptop edits a note later; home made an older edit to the same one it has not written yet.
var onLaptop = laptop.Store.ForBook(Get(laptop.Repo, "a.epub").StateKey).Single();
var homeCopy = home.Store.ForBook(epub.StateKey).Single();
homeCopy.Note = "Older edit made at home";
Save(home.Store, epub, homeCopy);
onLaptop.Note = "Newer edit from the laptop";
Save(laptop.Store, Get(laptop.Repo, "a.epub"), onLaptop);
laptop.Sync.Flush(laptop.Folder.Id);
came = home.Sync.Import(home.Folder);
Check(came >= 1 && home.Store.ForBook(epub.StateKey).Single().Note == "Newer edit from the laptop", "the newer edit wins");
// And the other way round: home edits after the laptop wrote; the laptop's older copy on the drive does not undo it.
var again = home.Store.ForBook(epub.StateKey).Single();
again.Note = "Newest, at home";
Save(home.Store, epub, again);
home.Sync.Import(home.Folder);
Check(home.Store.ForBook(epub.StateKey).Single().Note == "Newest, at home", "an older copy on the drive does not undo a newer edit here");

// Made here while the drive was away: the drive gets it when it is back.
var offline = new Annotation { Kind = AnnotationKind.Highlight, Anchor = "pdf1:7:0:7:12", Page = 7, Position = 0.07, Quote = "made while unplugged", Color = 4 };
Save(home.Store, pdf, offline);
Check(!File.ReadAllText(marksFile).Contains("made while unplugged"), "(setup) the drive does not have it");
home.Sync.SyncFolder(home.Folder);
Check(File.ReadAllText(marksFile).Contains("made while unplugged") && File.ReadAllText(marksFile).Contains("Newest, at home"), "drive back: what was made while it was away is written to it");
var stamp = File.GetLastWriteTimeUtc(marksFile);
Thread.Sleep(30);
home.Sync.SyncFolder(home.Folder);
Check(File.GetLastWriteTimeUtc(marksFile) == stamp, "already level: the file is not written again");

// A file from the old app (no highlights file at all) still imports its state, and the highlights file is untouched.
File.WriteAllText(stateFile, JsonSerializer.Serialize(new { Version = 1, Items = new Dictionary<string, BookState> { ["a.epub"] = new BookState { Notes = "state from the drive", UpdatedUtc = DateTime.UtcNow.AddMinutes(5) } } }));
var stateCame = home.Sync.Import(home.Folder);
Check(stateCame >= 1 && home.Repo.GetAllStates().Single(s => s.Key == epub.StateKey).State.Notes == "state from the drive", "the book-state file still imports beside the highlights file");

// ---- 4. the backup ----
var backup = Path.Combine(work, "backup.json");
var written = BackupService.Export(backup, home.Repo, home.Store);
var bj = File.ReadAllText(backup);
Check(bj.Contains("\"Annotations\"") && bj.Contains("made while unplugged") && bj.Contains("Nice panel on this page."), $"the backup has the annotations ({written} entries)");
var fresh = Library("fresh.db");
var applied = BackupService.Import(backup, fresh.Repo, fresh.Store);
Check(fresh.Store.All().Count == home.Store.All().Count && fresh.Store.All(withDeleted: true).Count == home.Store.All(withDeleted: true).Count,
    $"a fresh library gets them all back ({fresh.Store.All().Count} live, {fresh.Store.All(withDeleted: true).Count} with markers; applied {applied})");
Check(BackupService.Import(backup, fresh.Repo, fresh.Store) == 0, "restoring the same backup again changes nothing");

// ---- 5. the picture rule, and export ----
var myanmar = new string(new[] { (char)0x1019, (char)0x1031, (char)0x102C, (char)0x1004, (char)0x103A });
var hindi = new string(new[] { (char)0x0928, (char)0x092E, (char)0x0938, (char)0x094D, (char)0x0924, (char)0x0947 });
Check(Annotation.ReadsWell("Plain English words.") && !Annotation.ReadsWell(myanmar) && !Annotation.ReadsWell(hindi)
      && !Annotation.ReadsWell("odd " + (char)0xE001) && !Annotation.ReadsWell("broken " + (char)0xFFFD),
    "words read well only when no Burmese, Indic, private-use or broken character is in them");
Check(!new Annotation { Kind = AnnotationKind.Highlight, Anchor = "pdf1:1:0:1:5", Quote = "Plain words" }.ShowsPicture
      && new Annotation { Kind = AnnotationKind.Highlight, Anchor = "pdf1:1:0:1:5", Quote = myanmar }.ShowsPicture
      && !new Annotation { Kind = AnnotationKind.Highlight, Anchor = "epub1:epubcfi(/6/4!/4/2/1:0)", Quote = myanmar }.ShowsPicture
      && new Annotation { Kind = AnnotationKind.Area, Anchor = "comicarea1:0:0.1:0.1:0.5:0.5" }.ShowsPicture,
    "a card shows a picture for a clip and for a PDF's Burmese words, and an EPUB's own words as words");

var t0 = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
var rows = new List<ExportRow>
{
    new("The First Book", "An Author", "first.pdf", "Highlight", 1, "Line one of the words,\nline two.\n\nA second paragraph.", "", 12, "Chapter One", t0, t0, null),
    new("The First Book", "An Author", "first.pdf", "Clip", 3, "", "Look at this panel.\nIt has two lines.", 3, "", t0, t0, @"C:\clips\abc123.png"),
    new("The First Book", "An Author", "first.pdf", AnnotationExport.BookNoteKind, 0, "", "What I thought of it.", 0, "", t0, t0, null),
    new("Second, \"Quoted\" Book", "", "second.epub", "Highlight", 2, myanmar + ", with a comma", "", 5, "", t0, t0, null),
};
var pictures = new List<string>();
var md = AnnotationExport.Markdown(rows, "Highlights", "Highlights 2026-09-24 pictures", pictures);
File.WriteAllText(Path.Combine(work, "export.md"), md);
var mdLines = md.Replace("\r\n", "\n").Split('\n');
Check(mdLines[0] == "# Highlights" && md.Contains("4 highlights from 2 books."), "Markdown: a heading and how many from how many books");
Check(mdLines.Count(l => l.StartsWith("## ")) == 2 && md.IndexOf("## The First Book") < md.IndexOf("## Second, \"Quoted\" Book")
      && mdLines.Contains("An Author"), "Markdown: a section per book, in the order given, with its author");
Check(mdLines.Contains("> Line one of the words,") && mdLines.Contains("> line two.") && mdLines.Contains(">") && mdLines.Contains("> A second paragraph."),
    "Markdown: the words as a quote, line by line, its blank line kept inside the quote");
Check(mdLines.Contains("![Picture of page 3](Highlights%202026-09-24%20pictures/abc123.png)") && pictures.SequenceEqual(new[] { @"C:\clips\abc123.png" }),
    "Markdown: a clip links its picture in the folder beside the file, and is listed to be copied there");
Check(mdLines.Contains("**Note:** Look at this panel.") && mdLines.Contains("It has two lines.") && mdLines.Contains("**Note on the book:** What I thought of it."),
    "Markdown: notes, and the book's own note, labelled");
var dot = " " + (char)0x00B7 + " ";
Check(mdLines.Contains("*Page 12" + dot + "Chapter One" + dot + "Lime" + dot + t0.ToLocalTime().ToString("d MMM yyyy") + "*")
      && mdLines.Contains("*Page 3" + dot + "Orange" + dot + t0.ToLocalTime().ToString("d MMM yyyy") + "*"),
    "Markdown: under each, where it is, its colour and the day");
var csv = AnnotationExport.Csv(rows);
File.WriteAllText(Path.Combine(work, "export.csv"), csv, new System.Text.UTF8Encoding(true));
var csvLines = csv.Split("\r\n");
Check(csvLines[0] == "Book,Author,Kind,Colour,Words,Note,Page,Chapter,Created,Updated,File", "CSV: the header");
Check(csv.Contains("\"Line one of the words,\nline two.\n\nA second paragraph.\"") && csv.Contains("\"Second, \"\"Quoted\"\" Book\"")
      && csv.Contains("\"" + myanmar + ", with a comma\""), "CSV: commas, quotes and line breaks quoted as a spreadsheet expects; Burmese kept");
Check(csv.Contains("The First Book,An Author,Note on the book,,,What I thought of it.,,,") , "CSV: a book's note has no colour and no page");

foreach (var l in new[] { home, laptop, fresh }) l.Db.Dispose();
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
