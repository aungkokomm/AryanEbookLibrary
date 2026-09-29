using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// KFX books are read through an EPUB copy made by boko (vendor\boko). The KFX here is made by boko too, from a small
/// EPUB with a Burmese title and author, so the test needs nothing but the repository.
/// </summary>
public sealed class KfxBookTests : IDisposable
{
    private const string Title = "မှန်၏ မှောင်ရိပ်";
    private const string Author = "ကြည်အေး";
    private const string Sentence = "မှန်ထဲတွင် သူ့မျက်နှာကို တွေ့လိုက်ရသည်။";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aryan-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _cache;

    public KfxBookTests()
    {
        Directory.CreateDirectory(_dir);
        _cache = Path.Combine(_dir, "cache");
        KfxBook.Converter = Boko;
        KfxBook.CacheDir = _cache;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private static string Boko
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var exe = Path.Combine(dir.FullName, "vendor", "boko", "boko.exe");
                if (File.Exists(exe)) return exe;
            }
            throw new FileNotFoundException("vendor\\boko\\boko.exe is not in the repository");
        }
    }

    /// <summary>A KFX book made by boko from a two-chapter EPUB.</summary>
    private string Kfx(string name = "book.kfx")
    {
        var epub = Path.Combine(_dir, "source.epub");
        using (var zip = new ZipArchive(File.Create(epub), ZipArchiveMode.Create))
        {
            void Put(string entry, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Put("mimetype", "application/epub+zip");
            Put("META-INF/container.xml", """
                <?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                <rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            for (var i = 1; i <= 2; i++)
                Put($"OPS/c{i}.xhtml", $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <html xmlns="http://www.w3.org/1999/xhtml"><head><title>c{i}</title></head>
                    <body><h1>အခန်း {i}</h1><p>{Sentence}</p><p>Chapter {i} in English too.</p></body></html>
                    """);
            Put("OPS/nav.xhtml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>toc</title></head>
                <body><nav epub:type="toc"><ol><li><a href="c1.xhtml">အခန်း ၁</a></li><li><a href="c2.xhtml">အခန်း ၂</a></li></ol></nav></body></html>
                """);
            Put("OPS/package.opf", $"""
                <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">kfx-test</dc:identifier>
                <dc:title>{Title}</dc:title><dc:creator>{Author}</dc:creator><dc:language>my</dc:language></metadata>
                <manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/></manifest>
                <spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>
                """);
        }
        var kfx = Path.Combine(_dir, name);
        var start = new ProcessStartInfo(Boko) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "convert", epub, kfx }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(kfx), "boko could not make the test KFX: " + error);
        return kfx;
    }

    private static string Text(string epub)
    {
        using var zip = ZipFile.OpenRead(epub);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)))
        {
            using var r = new StreamReader(entry.Open());
            text.Append(r.ReadToEnd());
        }
        return text.ToString();
    }

    [Fact]
    public void Kfx_is_a_book_format_of_its_own()
    {
        Assert.Equal(BookFormat.Kfx, FormatHelper.FromPath(@"E:\Books\joy_of_life.KFX"));
        Assert.True(FormatHelper.IsSupported("book.kfx"));
        Assert.Equal("KFX", FormatHelper.Label(BookFormat.Kfx));
    }

    [Fact]
    public async Task A_kfx_book_is_read_through_an_epub_copy_with_its_text_and_details()
    {
        var (epub, problem) = await KfxBook.EpubAsync(Kfx());

        Assert.Null(problem);
        Assert.NotNull(epub);
        Assert.StartsWith(_cache, epub);
        Assert.Contains(Sentence, Text(epub!));
        var details = EpubReader.Read(epub!);
        Assert.Equal(Title, details.Title);
        Assert.Equal(Author, details.Author);
    }

    [Fact]
    public async Task The_library_reads_a_kfx_books_details()
    {
        var details = await MetadataService.ReadAsync(Kfx(), BookFormat.Kfx, useCalibre: false, readCover: false);

        Assert.Equal(Title, details.Title);
        Assert.Equal(Author, details.Author);
    }

    [Fact]
    public async Task The_copy_is_made_once_and_shared_by_every_copy_of_the_book()
    {
        var kfx = Kfx();
        var (first, _) = await KfxBook.EpubAsync(kfx);
        var written = File.GetLastWriteTimeUtc(first!);

        var renamed = Path.Combine(_dir, "moved and renamed.kfx");
        File.Copy(kfx, renamed);
        var (again, _) = await KfxBook.EpubAsync(kfx);
        var (other, _) = await KfxBook.EpubAsync(renamed);

        Assert.Equal(first, again);
        Assert.Equal(first, other);
        Assert.Equal(written, File.GetLastWriteTimeUtc(first!));
        Assert.Single(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task A_locked_kindle_store_book_says_so_and_is_not_converted()
    {
        var locked = Path.Combine(_dir, "store.kfx");
        File.WriteAllBytes(locked, [0xEA, (byte)'D', (byte)'R', (byte)'M', (byte)'I', (byte)'O', (byte)'N', 0xEE, 0, 0, 0, 0]);

        var (epub, problem) = await KfxBook.EpubAsync(locked);

        Assert.Null(epub);
        Assert.True(problem!.Locked);
        Assert.False(Directory.Exists(_cache) && Directory.GetFiles(_cache).Length > 0);
    }

    [Fact]
    public async Task A_missing_converter_is_a_plain_problem_not_a_crash()
    {
        var kfx = Kfx();
        KfxBook.Converter = Path.Combine(_dir, "no-such-boko.exe");

        var (epub, problem) = await KfxBook.EpubAsync(kfx);

        Assert.Null(epub);
        Assert.False(problem!.Locked);
        Assert.Contains("missing", problem.Text);
    }
}
