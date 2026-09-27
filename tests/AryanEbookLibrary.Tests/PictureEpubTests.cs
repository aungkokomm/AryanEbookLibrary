using System.IO.Compression;
using System.Text;
using AryanEbookLibrary.Reader.Comic;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// A fixed-layout EPUB made only of pictures (Sapiens: A Graphic History) opens in the comic reader, one page at a time
/// in spine order. Any EPUB with something to read on its pages stays in the book reader.
/// </summary>
public sealed class PictureEpubTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aryan-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PictureEpubTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private static string Picture(string name) =>
        $"""<div style="position:absolute"><img alt="background image" src="../images/{name}.jpg" width="581" height="783"/></div>""";

    /// <summary>An EPUB whose spine is <paramref name="bodies"/> in order (page ids p0, p1...), each page's picture
    /// holding its own name as bytes.</summary>
    private string Epub(bool fixedLayout, params string[] bodies)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".epub");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        void Put(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Put("mimetype", "application/epub+zip");
        Put("META-INF/container.xml", """
            <?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
            <rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
            """);
        var items = new StringBuilder();
        var spine = new StringBuilder();
        for (var i = 0; i < bodies.Length; i++)
        {
            items.Append($"""<item id="p{i}" href="xhtml/page{i}.xhtml" media-type="application/xhtml+xml"/>""");
            spine.Append($"""<itemref idref="p{i}"/>""");
            Put($"OPS/xhtml/page{i}.xhtml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:xlink="http://www.w3.org/1999/xlink"><head><title>Sapiens</title></head>
                <body>{bodies[i]}</body></html>
                """);
        }
        foreach (var name in new[] { "cover", "blank", "page001", "page002", "page010" })
            Put($"OPS/images/{name}.jpg", name);
        var layout = fixedLayout ? """<meta property="rendition:layout">pre-paginated</meta>""" : "";
        Put("OPS/package.opf", $"""
            <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0">
            <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sapiens</dc:title>{layout}</metadata>
            <manifest>{items}</manifest><spine>{spine}</spine></package>
            """);
        return path;
    }

    [Fact]
    public void A_fixed_layout_book_of_pictures_reads_as_a_comic_in_spine_order()
    {
        // Spine order, not name order: page010 comes before page002 here.
        var path = Epub(true, Picture("cover"), Picture("blank"), Picture("page010"), Picture("page002"));
        Assert.True(PictureEpub.Is(path));
        using var book = ComicBook.Open(path);
        Assert.Equal(4, book.PageCount);
        Assert.Equal("cover", Encoding.UTF8.GetString(book.ReadPage(0)!));
        Assert.Equal("page010", Encoding.UTF8.GetString(book.ReadPage(2)!));
        Assert.Equal("page002", Encoding.UTF8.GetString(book.ReadPage(3)!));
    }

    [Fact]
    public void Invisible_links_over_a_picture_leave_it_a_picture_page()
    {
        // Sapiens' copyright page: three web addresses at opacity 0 over the picture.
        var links = Picture("page001") + """<a href="http://www.sapienship.co" style="opacity:0">www.sapienship.co</a>""";
        Assert.True(PictureEpub.Is(Epub(true, Picture("cover"), links)));
    }

    [Fact]
    public void An_svg_wrapped_picture_counts()
    {
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 581 783"><image width="581" height="783" xlink:href="../images/page001.jpg"/></svg>""";
        Assert.True(PictureEpub.Is(Epub(true, svg)));
    }

    [Fact]
    public void Text_on_a_page_keeps_the_book_in_the_book_reader()
    {
        // A picture book with live text: the comic reader would lose the words.
        var worded = Picture("page001") + """<p style="position:absolute;top:600px">Once upon a time</p>""";
        Assert.False(PictureEpub.Is(Epub(true, Picture("cover"), worded)));
    }

    [Fact]
    public void A_reflowing_book_with_a_picture_per_chapter_stays_in_the_book_reader()
    {
        Assert.False(PictureEpub.Is(Epub(false, Picture("cover"), Picture("page001"))));
    }

    [Fact]
    public void A_page_of_two_pictures_or_none_keeps_the_book_in_the_book_reader()
    {
        Assert.False(PictureEpub.Is(Epub(true, Picture("cover"), Picture("page001") + Picture("page002"))));
        Assert.False(PictureEpub.Is(Epub(true, Picture("cover"), "<div/>")));
    }

    [Fact]
    public void A_comic_archive_keeps_its_natural_name_order()
    {
        var path = Path.Combine(_dir, "comic.cbz");
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            foreach (var name in new[] { "p10.jpg", "p2.jpg", "p1.jpg" })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(name);
        Assert.False(PictureEpub.Is(path));
        using var book = ComicBook.Open(path);
        Assert.Equal(new[] { "p1.jpg", "p2.jpg", "p10.jpg" }, Enumerable.Range(0, 3).Select(i => Encoding.UTF8.GetString(book.ReadPage(i)!)));
    }

    [Theory]
    [InlineData("epub1;epubcfi(/6/104)", 51)] // the place saved in the real Sapiens, volume 2
    [InlineData("epub1;epubcfi(/6/104!/4/2/2)", 51)]
    [InlineData("epub1;epubcfi(/6/2)", 0)]
    [InlineData("comic1", null)]
    [InlineData("epub1;something else", null)]
    [InlineData("", null)]
    public void The_book_readers_place_names_the_page(string position, int? page) =>
        Assert.Equal(page, PictureEpub.SpinePage(position));
}
