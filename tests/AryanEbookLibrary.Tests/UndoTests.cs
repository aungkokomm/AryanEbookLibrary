using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Tests;

/// <summary>Ctrl+Z: one step per action, only what the step changed goes back, and the copied list of books.</summary>
public class UndoTests
{
    private static Book B(string title)
    {
        var book = new Book { Title = title, RelPath = title + ".epub", DriveId = "D1" };
        book.KeepFileDetails();   // as the library loads it: what the file says, for an edit to fall back to
        return book;
    }

    /// <summary>A change made the way the app makes it: the book changed, then saved (recorded from its last saved state).</summary>
    private static void Change(UndoHistory history, Book book, Action<Book> change)
    {
        var before = book.LastSaved ?? new BookState();
        change(book);
        var after = book.ToState();
        history.Saved(book.StateKey, before, after);
        book.LastSaved = after;
    }

    [Fact]
    public void Books_changed_in_one_go_undo_together()
    {
        var history = new UndoHistory();
        var books = new[] { B("One"), B("Two"), B("Three") };
        foreach (var b in books) Change(history, b, x => ReadingLog.SetStatus(x, ReadStatus.Finished, DateTime.UtcNow));
        history.EndStep();

        var undone = history.Undo(key => books.FirstOrDefault(b => b.StateKey == key));
        Assert.NotNull(undone);
        Assert.All(books, b => Assert.Equal(ReadStatus.Unread, b.Status));
        Assert.All(books, b => Assert.Equal(0, b.Progress));
        Assert.All(books, b => Assert.Null(b.FinishedUtc));
        Assert.Equal("Undone (reading status): 3 books", undone.Value.Text);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Each_action_is_its_own_step_and_the_last_goes_first()
    {
        var history = new UndoHistory();
        var book = B("Sapiens");
        Change(history, book, b => b.Rating = 4);
        history.EndStep();
        Change(history, book, b => b.IsFavorite = true);
        history.EndStep();

        Assert.Equal("Undone (favorite): Sapiens", history.Undo(_ => book)!.Value.Text);
        Assert.False(book.IsFavorite);
        Assert.Equal(4, book.Rating);
        Assert.Equal("Undone (rating): Sapiens", history.Undo(_ => book)!.Value.Text);
        Assert.Equal(0, book.Rating);
        Assert.Null(history.Undo(_ => book));
    }

    [Fact]
    public void Undo_keeps_what_changed_since_in_other_fields()
    {
        var history = new UndoHistory();
        var book = B("Dune");
        Change(history, book, b => b.Rating = 5);
        history.EndStep();
        // Read on afterwards: progress is saved but is not a step of its own.
        book.Progress = 40;
        book.LastSaved = book.ToState();

        history.Undo(_ => book);
        Assert.Equal(0, book.Rating);
        Assert.Equal(40, book.Progress);
    }

    [Fact]
    public void Lists_tags_and_details_go_back()
    {
        var history = new UndoHistory();
        var book = B("Nagraj");
        Change(history, book, b =>
        {
            b.Lists = ["Comics"];
            b.UserTags = "hindi";
            b.SetCustomDetails("Nagraj 1", null, "Nagraj");
        });
        history.EndStep();

        Assert.Equal("Undone (lists, tags, details): Nagraj", history.Undo(_ => book)!.Value.Text);
        Assert.Empty(book.Lists);
        Assert.Equal("", book.UserTags);
        Assert.Null(book.CustomTitle);
        Assert.Null(book.CustomSeries);
    }

    [Fact]
    public void A_save_that_changes_nothing_is_no_step()
    {
        var history = new UndoHistory();
        var book = B("Same");
        Change(history, book, _ => { });
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Only_the_last_twenty_steps_are_kept()
    {
        var history = new UndoHistory();
        var book = B("Many");
        for (var i = 1; i <= 25; i++)
        {
            var notes = $"note {i}";
            Change(history, book, b => b.Notes = notes);
            history.EndStep();
        }
        var steps = 0;
        while (history.Undo(_ => book) is not null) steps++;
        Assert.Equal(UndoHistory.Depth, steps);
        Assert.Equal("note 5", book.Notes);
    }

    [Fact]
    public void Copied_books_read_as_a_list()
    {
        var a = new Book { Title = "Sapiens", Author = "Yuval Noah Harari", Year = 2011, RelPath = "a.epub" };
        var b = new Book { Title = "Hari Maut", Series = "Nagraj", SeriesIndex = 120, RelPath = "b.pdf" };
        var c = new Book { Title = "Untitled", RelPath = "c.pdf" };
        Assert.Equal(
            "Sapiens by Yuval Noah Harari (2011)" + Environment.NewLine + "Hari Maut (Nagraj #120)" + Environment.NewLine + "Untitled",
            CatalogExport.Lines([a, b, c]));
    }
}
