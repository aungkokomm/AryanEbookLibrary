using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Reader.Pdf;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// PDF metadata:
///  - Title/Author/Keywords: PdfPig reads the document Info dictionary, then XMP (dc:title / dc:creator).
///    It parses the PDF properly, so compressed object streams and encrypted-for-editing files work.
///    The earlier raw text scan took the last "/Title" anywhere in the file, which on a real library was
///    mostly bookmark headings, image names and scrambled bytes; measured on 1,534 PDFs, PdfPig finds 206
///    more real titles and 141 more authors. Nothing usable means the filename becomes the title.
///  - ISBN: the text of the first 6 and last 2 pages (copyright page, back cover), checksum-validated, as
///    Zotero does. 26% of a real library's PDFs without an ISBN in their metadata have one there.
///  - Whether page 1 is a text page (a scan's notes, a copyright page) rather than a cover.
///  - Cover + page count: rendered by the built-in Windows.Data.Pdf renderer (page 1), or by PDFium when Windows cannot.
/// </summary>
public static class PdfReader
{
    public static async Task<BookMetadata> ReadAsync(string path, bool readCover = true)
    {
        var md = new BookMetadata();

        try
        {
            ReadDocumentInfo(path, md);
        }
        catch (Exception ex)
        {
            // damaged, or needs a password to open: the filename becomes the title
            Log.Write($"PDF metadata failed ({Path.GetFileName(path)}): {ex.Message}");
        }

        for (var attempt = 1; readCover; attempt++)
        {
            try
            {
                await RenderFirstPageAsync(path, md);
                if (attempt > 1) Log.Write($"PDF render retry worked ({Path.GetFileName(path)})");
                break;
            }
            catch (Exception ex) when (attempt == 1 && ex.HResult == RpcWrongThread)
            {
                // Windows sometimes fails to open the file with RPC_E_WRONG_THREAD (5 of 1,534 PDFs in one scan,
                // none in two others). Try once more; the log line above says whether that helps.
            }
            catch (Exception ex)
            {
                // Windows cannot draw some PDFs that PDFium, the reader's engine, can (two Burmese books in a real
                // library, with an empty error). Password-protected or damaged ones: keep whatever metadata was found.
                if (!await RenderFirstPageWithPdfiumAsync(path, md))
                    Log.Write($"PDF render failed ({Path.GetFileName(path)}): {ex.Message}");
                break;
            }
        }

        await CollectEveryFewDocumentsAsync();
        return md;
    }

    private const int RpcWrongThread = unchecked((int)0x8001010E);

    private static async Task RenderFirstPageAsync(string path, BookMetadata md)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var doc = await PdfDocument.LoadFromFileAsync(file);
        md.PageCount = (int)doc.PageCount;
        if (doc.PageCount == 0) return;

