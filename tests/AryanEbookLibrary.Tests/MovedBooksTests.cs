using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// Files sorted by hand in Explorer: after a scan, a missing book and a fresh one that are certainly the same file become
/// one book again, with everything the user gave it. Anything less than certain stays missing.
/// </summary>
public sealed class MovedBooksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aryan-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public MovedBooksTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private static MissingEntry Gone(long id, string rel, long size, string drive = "E1", long folder = 1) =>
        new(id, drive, "", rel, "", "", size, BookFormat.Pdf, folder);

    private static LibraryRepository.FreshBook Here(long id, string rel, long size, string drive = "E1", long folder = 1) =>
        new(id, drive, rel, size, folder);

    [Fact]
    public void A_file_moved_to_another_folder_is_the_same_book()
    {
        var moves = MissingBooks.PairMoves([Gone(1, @"Books\Sapiens.pdf", 5000)],
            [Here(7, @"Books\History\Sapiens.pdf", 5000), Here(8, @"Books\Other.pdf", 5000)]);
        var m = Assert.Single(moves);
        Assert.Equal(1, m.Book.Id);
        Assert.Equal(@"Books\History\Sapiens.pdf", m.NewRelPath);
    }

    [Fact]
    public void A_file_moved_to_another_drive_is_the_same_book() =>
        Assert.Equal("D2", Assert.Single(MissingBooks.PairMoves([Gone(1, @"Books\Sapiens.pdf", 5000)],
            [Here(7, @"Cha Cha CD\Sapiens.pdf", 5000, drive: "D2", folder: 2)])).NewDriveId);

    [Fact]
    public void A_file_renamed_in_its_folder_is_the_same_book() =>
        Assert.Single(MissingBooks.PairMoves([Gone(1, @"Books\sapiens (1).pdf", 5000)],
            [Here(7, @"Books\Sapiens - Yuval Noah Harari.pdf", 5000), Here(8, @"Books\Other.epub", 5000)]));

    [Theory]
    [InlineData(@"Books\A\Sapiens.pdf", @"Books\B\Sapiens.pdf")]   // two copies could be it
    public void Two_files_that_could_be_it_leave_it_missing(string one, string two) =>
        Assert.Empty(MissingBooks.PairMoves([Gone(1, @"Books\Sapiens.pdf", 5000)], [Here(7, one, 5000), Here(8, two, 5000)]));

    [Fact]
    public void Two_books_that_could_be_the_file_stay_missing() =>
        Assert.Empty(MissingBooks.PairMoves([Gone(1, @"Books\A\Sapiens.pdf", 5000), Gone(2, @"Books\B\Sapiens.pdf", 5000)],
            [Here(7, @"Books\C\Sapiens.pdf", 5000)]));

    [Fact]
    public void A_different_size_or_a_rename_into_another_folder_is_not_certain()
    {
        Assert.Empty(MissingBooks.PairMoves([Gone(1, @"Books\Sapiens.pdf", 5000)], [Here(7, @"Books\History\Sapiens.pdf", 5001)]));
        Assert.Empty(MissingBooks.PairMoves([Gone(1, @"Books\Sapiens.pdf", 5000)], [Here(7, @"Books\History\Other name.pdf", 5000)]));
        Assert.Empty(MissingBooks.PairMoves([Gone(1, @"Books\Sapiens.pdf", 0)], [Here(7, @"Books\History\Sapiens.pdf", 0)]));
    }

    [Fact]
    public void Moving_to_another_drive_takes_everything_and_a_book_with_the_users_things_never_gives_way()
    {
        using var db = new Database(Path.Combine(_dir, "library.db"));
        var repo = new LibraryRepository(db);
        repo.AddDrive("E1", "Misc (E:)", @"E:\");
        repo.AddDrive("D2", "Data (D:)", @"D:\");
        var books = repo.AddFolder("E1", "Books");
        var chacha = repo.AddFolder("D2", "Cha Cha CD");

        var old = new Book { FolderId = books.Id, DriveId = "E1", RelPath = @"Books\Sapiens.pdf", Format = BookFormat.Pdf, Title = "Sapiens", FileSize = 5000 };
        var fresh = new Book { FolderId = chacha.Id, DriveId = "D2", RelPath = @"Cha Cha CD\Sapiens.pdf", Format = BookFormat.Pdf, Title = "Sapiens", FileSize = 5000 };
        var read = new Book { FolderId = chacha.Id, DriveId = "D2", RelPath = @"Cha Cha CD\Read.pdf", Format = BookFormat.Pdf, Title = "Read", FileSize = 7000 };
        repo.UpsertBook(old);
        repo.UpsertBook(fresh);
        repo.UpsertBook(read);
        repo.UpsertState("E1", old.RelPath, old.StateKey, new BookState { IsFavorite = true, Notes = "mine", UpdatedUtc = DateTime.UtcNow });
        repo.UpsertState("D2", read.RelPath, read.StateKey, new BookState { Progress = 40, UpdatedUtc = DateTime.UtcNow });
        repo.UpsertOnline(old.StateKey, new OnlineDetails { Source = OnlineSource.GoogleBooks, Status = OnlineDetails.Found, Description = "found online" });
        repo.MarkMissing(books.Id, "E1", []);

        var freshBooks = repo.GetFreshBooks();
        Assert.Contains(freshBooks, f => f.RelPath == fresh.RelPath);
        Assert.DoesNotContain(freshBooks, f => f.RelPath == read.RelPath);   // it has the user's progress

        var move = Assert.Single(MissingBooks.PairMoves(repo.GetMissing(), freshBooks));
        MissingBooks.Relink(repo, move);

        Assert.Empty(repo.GetMissing());
        var all = repo.LoadAll();
        var sapiens = Assert.Single(all, b => b.Title == "Sapiens");
        Assert.Equal("D2", sapiens.DriveId);
        Assert.Equal(fresh.RelPath, sapiens.RelPath);
        Assert.True(sapiens.IsFavorite);
        Assert.Equal("mine", sapiens.Notes);
        Assert.Equal("found online", sapiens.OnlineFrom(OnlineSource.GoogleBooks)?.Description);
        Assert.Equal(40, Assert.Single(all, b => b.Title == "Read").Progress);
    }
}
