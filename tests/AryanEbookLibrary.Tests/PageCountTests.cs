using System.IO.Compression;
using System.Text;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Tests;

/// <summary>Every book's length: an EPUB's estimated from its text, the pages left in a book, and the pages: search.</summary>
public class PageCountTests
{
    private static ZipArchive Epub(params (string Name, string Body)[] docs)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in docs)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(body);
            }
            using (var css = new StreamWriter(zip.CreateEntry("OEBPS/style.css").Open())) css.Write(new string('x', 50_000));
        }
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static string Page(int letters) =>
        "<html><head><title>Chapter</title></head><body><p class=\"text\">" + new string('a', letters) + "</p>\n\n   </body></html>";

    [Fact]
    public void An_epub_is_as_long_as_its_text_without_tags()
    {
        // 9,000 letters of text in two chapters, a few more in their titles: five printed pages. The stylesheet is not text.
        using var zip = Epub(("OEBPS/one.xhtml", Page(4500)), ("OEBPS/two.html", Page(4500)));
        Assert.Equal(5, EpubReader.EstimatePages(zip));
    }

    [Fact]
    public void A_picture_book_has_a_page_per_document()
    {
        var pages = Enumerable.Range(1, 12).Select(i => ($"OEBPS/p{i}.xhtml", "<html><body><img src=\"p.jpg\"/></body></html>")).ToArray();
        using var zip = Epub(pages);
        Assert.Equal(12, EpubReader.EstimatePages(zip));
    }

    [Fact]
    public void A_book_with_no_chapters_has_no_length() =>
        Assert.Equal(0, EpubReader.EstimatePages(Epub()));

    [Fact]
    public void Pages_left_follow_the_progress()
    {
        Assert.Equal(150, new Book { PageCount = 200, Progress = 25, Status = ReadStatus.Reading }.PagesLeft);
        Assert.Equal(0, new Book { PageCount = 200, Progress = 25, Status = ReadStatus.Finished }.PagesLeft);
        Assert.Equal(-1, new Book { PageCount = 0 }.PagesLeft);
    }

    [Fact]
    public void An_estimate_says_about()
    {
        Assert.Equal("about 320 pages", new Book { PageCount = 320, Format = BookFormat.Epub }.PagesText);
        Assert.Equal("1,204 pages", new Book { PageCount = 1204, Format = BookFormat.Pdf }.PagesText);
        Assert.Equal("", new Book { Format = BookFormat.Pdf }.PagesText);
    }

    [Fact]
    public void The_reading_log_adds_up_each_months_pages()
    {
        Book Done(int pages, int month) => new()
        {
            PageCount = pages, Status = ReadStatus.Finished, RelPath = $"{pages}-{month}.pdf",
            FinishedUtc = new DateTime(2026, month, 15, 12, 0, 0, DateTimeKind.Utc)
        };
        var year = ReadingLog.Year([Done(300, 1), Done(200, 1), Done(0, 1), Done(450, 3)], 2026);
        var pages = ReadingLog.PagesByMonth(year);
        Assert.Equal(500, pages[0]);
        Assert.Equal(0, pages[1]);
        Assert.Equal(450, pages[2]);
        Assert.Equal(3, year.Months[0]);   // the book of unknown length is still a book finished
    }

    [Fact]
    public void Pages_search_finds_a_length_and_never_an_unknown_one()
    {
        var shelf = new[]
        {
            new Book { Title = "Short", PageCount = 90, RelPath = "a.pdf" },
            new Book { Title = "Long", PageCount = 900, RelPath = "b.pdf" },
            new Book { Title = "Unknown", RelPath = "c.pdf" },
        };
        string[] Find(string q) => shelf.Where(SearchQuery.Parse(q).Matches).Select(b => b.Title).ToArray();
        Assert.Equal(["Short"], Find("pages:<300"));
        Assert.Equal(["Long"], Find("length:500+"));
        Assert.Equal(["Short", "Long"], Find("pages:1-1000"));
    }
}
