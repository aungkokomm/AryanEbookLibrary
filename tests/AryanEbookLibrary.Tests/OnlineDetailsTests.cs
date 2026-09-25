using System.Text.Json;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services.Online;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// A recorded Google Books answer read, matched and merged the way the app does it: only empty details are filled,
/// each says where it came from, and nothing uncertain is filled at all.
/// </summary>
public class OnlineDetailsTests
{
    private const string Recorded = """
        {"items":[
          {"id":"x1","volumeInfo":{"title":"Becoming the Hacker","subtitle":"The Playbook for Getting Inside the Mind of the Attacker",
           "authors":["Adrian Pruteanu"],"publisher":"Packt Publishing Ltd","publishedDate":"2019-01-31",
           "description":"<p>Web penetration testing by becoming an ethical hacker.</p><p>Protect the web by learning the tools &amp; tricks.</p>",
           "categories":["Computers / Security / General"],"industryIdentifiers":[{"type":"ISBN_13","identifier":"9781788627962"},{"type":"ISBN_10","identifier":"1788627962"}]}},
          {"id":"x2","volumeInfo":{"title":"Becoming a Hacker Chef","authors":["Someone Else"],"publishedDate":"2001"}}
        ]}
        """;

    private static List<OnlineCandidate> Found()
    {
        using var json = JsonDocument.Parse(Recorded);
        return GoogleBooksClient.Volumes(json.RootElement, byIsbn: false).ToList();
    }

    private static OnlineDetails Google(OnlineCandidate c, string status = OnlineDetails.Found) => new()
    {
        Source = OnlineSource.GoogleBooks, Status = status, How = OnlineDetails.ByMatch, SourceKey = c.WorkKey,
        Title = c.Title, Author = c.AuthorText, Publisher = c.Publisher, Year = c.Year,
        Subjects = string.Join(", ", c.Subjects), Description = c.Description
    };

    private static Book Fresh(string publisher = "", int? year = null, string description = "")
    {
        var b = new Book { Title = "Becoming the Hacker", Author = "Adrian Pruteanu", Publisher = publisher, Year = year, Description = description };
        b.KeepFileDetails();
        return b;
    }

    [Fact]
    public void An_answer_reads_title_author_year_publisher_and_isbns()
    {
        var c = Found()[0];
        Assert.Equal("Becoming the Hacker", c.Title);
        Assert.Equal(["Adrian Pruteanu"], c.Authors);
        Assert.Equal(2019, c.Year);
        Assert.Equal("Packt Publishing Ltd", c.Publisher);
        Assert.Contains("9781788627962", c.Isbns);
    }

    [Fact]
    public void Categories_become_subjects_without_general() => Assert.Equal(["Computers", "Security"], Found()[0].Subjects);

    [Fact]
    public void The_description_loses_its_html_and_keeps_its_paragraphs()
    {
        var text = Found()[0].Description!.Replace("\n\n", "\n");
        Assert.Equal("Web penetration testing by becoming an ethical hacker.\nProtect the web by learning the tools & tricks.", text);
    }

    [Fact]
    public void Googles_first_answer_ranks_first()
    {
        var found = Found();
        Assert.True(found[0].EditionCount > found[1].EditionCount);
    }

    [Fact]
    public void Title_and_author_agreeing_is_a_match_and_the_other_book_is_not_taken()
    {
        var look = new LookupBook("k", "Becoming the Hacker", "Adrian Pruteanu", false, "[smtebooks.com] Becoming the Hacker 1st Edition", null);
        var d = OnlineMatcher.Decide(look, null, Found());
        Assert.Equal(OnlineDetails.Found, d.Status);
        Assert.Equal("Becoming the Hacker", d.Candidate?.Title);
    }

    [Fact]
    public void Another_authors_book_of_the_same_name_is_not_filled_in()
    {
        var stranger = new LookupBook("k", "Becoming the Hacker", "Mary Smith", false, "x", null);
        Assert.NotEqual(OnlineDetails.Found, OnlineMatcher.Decide(stranger, null, Found()).Status);
    }

    [Fact]
    public void Empty_details_are_filled_and_say_where_they_came_from()
    {
        var b = Fresh();
        b.SetOnline(Google(Found()[0]));
        Assert.StartsWith("Web penetration", b.Description);
        Assert.Equal(OnlineSource.GoogleBooks, b.DescriptionSource);
        Assert.Equal(2019, b.Year);
        Assert.Equal(OnlineSource.GoogleBooks, b.YearSource);
        Assert.Equal("Packt Publishing Ltd", b.Publisher);
        Assert.Equal("Computers, Security", b.Subjects);
        Assert.Equal("Google Books", OnlineSource.Name(b.YearSource));
    }

    [Fact]
    public void The_books_own_details_are_never_replaced()
    {
        var b = Fresh(publisher: "Packt", year: 2018, description: "The book's own words.");
        b.SetOnline(Google(Found()[0]));
        Assert.Equal("Packt", b.Publisher);
        Assert.Equal(2018, b.Year);
        Assert.Equal("The book's own words.", b.Description);
    }

    [Fact]
    public void Open_library_comes_before_google()
    {
        var b = Fresh();
        b.SetOnline(new OnlineDetails
        {
            Source = OnlineSource.OpenLibrary, Status = OnlineDetails.Found, How = OnlineDetails.ByMatch,
            Title = "Becoming the Hacker", Year = 2018, Description = "Open Library's words.", Publisher = "Packt"
        });
        b.SetOnline(Google(Found()[0]));
        Assert.Equal("Open Library's words.", b.Description);
        Assert.Equal(2018, b.Year);
        Assert.Equal("Packt", b.Publisher);
    }

    [Fact]
    public void A_suggestion_fills_nothing()
    {
        var b = Fresh();
        b.SetOnline(Google(Found()[0], OnlineDetails.Suggested));
        Assert.Equal("", b.Description);
        Assert.Null(b.Year);
    }
}
