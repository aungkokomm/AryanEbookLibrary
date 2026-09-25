using System.Text.Json;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.Data.Sqlite;

// Proves the data side of My lists, notes sync and the reconnect flush against real files:
//   (1. the 0.17 upgrade of a copy of the real library ran once and is retired; NotesChecks covers upgrades now)
//   2. lists survive save/load, rename, delete and a moved file;
//   3. the sidecar carries lists, a drive coming back gets the edits made while it was away,
//      and another computer's newer edit (with a new list) comes in;
//   4. the backup carries lists, including an empty one.
var work = args[0];
Directory.CreateDirectory(work);
var fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
long Version(string db)
{
    using var c = new SqliteConnection($"Data Source={db};Pooling=False"); c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; return (long)cmd.ExecuteScalar()!;
}

// ---- a small library on a real folder, so the sidecar has somewhere to live ----
var root = Path.Combine(work, "drive");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(Path.Combine(root, "Books", "Sub"));
foreach (var f in new[] { "Books\\a.epub", "Books\\b.pdf", "Books\\Sub\\c.epub" })
    File.WriteAllText(Path.Combine(root, f), "x");

var dbFile = Path.Combine(work, "lists.db");
foreach (var f in new[] { dbFile, dbFile + "-wal", dbFile + "-shm" }) if (File.Exists(f)) File.Delete(f);
var ident = DriveRegistry.Identify(root) ?? throw new Exception("cannot identify the drive");
var sidecar = Path.Combine(root, "Books", StateSyncService.SidecarName);

