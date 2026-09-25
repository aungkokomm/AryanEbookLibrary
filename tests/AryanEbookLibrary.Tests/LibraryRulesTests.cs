using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Tests;

/// <summary>The rules behind the Authors offer, tags and the reading log.</summary>
public class LibraryRulesTests
{
    private static Book SiteBook(string relPath, string publisher = "oshoworld.com")
    {
        var b = new Book { Title = Path.GetFileNameWithoutExtension(relPath), Author = "", Publisher = publisher, RelPath = relPath };
        b.KeepFileDetails();
        return b;
    }

    private static List<Book> OshoShelf(int count) =>
        Enumerable.Range(1, count).Select(i => SiteBook($@"Books\Osho\Osho Hindi Book\{i:000}_Book.pdf")).ToList();

    [Fact]
    public void Site_books_with_no_author_in_a_persons_folder_are_offered_that_name()
    {
        var offer = Assert.Single(AuthorSuggester.Build(OshoShelf(3), []));
        Assert.Equal("Osho", offer.Name);
        Assert.Equal("oshoworld.com", offer.Site);
        Assert.Equal(3, offer.Books.Count);
        Assert.Equal("Set Osho as their author", offer.Answer);
    }

    [Fact]
    public void Two_books_are_not_enough() => Assert.Empty(AuthorSuggester.Build(OshoShelf(2), []));

    [Fact]
    public void A_no_is_remembered()
    {
        var offer = Assert.Single(AuthorSuggester.Build(OshoShelf(3), []));
        Assert.Empty(AuthorSuggester.Build(OshoShelf(3), [offer.Key]));
    }

    [Fact]
    public void Books_given_an_author_by_hand_are_not_offered()
    {
        var shelf = OshoShelf(3);
        foreach (var b in shelf) b.SetCustomDetails(null, "Osho", null);
        Assert.Empty(AuthorSuggester.Build(shelf, []));
    }

    [Fact]
    public void A_folder_that_says_what_is_in_it_is_not_a_name()
    {
        var shelf = Enumerable.Range(1, 4).Select(i => SiteBook($@"Books\Hindi\{i}.pdf")).ToList();
        Assert.Empty(AuthorSuggester.Build(shelf, []));
    }

    [Fact]
    public void Tags_merge_once_each_in_the_order_added() =>
        Assert.Equal("Osho, Hindi, Poetry", Tags.Merge("Osho, Hindi", "hindi, Poetry"));

    [Fact]
    public void Renaming_onto_a_tag_already_worn_joins_them() =>
        Assert.Equal("Assorted Magazines, Old", Tags.Rename("Magazines, Assorted Magazines, Old", "Magazines", "Assorted Magazines"));

    [Fact]
    public void Removing_a_tag_leaves_the_others() => Assert.Equal("Hindi, Poetry", Tags.Remove("Hindi, Osho, Poetry", "osho"));

    [Fact]
    public void Finishing_a_book_dates_it_and_finishing_again_keeps_the_date()
    {
        var b = new Book();
        var first = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        ReadingLog.SetStatus(b, ReadStatus.Finished, first);
        Assert.Equal(first, b.FinishedUtc);
        Assert.Equal(100, b.Progress);
        ReadingLog.SetStatus(b, ReadStatus.Finished, first.AddDays(9));
        Assert.Equal(first, b.FinishedUtc);
    }

    [Fact]
    public void Unread_forgets_the_date_and_the_progress()
    {
        var b = new Book();
        ReadingLog.SetStatus(b, ReadStatus.Finished, DateTime.UtcNow);
        ReadingLog.SetStatus(b, ReadStatus.Unread, null);
        Assert.Null(b.FinishedUtc);
        Assert.Equal(0, b.Progress);
    }
}