        using var page = doc.GetPage(0);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = 480,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);

        md.Cover = bytes;
        md.CoverExt = ".png";
    }

    /// <summary>Page 1 by PDFium, as the same 480 px PNG, or false when PDFium cannot open the file either.</summary>
    private static async Task<bool> RenderFirstPageWithPdfiumAsync(string path, BookMetadata md)
    {
        (int Width, int Height, byte[] Bgra)? page;
        int pages;
        try
        {
            var (book, _) = PdfBook.Open(path);
            if (book is null) return false;
            using (book)
            {
                pages = book.PageCount;
                page = pages > 0 ? book.RenderPage(0, 480) : null;
            }
        }
        catch (Exception)
        {
            return false;   // no native engine (a harness without it) or a file it chokes on
        }
        if (page is not { } p) return false;

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(p.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, p.Width, p.Height, BitmapAlphaMode.Ignore);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);

        md.PageCount = pages;
        md.Cover = bytes;
        md.CoverExt = ".png";
        Log.Write($"PDF cover drawn by PDFium ({Path.GetFileName(path)})");
        return true;
    }

    private static int _documentsSinceCollect;

    /// <summary>
    /// Windows.Data.Pdf keeps each loaded document's native memory (several MB) until finalizers run, and the
    /// GC cannot see that memory, so it hardly ever runs: scanning 400 PDFs grew the process to 3.9 GB.
    /// Releasing the WinRT objects by hand (document, file, page, async operations) did not free it; only a
    /// collection that waits for finalizers does. Doing that every 20 documents keeps a scan flat.
    /// Runs on the thread pool so it can never block the UI thread.
    /// </summary>
    private static async Task CollectEveryFewDocumentsAsync()
    {
        if (Interlocked.Increment(ref _documentsSinceCollect) % 20 != 0) return;
        await Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
    }

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    private static void ReadDocumentInfo(string path, BookMetadata md)
    {
        // A stream, so PdfPig reads only the parts it needs instead of loading a 100 MB file into memory.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = PigDocument.Open(fs);

        var info = doc.Information;
        var title = XmlUtil.Clean(info.Title);
        var authors = SplitAuthors(info.Author);

        if (title is null || LooksLikeJunkTitle(title) || authors.Count == 0)
        {
            try
            {
                if (doc.TryGetXmpMetadata(out var xmp))
                {
                    var x = xmp.GetXDocument();
                    if (title is null || LooksLikeJunkTitle(title)) title = XmlUtil.Clean(XmpValues(x, "title").FirstOrDefault());
                    if (authors.Count == 0) authors = XmpValues(x, "creator").SelectMany(SplitAuthors).ToList();
                }
            }
            catch (Exception ex)
            {
                // Damaged XMP (not XML at all in 4 of 1,534 PDFs): keep what the Info dictionary gave.
                Log.Write($"PDF XMP unreadable ({Path.GetFileName(path)}): {ex.Message}");
            }
        }

        if (title is not null) md.Isbn = Isbn.Find(title.Replace('_', ' '));   // a title that is only "isbn_0671818325"
        if (title is not null && !LooksLikeJunkTitle(title)) md.Title = CopyOf.Replace(title, "");
        if (authors.Count > 0) md.Author = string.Join(", ", authors);   // same separator as EPUB authors
        if (XmlUtil.Clean(info.Keywords) is { } kw) md.Subjects = kw;

        ReadPageText(doc, md);
    }

    /// <summary>ISBN from the first 6 and last 2 pages, and whether page 1 is a page of text.</summary>
    private static void ReadPageText(PigDocument doc, BookMetadata md)
    {
        var count = doc.NumberOfPages;
        if (count == 0) return;
        var pages = Enumerable.Range(1, Math.Min(6, count)).Concat(Enumerable.Range(Math.Max(7, count - 1), Math.Max(0, Math.Min(2, count - 6))));
        foreach (var number in pages)
        {
            string text;
            try
            {
                text = doc.GetPage(number).Text;
            }
            catch
            {
                continue;   // one damaged page does not stop the others
            }
            if (number == 1) md.CoverIsTextPage = text.Count(char.IsLetter) > 600;
            if (md.Isbn is null && Isbn.Find(text) is { } isbn) md.Isbn = isbn;
            if (md.Isbn is not null) break;
            if (number == 2 && md.Isbn is null && text.Length == 0 && !md.CoverIsTextPage) break;   // a scan: no text layer
        }
    }

    /// <summary>Values of a Dublin Core element in XMP: the rdf:li entries of its Alt/Seq/Bag, or its text.</summary>
    private static IEnumerable<string> XmpValues(XDocument x, string name)
    {
        foreach (var el in x.Descendants(Dc + name))
        {
            var items = el.Descendants(Rdf + "li").Select(li => li.Value).ToList();
            foreach (var v in items.Count > 0 ? items : new List<string> { el.Value })
                if (!string.IsNullOrWhiteSpace(v)) yield return v;
        }
    }

    /// <summary>"Stoltz, Dustin;Taylor, Marshall;" and "Arden, John B." become "Dustin Stoltz", "Marshall Taylor", "John B. Arden".</summary>
    private static List<string> SplitAuthors(string? raw) =>
        PeopleParser.Parse(XmlUtil.Clean(raw))
            .Where(a => !LooksLikeJunkAuthor(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly Regex FileNameLike = new(
        @"\.(docx?|indd|pages|rtf|tex|dvi|ps|qxd|pub|odt|pdf|cdr|eps|ai|psd|jpe?g|png|tiff?|pptx?|xlsx?|html?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] JunkTitlePrefixes = {
        "Microsoft Word", "Microsoft PowerPoint", "PowerPoint", "Untitled", "Scanned using", "Scanned by", "Scanned with",
        // stamps left by scanners and readers, found on 36 books of a real library
        "ACDSee", "GonVisor", "Full page photo", "Print Job"
    };

    /// <summary>Editor placeholders and machine names: "&lt;Name of Project&gt;", "Document1", "Layout 1", a path, a hash.</summary>
    private static readonly Regex PlaceholderTitle = new(
        @"^(<[^>]*>|(new\s+)?document\s*\d*|layout\s*\d+|book\s*\d+|title|no title|cover|ebook|[a-z]:\\.*|/.*|[0-9a-f]{16,}|isbn[\s_:-]*[\dxX-]{10,17})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CopyOf = new(@"^copy of\s+", RegexOptions.IgnoreCase);   // "Copy of ACK 303 Senapati Bapat"

    private static readonly HashSet<string> JunkAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "admin", "administrator", "owner", "unknown", "desconocido", "author", "default", "windows user", "pc"
    };

    /// <summary>Titles that are really file names, editor defaults or noise (":", "3", scrambled bytes).</summary>
    private static bool LooksLikeJunkTitle(string t)
    {
        t = t.Trim();
        if (t.Count(char.IsLetter) < 2) return true;
        if (JunkTitlePrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        return FileNameLike.IsMatch(t) || PlaceholderTitle.IsMatch(t);
    }

    /// <summary>Account names that editors write as the author ("User", "OWadmin", "0008471").</summary>
    private static bool LooksLikeJunkAuthor(string a)
    {
        a = a.Trim();
        if (a.Count(char.IsLetter) < 2) return true;
        return JunkAuthors.Contains(a) || a.EndsWith("admin", StringComparison.OrdinalIgnoreCase);
    }
}