using (var db = new Database(dbFile))
{
    var repo = new LibraryRepository(db);
    Check(Version(dbFile) == Database.LatestVersion, "fresh database is at the latest layout");
    repo.AddDrive(ident.Id, "Test drive", ident.Root);
    DriveRegistry.Refresh(repo.GetDrives());
    var driveRel = Path.GetRelativePath(ident.Root, Path.Combine(root, "Books"));
    var folder = repo.AddFolder(ident.Id, driveRel);
    Book Add(string name)
    {
        var rel = Path.Combine(driveRel, name);
        var b = new Book { FolderId = folder.Id, DriveId = ident.Id, RelPath = rel, Format = FormatHelper.FromPath(rel), Title = Path.GetFileNameWithoutExtension(name) };
        repo.UpsertBook(b);
        return b;
    }
    Add("a.epub"); Add("b.pdf"); Add("Sub\\c.epub");
    Book Get(string name) => repo.LoadAll().Single(b => b.RelPath.EndsWith(name));
    void Save(Book b) { b.StateUpdatedUtc = DateTime.UtcNow; repo.UpsertState(b.DriveId, b.RelPath, b.StateKey, b.ToState()); Thread.Sleep(5); }

    // ---- 2. lists ----
    Check(repo.AddList("Summer reading"), "list made");
    Check(!repo.AddList("summer READING"), "same name in another case refused");
    Check(repo.AddList("To lend"), "second list made");
    Check(repo.AddList("Empty one"), "an empty list");
    Check(repo.GetLists().SequenceEqual(new[] { "Summer reading", "To lend", "Empty one" }), "lists in the order made: " + string.Join(", ", repo.GetLists()));

    var a = Get("a.epub");
    a.Lists = new List<string> { "Summer reading", "To lend" };
    a.Notes = "Loved the ending.";
    Save(a);
    var c = Get("c.epub");
    c.Lists = new List<string> { "Summer reading" };
    Save(c);
    Check(Get("a.epub").Lists.SequenceEqual(new[] { "Summer reading", "To lend" }), "a book's lists survive a reload");
    Check(Get("a.epub").HasNote && Get("b.pdf").HasNote == false, "HasNote");
    Check(Get("b.pdf").Lists.Count == 0, "a book on no list");

    var changed = repo.RenameList("summer reading", "Holiday");
    Check(changed.Count == 2, $"rename touched both books ({changed.Count})");
    Check(repo.GetLists().Contains("Holiday") && !repo.GetLists().Contains("Summer reading"), "renamed in the list of lists");
    Check(Get("a.epub").Lists.SequenceEqual(new[] { "Holiday", "To lend" }) && Get("c.epub").Lists.SequenceEqual(new[] { "Holiday" }), "renamed on the books, in place");
    Check(Get("a.epub").StateUpdatedUtc > a.StateUpdatedUtc, "a rename stamps the book, so an older sidecar cannot undo it");

    changed = repo.RenameList("To lend", null);
    Check(changed.Count == 1 && !repo.GetLists().Contains("To lend"), "list deleted");
    Check(Get("a.epub").Lists.SequenceEqual(new[] { "Holiday" }), "deleted list gone from the book, the rest kept");
    Check(Get("a.epub").Notes == "Loved the ending.", "deleting a list leaves the note");

    // A moved file keeps its lists (the state row follows it).
    var moved = Get("c.epub");
    var newRel = Path.Combine(driveRel, "Sub", "c-moved.epub");
    File.Move(Path.Combine(root, "Books", "Sub", "c.epub"), Path.Combine(root, "Books", "Sub", "c-moved.epub"));
    repo.MoveBook(moved.Id, moved.DriveId, moved.RelPath, newRel, folder.Id);
    Check(Get("c-moved.epub").Lists.SequenceEqual(new[] { "Holiday" }), "a moved book keeps its list");

    // ---- 3. the sidecar ----
    var sync = new StateSyncService(repo, new AnnotationStore(db));
    sync.Flush(folder.Id);
    var json = File.ReadAllText(sidecar);
    Check(json.Contains("\"Lists\"") && json.Contains("Holiday") && json.Contains("Loved the ending."), "sidecar carries the list and the note");

    // An edit made while the drive was away: saved here, never written to the drive.
    var aa = Get("a.epub");
    aa.Notes = "Written while the drive was unplugged.";
    aa.Lists = new List<string> { "Holiday", "Empty one" };
    Save(aa);
    Check(!File.ReadAllText(sidecar).Contains("unplugged"), "(setup) the drive does not have the offline edit");
    var stamp = File.GetLastWriteTimeUtc(sidecar);
    Thread.Sleep(20);
    var came = sync.SyncFolder(folder);
    json = File.ReadAllText(sidecar);
    Check(came == 0, $"nothing newer on the drive ({came})");
    Check(json.Contains("Written while the drive was unplugged.") && json.Contains("Empty one"), "drive back: the offline edit is written to it");
    stamp = File.GetLastWriteTimeUtc(sidecar);
    Thread.Sleep(20);
    sync.SyncFolder(folder);
    Check(File.GetLastWriteTimeUtc(sidecar) == stamp, "already level: the sidecar is not written again");

    // Another computer's newer edit, with a list this one has never seen.
    var file = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(sidecar));
    var items = file.GetProperty("Items").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Deserialize<BookState>()!);
    var bKey = items.Keys.FirstOrDefault(k => k.EndsWith("b.pdf"));
    var bState = new BookState { Notes = "Note from the other computer", Lists = new List<string> { "From the laptop" }, UpdatedUtc = DateTime.UtcNow.AddMinutes(1) };
    items["b.pdf"] = bState;
    File.WriteAllText(sidecar, JsonSerializer.Serialize(new { Version = 1, Items = items }, new JsonSerializerOptions { WriteIndented = true }));
    came = sync.SyncFolder(folder);
    Check(came == 1, $"one newer edit came in ({came})");
    var b = Get("b.pdf");
    Check(b.Notes == "Note from the other computer" && b.Lists.SequenceEqual(new[] { "From the laptop" }), "the other computer's note and list are here");
    Check(repo.GetLists().Contains("From the laptop"), "its list now exists here too");
    Check(Get("a.epub").Notes == "Written while the drive was unplugged.", "this computer's newer edit was not overwritten");

    // An older sidecar cannot bring a renamed list back.
    var older = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(sidecar)).GetProperty("Items").EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.Deserialize<BookState>()!);
    repo.RenameList("Holiday", "Beach");
    sync.SyncFolder(folder);
    Check(!repo.GetLists().Contains("Holiday") && Get("a.epub").Lists.Contains("Beach"), "rename holds against the drive's older copy");
    Check(File.ReadAllText(sidecar).Contains("Beach") && !File.ReadAllText(sidecar).Contains("Holiday"), "and the drive gets the new name");

    // ---- 4. backup ----
    var backup = Path.Combine(work, "backup.json");
    BackupService.Export(backup, repo, new AnnotationStore(db));
    var bj = File.ReadAllText(backup);
    Check(bj.Contains("\"Lists\"") && bj.Contains("Empty one") && bj.Contains("Beach"), "backup has the lists");

    var dbFile2 = Path.Combine(work, "restore.db");
    foreach (var f in new[] { dbFile2, dbFile2 + "-wal", dbFile2 + "-shm" }) if (File.Exists(f)) File.Delete(f);
    using var db2 = new Database(dbFile2);
    var repo2 = new LibraryRepository(db2);
    var applied = BackupService.Import(backup, repo2, new AnnotationStore(db2));
    Check(applied >= 2, $"backup applied ({applied})");
    var lists2 = repo2.GetLists();
    Check(lists2.Contains("Empty one") && lists2.Contains("Beach") && lists2.Contains("From the laptop"), "restored lists, empty one included: " + string.Join(", ", lists2));
    var aKey = Book.MakeKey(ident.Id, Path.Combine(driveRel, "a.epub"));
    Check(repo2.GetAllStates().Single(s => s.Key == aKey).State.Lists.SequenceEqual(new[] { "Beach", "Empty one" }), "restored book is on its lists");
}

