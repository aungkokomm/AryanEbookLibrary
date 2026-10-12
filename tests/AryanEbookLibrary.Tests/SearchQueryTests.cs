using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Tests;

/// <summary>The search box's fields: author:, tag:, year:, rating:, format:, is:, quotes and minus.</summary>
public class SearchQueryTests
{
    private static Book B(string title, string author = "", int? year = null, int rating = 0, BookFormat format = BookFormat.Epub,
        string tags = "", ReadStatus status = ReadStatus.Unread, bool favorite = false, string language = "en") =>
        new()
        {
            Title = title, Author = author, Year = year, Rating = rating, Format = format, UserTags = tags,
            Status = status, IsFavorite = favorite, Language = language, RelPath = title + ".epub"
        };

    private static readonly List<Book> Shelf =
    [
        B("War and Peace", "Leo Tolstoy", 1869, 5, BookFormat.Epub, "classics, history", ReadStatus.Finished, favorite: true),
        B("Anna Karenina", "Leo Tolstoy", 1878, 4, BookFormat.Pdf, "classics", ReadStatus.Reading),
        B("Sapiens", "Yuval Noah Harari", 2011, 3, BookFormat.Azw3, "history, lent"),
        B("Homo Deus", "Yuval Noah Harari", 2015, 0, BookFormat.Kfx, "history"),
        B("Nagraj", "", 1995, 0, BookFormat.Cbz, "comics"),
    ];

    private static string[] Find(string search) =>
        Shelf.Where(SearchQuery.Parse(search).Matches).Select(b => b.Title).ToArray();

    [Fact]
    public void Plain_words_are_found_anywhere_as_before() =>
        Assert.Equal(["War and Peace", "Anna Karenina"], Find("tolstoy"));

    [Fact]
    public void A_field_looks_only_there()
    {
        Assert.Equal(["Sapiens", "Homo Deus"], Find("author:harari"));
        Assert.Empty(Find("title:harari"));
    }

    [Fact]
    public void Quotes_keep_a_value_with_spaces_together() =>
        Assert.Equal(["Sapiens", "Homo Deus"], Find("author:\"yuval noah\""));

    [Fact]
    public void A_minus_leaves_books_out() =>
        Assert.Equal(["War and Peace", "Homo Deus"], Find("tag:history -tag:lent"));

    [Fact]
    public void Years_take_ranges_and_bounds()
    {
        Assert.Equal(["War and Peace", "Anna Karenina"], Find("year:1800-1900"));
        Assert.Equal(["Sapiens", "Homo Deus"], Find("year:2000+"));
        Assert.Equal(["Nagraj"], Find("year:1995"));
        Assert.Equal(["War and Peace"], Find("year:<1870"));
    }

    [Fact]
    public void Ratings_and_up()
    {
        Assert.Equal(["War and Peace", "Anna Karenina"], Find("rating:4+"));
        Assert.Equal(["Homo Deus", "Nagraj"], Find("is:unrated"));
    }

    [Fact]
    public void Formats_by_name_or_group()
    {
        Assert.Equal(["Anna Karenina"], Find("format:pdf"));
        Assert.Equal(["Sapiens", "Homo Deus"], Find("format:kindle"));
        Assert.Equal(["Nagraj"], Find("format:comics"));
    }

    [Fact]
    public void Reading_state_and_favorites()
    {
        Assert.Equal(["Anna Karenina"], Find("is:reading"));
        Assert.Equal(["War and Peace"], Find("is:finished is:favorite"));
        Assert.Equal(["Sapiens", "Homo Deus", "Nagraj"], Find("is:unread"));
    }

    [Fact]
    public void Fields_and_words_combine() =>
        Assert.Equal(["Sapiens"], Find("history author:harari -deus"));

    [Fact]
    public void An_unknown_field_is_searched_for_as_typed()
    {
        var b = B("Mission: Impossible");
        Assert.True(SearchQuery.Parse("mission:").IsEmpty);   // a field with nothing after it yet asks nothing
        Assert.True(SearchQuery.Parse("Mission: Impossible").Matches(b));
        Assert.True(SearchQuery.Parse("mission:imp").Matches(new Book { Title = "x", RelPath = "mission:impossible.epub" }));
    }

    [Fact]
    public void Languages_by_code_or_name()
    {
        var burmese = B("မှန်၏ မှောင်ရိပ်", language: "en");   // Burmese letters settle it, whatever the file says
        Assert.True(SearchQuery.Parse("lang:my").Matches(burmese));
        Assert.False(SearchQuery.Parse("lang:my").Matches(Shelf[0]));
        Assert.True(SearchQuery.Parse("lang:en").Matches(Shelf[0]));
    }
}
