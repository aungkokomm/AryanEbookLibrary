using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.Services.Metadata;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// The scan at start-up usually finds every book where it was. It must say so, so the library is not read again and
/// every card built again once per folder: the cards flashed for seconds after the app opened.
/// </summary>
public sealed class RescanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aryan-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public RescanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        FileNameParser.Context = NameContext.Empty;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    [Fact]
    public void Marking_the_missing_books_says_whether_any_went_or_came_back()
    {
        using var db = new Database(Path.Combine(_dir, "library.db"));
        var repo = new LibraryRepository(db);
        repo.AddDrive("E1", "Misc (E:)", @"E:\");
        var folder = repo.AddFolder("E1", "Books");
        foreach (var name in new[] { "A", "B" })
            repo.UpsertBook(new Book { FolderId = folder.Id, DriveId = "E1", RelPath = $@"Books\{name}.pdf", Format = BookFormat.Pdf, Title = name, FileSize = 5000 });

        Assert.Equal((0, false), repo.MarkMissing(folder.Id, "E1", [@"Books\A.pdf", @"Books\B.pdf"]));   // all found
        Assert.Equal((1, true), repo.MarkMissing(folder.Id, "E1", [@"Books\A.pdf"]));                    // B went
        Assert.Equal((1, false), repo.MarkMissing(folder.Id, "E1", [@"Books\A.pdf"]));                   // still gone
        Assert.Equal((0, true), repo.MarkMissing(folder.Id, "E1", [@"Books\A.pdf", @"Books\B.pdf"]));    // B came back
    }

    [Fact]
    public void Reading_the_names_again_counts_the_books_it_changed()
    {
        using var db = new Database(Path.Combine(_dir, "library.db"));
        var repo = new LibraryRepository(db);
        repo.AddDrive("E1", "Misc (E:)", @"E:\");
        var folder = repo.AddFolder("E1", "Comics");
        FileNameParser.Context = NameContext.Empty;
        foreach (var name in new[] { "120 Nagraj - Hari Maut", "121 Nagraj - Khooni Khel", "122 Nagraj - Zehreela Barood" })
        {
            var rel = $@"Comics\{name}.pdf";
            var md = new BookMetadata();
            MetadataService.FillFromFileName(md, rel);
            repo.UpsertBook(new Book
            {
                FolderId = folder.Id, DriveId = "E1", RelPath = rel, Format = BookFormat.Pdf, Title = md.Title ?? "", Author = md.Author ?? "",
                Series = md.Series ?? "", SeriesIndex = md.SeriesIndex, NameFields = md.NameFields, FileSize = 1000
            });
        }

        var scanner = new ScannerService(repo);
        Assert.Equal(3, scanner.RefreshNamesFromFiles());   // the series is known now
        Assert.Equal(0, scanner.RefreshNamesFromFiles());   // and nothing more is learned
    }

    [Fact]
    public void A_scan_that_found_everything_where_it_was_changed_nothing()
    {
        Assert.False(new ScanResult { Found = 8068, Skipped = 8068, Missing = 3 }.Changed);   // the 3 were missing before
        Assert.True(new ScanResult { Found = 1, Inserted = 1 }.Changed);
        Assert.True(new ScanResult { Found = 1, Updated = 1 }.Changed);
        Assert.True(new ScanResult { Renamed = 1 }.Changed);
        Assert.True(new ScanResult { MissingChanged = true }.Changed);
        var total = new ScanResult();
        total.Add(new ScanResult { Skipped = 5 });
        total.Add(new ScanResult { MissingChanged = true });
        Assert.True(total.Changed);
    }
}