// ---- 5. copy books to folder ----
{
    var src = Path.Combine(work, "copysrc");
    if (Directory.Exists(src)) Directory.Delete(src, true);
    Directory.CreateDirectory(Path.Combine(src, "One"));
    Directory.CreateDirectory(Path.Combine(src, "Two"));
    File.WriteAllText(Path.Combine(src, "One", "a.epub"), "first a");
    File.WriteAllText(Path.Combine(src, "Two", "a.epub"), "second a");
    File.WriteAllText(Path.Combine(src, "One", "b.pdf"), "b");
    var big = Path.Combine(src, "Two", "big.pdf");
    using (var f = File.Create(big)) f.SetLength(200L << 20);   // 200 MB, to stop half way
    var ident2 = DriveRegistry.Identify(src)!.Value;
    DriveRegistry.Refresh(new[] { new DriveRecord { Id = ident2.Id, Label = "Test", LastRoot = ident2.Root } });
    Book B(string rel, string drive = "", string label = "Test", bool online = true) => new()
    {
        DriveId = drive.Length > 0 ? drive : ident2.Id,
        DriveLabel = label,
        RelPath = Path.GetRelativePath(ident2.Root, Path.Combine(src, rel)),
        Title = rel,
        IsAvailable = online
    };
    var books = new List<Book>
    {
        B("One\\a.epub"), B("Two\\a.epub"), B("One\\b.pdf"),
        B("gone.epub"),                                         // file not there
        B("x.epub", "OFFLINE1", "Old drive", online: false)     // drive not plugged in
    };
    var dest = Path.Combine(work, "copydest");
    if (Directory.Exists(dest)) Directory.Delete(dest, true);
    Directory.CreateDirectory(dest);

    var plan = ListCopy.Plan(books, dest);
    Check(plan.Items.Select(i => Path.GetFileName(i.Target)).SequenceEqual(new[] { "a.epub", "a (2).epub", "b.pdf" }),
        "plan: same file names kept apart: " + string.Join(", ", plan.Items.Select(i => Path.GetFileName(i.Target))));
    Check(plan.OfflineBooks == 1 && plan.OfflineDrives.SequenceEqual(new[] { "Old drive" }), "plan: the unplugged drive is named");
    Check(plan.MissingFiles == 1, "plan: the missing file is counted");
    var progress = new Progress<ListCopy.Progress>(_ => { });
    var r1 = plan.RunAsync(false, progress, CancellationToken.None).Result;
    Check(r1 is { Copied: 3, Skipped: 0, Failed: 0, Cancelled: false }, $"copied 3 ({r1})");
    Check(File.ReadAllText(Path.Combine(dest, "a (2).epub")) == "second a", "the second a.epub is the second file");
    Check(plan.Existing().Count == 3, "all three are there now");
    var r2 = ListCopy.Plan(books, dest).RunAsync(false, progress, CancellationToken.None).Result;
    Check(r2 is { Copied: 0, Skipped: 3 }, $"skip keeps them ({r2})");
    File.WriteAllText(Path.Combine(dest, "b.pdf"), "changed at the destination");
    var r3 = ListCopy.Plan(books, dest).RunAsync(true, progress, CancellationToken.None).Result;
    Check(r3 is { Copied: 3, Skipped: 0 } && File.ReadAllText(Path.Combine(dest, "b.pdf")) == "b", $"replace writes them again ({r3})");
    Check(ListCopy.FreeBytes(dest) > 0, "free space is known for a local folder");

    // Stop half way through the big file: nothing half-written is left under any name.
    var bigPlan = ListCopy.Plan(new[] { B("Two\\big.pdf") }, dest);
    using var cts = new CancellationTokenSource();
    var stopper = new Progress<ListCopy.Progress>(p => { if (p.BytesDone > (20L << 20)) cts.Cancel(); });
    var r4 = bigPlan.RunAsync(false, stopper, cts.Token).Result;
    Thread.Sleep(200);
    Check(r4.Cancelled && r4.Copied == 0, $"stopped ({r4})");
    Check(!File.Exists(Path.Combine(dest, "big.pdf")) && !File.Exists(Path.Combine(dest, "big.pdf.partial")), "no half-copied file left");
    File.Delete(big);
}

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
