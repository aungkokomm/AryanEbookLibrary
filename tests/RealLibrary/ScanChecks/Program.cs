// Runs Aryan's real scanner over the library folders against a COPY of the database (args[0]).
// Book folders are only read. Covers the scan renders go to this harness's own AryanLibrary-Data.
// "online N": looks N books up on Open Library the way the app's background fill does (same matcher,
// repository and Book display rules), then reloads the library and prints what each book now shows.
using System.Diagnostics;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.Services.Metadata;
using AryanEbookLibrary.Services.Online;

AppPaths.Init();
using var db = new Database(args[0]);
var repo = new LibraryRepository(db);
DriveRegistry.Refresh(repo.GetDrives());

if (args.Length > 2 && args[1] == "covers")
{
    // Every cover file in a folder (args[2]), decoded by WIC at 300 px wide as the cards do. Each name is printed
    // before its decode, so a native crash names its file. Only reads.
    int ok = 0, failed = 0;
    var odd = new List<string>();
    foreach (var file in Directory.GetFiles(args[2]).OrderBy(f => f))
    {
        Console.WriteLine("decode " + Path.GetFileName(file));
        Console.Out.Flush();
        try
        {
            var bytes = File.ReadAllBytes(file);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var w = Math.Min(300u, decoder.PixelWidth);
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                ScaledWidth = w,
                ScaledHeight = (uint)Math.Max(1, Math.Round(w * (double)decoder.PixelHeight / decoder.PixelWidth)),
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            if (decoder.PixelWidth > 4000 || decoder.PixelHeight > 6000 || decoder.DecoderInformation.CodecId != Windows.Graphics.Imaging.BitmapDecoder.JpegDecoderId)
                odd.Add($"{Path.GetFileName(file)} {decoder.DecoderInformation.FriendlyName} {decoder.PixelWidth}x{decoder.PixelHeight} {bytes.Length:N0} bytes");
            ok++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"FAIL {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"covers: {ok} decoded, {failed} failed; not plain JPEG or very large: {odd.Count}");
    foreach (var o in odd.Take(40)) Console.WriteLine("   " + o);
    return;
}

if (args.Length > 2 && args[1] == "pdfcover")
{
    // Page 1 of each PDF in a folder (args[2]), by PDFium and by the full metadata reader (files only READ).
    foreach (var path in Directory.GetFiles(args[2], "*.pdf"))
    {
        var (book, status) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(path);
        var page = book?.RenderPage(0, 480);
        book?.Dispose();
        var md = await MetadataService.ReadAsync(path, BookFormat.Pdf, false, readCover: true);
        if (md.Cover is { } saved) File.WriteAllBytes(Path.Combine(args[2], Path.GetFileNameWithoutExtension(path).Length.ToString() + ".png"), saved);
        Console.WriteLine($"{Path.GetFileName(path)}: pdfium {status} {(page is { } p ? $"{p.Width}x{p.Height}" : "no page")}; reader cover {(md.Cover is { } c ? c.Length + " bytes" : "none")}");
    }
    return;
}

if (args.Length > 1 && args[1] == "google")
{
    // Google Books without the network: a recorded answer read, matched and shown the way the app does it; then one
    // real request with a wrong key, which must stop the lookups cleanly rather than mark books as failed.
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    const string recorded = """
    {"items":[
      {"id":"x1","volumeInfo":{"title":"Becoming the Hacker","subtitle":"The Playbook for Getting Inside the Mind of the Attacker",
       "authors":["Adrian Pruteanu"],"publisher":"Packt Publishing Ltd","publishedDate":"2019-01-31",
       "description":"<p>Web penetration testing by becoming an ethical hacker.</p><p>Protect the web by learning the tools &amp; tricks.</p>",
       "categories":["Computers / Security / General"],"industryIdentifiers":[{"type":"ISBN_13","identifier":"9781788627962"},{"type":"ISBN_10","identifier":"1788627962"}]}},
      {"id":"x2","volumeInfo":{"title":"Becoming a Hacker Chef","authors":["Someone Else"],"publishedDate":"2001"}}
    ]}
    """;
    using var json = System.Text.Json.JsonDocument.Parse(recorded);
    var found = GoogleBooksClient.Volumes(json.RootElement, byIsbn: false).ToList();
    var c = found[0];
    Check(found.Count == 2 && c.Title == "Becoming the Hacker" && c.Authors.SequenceEqual(new[] { "Adrian Pruteanu" }) && c.Year == 2019
          && c.Publisher == "Packt Publishing Ltd" && c.Isbns.Contains("9781788627962"), "an answer reads: title, author, year, publisher, ISBNs");
    Check(c.Subjects.SequenceEqual(new[] { "Computers", "Security" }), "categories read as subjects, without \"General\" ({0})".Replace("{0}", string.Join(", ", c.Subjects)));
    Check(c.Description == "Web penetration testing by becoming an ethical hacker.\n\nProtect the web by learning the tools & tricks."
          || c.Description?.Replace("\n\n", "\n") == "Web penetration testing by becoming an ethical hacker.\nProtect the web by learning the tools & tricks.",
          "the description loses its HTML and keeps its paragraphs: " + c.Description?.Replace("\n", "|"));
    Check(c.EditionCount > found[1].EditionCount, "Google's first answer ranks first");

    var look = new LookupBook("k", "Becoming the Hacker", "Adrian Pruteanu", false, "[smtebooks.com] Becoming the Hacker 1st Edition", null);
    var d = OnlineMatcher.Decide(look, null, found);
    Check(d.Status == OnlineDetails.Found && d.Candidate?.Title == "Becoming the Hacker", "title and author agree: found, and the other book is not taken");
    var stranger = new LookupBook("k", "Becoming the Hacker", "Mary Smith", false, "x", null);
    Check(OnlineMatcher.Decide(stranger, null, found).Status != OnlineDetails.Found, "another author's book of the same name is not filled in");

    OnlineDetails Google(OnlineCandidate cand) => new()
    {
        Source = OnlineSource.GoogleBooks, Status = OnlineDetails.Found, How = OnlineDetails.ByMatch, SourceKey = cand.WorkKey,
        Title = cand.Title, Author = cand.AuthorText, Publisher = cand.Publisher, Year = cand.Year,
        Subjects = string.Join(", ", cand.Subjects), Description = cand.Description
    };
    Book Fresh(string publisher = "", int? year = null, string description = "")
    {
        var b = new Book { Title = "Becoming the Hacker", Author = "Adrian Pruteanu", Publisher = publisher, Year = year, Description = description };
        b.KeepFileDetails();
        return b;
    }

    var empty = Fresh();
    empty.SetOnline(Google(c));
    Check(empty.Description.StartsWith("Web penetration") && empty.DescriptionSource == OnlineSource.GoogleBooks, "an empty description is filled, and says it came from Google Books");
    Check(empty.Year == 2019 && empty.YearSource == OnlineSource.GoogleBooks && empty.Publisher == "Packt Publishing Ltd" && empty.Subjects == "Computers, Security",
        "an empty year, publisher and subjects are filled");
    Check(OnlineSource.Name(empty.YearSource) == "Google Books", "its name on screen is Google Books");

    var own = Fresh(publisher: "Packt", year: 2018, description: "The book's own words.");
    own.SetOnline(Google(c));
    Check(own.Publisher == "Packt" && own.Year == 2018 && own.Description == "The book's own words.", "the book's own publisher, year and description are never replaced");

    var withOl = Fresh();
    withOl.SetOnline(new OnlineDetails { Source = OnlineSource.OpenLibrary, Status = OnlineDetails.Found, How = OnlineDetails.ByMatch,
        Title = "Becoming the Hacker", Year = 2018, Description = "Open Library's words.", Publisher = "Packt" });
    withOl.SetOnline(Google(c));
    Check(withOl.Description == "Open Library's words." && withOl.Year == 2018 && withOl.Publisher == "Packt", "Open Library's details come before Google's");

    var suggestion = Fresh();
    var maybe = Google(c);
    maybe.Status = OnlineDetails.Suggested;
    suggestion.SetOnline(maybe);
    Check(suggestion.Description.Length == 0 && suggestion.Year is null, "a Google suggestion (not certain) fills nothing");

    var client = new GoogleBooksClient(() => "not-a-real-key");
    try
    {
        await client.ByIsbnAsync("9781788627962", CancellationToken.None);
        Check(false, "a wrong key is refused");
    }
    catch (GoogleQuotaException ex)
    {
        Check(true, "a wrong key (or a used-up day) stops the lookups: " + ex.Message);
    }
    catch (Exception ex)
    {
        Check(false, "a wrong key gave " + ex.GetType().Name + ": " + ex.Message);
    }
    Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
    return;
}

if (args.Length > 2 && args[1] == "enrich")
{
    // Every book in the database COPY read again with the current readers (files only READ, no covers): each author,
    // publisher and year that would change, against what the copy says now, with the copyright-page line each new
    // PDF year or publisher came from. args[2] = the TSV to write.
    FileNameParser.Context = NameContext.Build(repo.GetDeclaredAuthors(), repo.GetAllRelPaths());
    var books = repo.LoadAll().Where(b => b.FullPath is { } p && File.Exists(p)).ToList();
    var stems = books.GroupBy(b => Path.GetDirectoryName(b.FullPath!) ?? "", StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(b => Path.GetFileNameWithoutExtension(b.FullPath!)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            StringComparer.OrdinalIgnoreCase);
    var results = new System.Collections.Concurrent.ConcurrentBag<(Book Book, BookMetadata Md)>();
    var clock = Stopwatch.StartNew();
    await Parallel.ForEachAsync(books, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (b, _) =>
    {
        var path = b.FullPath!;
        var calibre = stems.TryGetValue(Path.GetDirectoryName(path) ?? "", out var n) && n == 1;
        results.Add((b, await MetadataService.ReadAsync(path, b.Format, calibre, readCover: false)));
    });
    var seconds = clock.Elapsed.TotalSeconds;

    static (string? YearLine, string? PublisherLine) Evidence(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = UglyToad.PdfPig.PdfDocument.Open(fs);
            var count = doc.NumberOfPages;
            var pages = Enumerable.Range(1, Math.Min(6, count)).Concat(Enumerable.Range(Math.Max(7, count - 1), Math.Max(0, Math.Min(2, count - 6))));
            string? y = null, p = null;
            foreach (var number in pages)
            {
                var page = doc.GetPage(number);
                if (!Imprint.LooksLikeOne(page.Text)) continue;
                var f = Imprint.Read(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
                y ??= f.YearLine is null ? null : $"p{number}: {f.YearLine}";
                p ??= f.PublisherLine is null ? null : $"p{number}: {f.PublisherLine}";
                if (y is not null && p is not null) break;
            }
            return (y, p);
        }
        catch (Exception ex)
        {
            return ("(" + ex.GetType().Name + ")", null);
        }
    }

    int authorsGone = 0, authorsChanged = 0, publishers = 0, publishersChanged = 0, years = 0, yearsChanged = 0;
    var lines = new List<string> { "kind\tformat\told\tnew\tevidence\ttitle\tfile" };
    foreach (var (b, md) in results.OrderBy(r => r.Book.RelPath, StringComparer.OrdinalIgnoreCase))
    {
        var file = Path.GetFileName(b.RelPath);
        var author = md.Author ?? "";
        if (author != b.FileAuthor)
        {
            if (author.Length == 0) authorsGone++; else authorsChanged++;
            lines.Add($"author\t{b.Format}\t{b.FileAuthor}\t{author}\t\t{md.Title}\t{file}");
        }
        (string? YearLine, string? PublisherLine)? ev = null;
        var publisher = md.Publisher ?? "";
        if (publisher != b.FilePublisher)
        {
            if (b.FilePublisher.Length == 0) publishers++; else publishersChanged++;
            ev ??= b.Format == BookFormat.Pdf ? Evidence(b.FullPath!) : (null, null);
            lines.Add($"publisher\t{b.Format}\t{b.FilePublisher}\t{publisher}\t{ev.Value.PublisherLine}\t{md.Title}\t{file}");
        }
        if (md.Year != b.FileYear)
        {
            if (b.FileYear is null) years++; else yearsChanged++;
            ev ??= b.Format == BookFormat.Pdf ? Evidence(b.FullPath!) : (null, null);
            lines.Add($"year\t{b.Format}\t{b.FileYear}\t{md.Year}\t{ev.Value.YearLine}\t{md.Title}\t{file}");
        }
    }
    File.WriteAllLines(args[2], lines.Select(l => l.Replace('\r', ' ').Replace('\n', ' ')));
    Console.WriteLine($"{results.Count} books read in {seconds:N0} s");
    Console.WriteLine($"authors: {authorsGone} cleared, {authorsChanged} changed");
    Console.WriteLine($"publishers: {publishers} new, {publishersChanged} changed");
    Console.WriteLine($"years: {years} new, {yearsChanged} changed");
    var reread = results.Select(r => new Book { RelPath = r.Book.RelPath, Author = r.Md.Author ?? "", Publisher = r.Md.Publisher ?? "" }).ToList();
    foreach (var offer in AuthorSuggester.Build(reread, new List<string>()))
        Console.WriteLine($"offer: {offer.Question} -> {offer.Answer}");
    return;
}

if (args.Length > 3 && args[1] == "pagetext")
{
    // One page of a PDF (args[2], page args[3]) as PdfPig's plain text and as its content-order extractor gives it.
    using var fs = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var doc = UglyToad.PdfPig.PdfDocument.Open(fs);
    var page = doc.GetPage(int.Parse(args[3]));
    Console.WriteLine("---- Text ----");
    Console.WriteLine(page.Text);
    Console.WriteLine("---- ContentOrderTextExtractor ----");
    Console.WriteLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
    return;
}

if (args.Length > 1 && args[1] == "retitle")
{
    // Every book in the database COPY read again with the current readers (files only READ, no covers), and each
    // title or series number that would change printed old -> new. The scanner's own name context is used.
    FileNameParser.Context = NameContext.Build(repo.GetDeclaredAuthors(), repo.GetAllRelPaths());
    var books = repo.LoadAll().Where(b => b.FullPath is { } p && File.Exists(p)).ToList();
    var stems = books.GroupBy(b => Path.GetDirectoryName(b.FullPath!) ?? "", StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(b => Path.GetFileNameWithoutExtension(b.FullPath!)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            StringComparer.OrdinalIgnoreCase);
    var changes = new System.Collections.Concurrent.ConcurrentBag<string>();
    var read = 0;
    var clock = Stopwatch.StartNew();
    await Parallel.ForEachAsync(books, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (b, _) =>
    {
        var path = b.FullPath!;
        var calibre = stems.TryGetValue(Path.GetDirectoryName(path) ?? "", out var n) && n == 1;
        var md = await MetadataService.ReadAsync(path, b.Format, calibre, readCover: false);
        Interlocked.Increment(ref read);
        var title = md.Title ?? "";
        var index = md.SeriesIndex;
        if (title != b.FileTitle || index != b.FileSeriesIndex)
            changes.Add($"{b.FileTitle}  [#{b.FileSeriesIndex}]  ->  {title}  [#{index}]   ({Path.GetFileName(path)})");
    });
    foreach (var c in changes.OrderBy(c => c, StringComparer.OrdinalIgnoreCase)) Console.WriteLine(c);
    Console.WriteLine($"{read} books read in {clock.Elapsed.TotalSeconds:N0} s, {changes.Count} would change");
    return;
}

if (args.Length > 2 && args[1] == "comics")
{
    // The comic reader's archive side on every real comic (only READ): open, count pages, and read and decode the
    // first, middle and last page at 1000 px wide, as the reader does.
    var paths = File.ReadAllLines(args[2]).Where(l => l.Length > 0).ToList();
    int ok = 0, failed = 0;
    var openMs = new List<long>();
    var pageMs = new List<long>();
    foreach (var path in paths)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var book = AryanEbookLibrary.Reader.Comic.ComicBook.Open(path);
            openMs.Add(clock.ElapsedMilliseconds);
            if (book.PageCount == 0) throw new Exception("no pages");
            var all = Environment.GetEnvironmentVariable("COMIC_ALL_PAGES") == "1";
            foreach (var page in all ? Enumerable.Range(0, book.PageCount) : new[] { 0, book.PageCount / 2, book.PageCount - 1 }.Distinct())
            {
                if (all) Console.WriteLine($"   page {page}");
                var t = Stopwatch.StartNew();
                var bytes = book.ReadPage(page) ?? throw new Exception($"page {page} read null");
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                stream.Seek(0);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var w = Math.Min(1000u, decoder.PixelWidth);
                var transform = new Windows.Graphics.Imaging.BitmapTransform
                {
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                    ScaledWidth = w,
                    ScaledHeight = (uint)Math.Max(1, Math.Round(w * (double)decoder.PixelHeight / decoder.PixelWidth)),
                };
                using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                pageMs.Add(t.ElapsedMilliseconds);
                if (page == 0 && ok < 3) Console.WriteLine($"   {Path.GetFileName(path)}: {book.PageCount} pages, first {decoder.PixelWidth}x{decoder.PixelHeight}");
            }
            ok++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"FAIL {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    openMs.Sort();
    pageMs.Sort();
    Console.WriteLine($"{ok} opened, {failed} failed; open median {openMs[openMs.Count / 2]} ms, max {openMs[^1]} ms; " +
                      $"page read+decode median {pageMs[pageMs.Count / 2]} ms, max {pageMs[^1]} ms over {pageMs.Count} pages");
    return;
}

if (args.Length > 1 && args[1] == "pdfreader")
{
    // The app's own PDF reader, without its window: the native core on fixtures and on real books (only READ),
    // Define's dictionaries, the page stack, and the reading record on the fresh database given as args[0].
    var fails = 0;
    void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    }
    const string Fixtures = @"E:\Aryan\native\reader_core\tests\fixtures\";

    // ---- fixtures ----
    var (outlined, st1) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(Fixtures + "sample_outline.pdf");
    Check(st1 == AryanEbookLibrary.Reader.Pdf.PdfOpenStatus.Ok && outlined is not null, "the outline fixture opens");
    var outline = outlined!.ReadOutline();
    Check(string.Join("|", outline.Select(o => $"{o.Depth}:{o.Page}:{o.Title}")) == "0:0:Chapter One|1:1:Section 1.1|0:2:Chapter Two|0:-1:Nowhere",
        "the outline reads back in order with depth and pages: " + string.Join("|", outline.Select(o => $"{o.Depth}:{o.Page}:{o.Title}")));
    outlined.Dispose();
    Check(outlined.RenderPage(0, 100) is null && outlined.ReadText(0, 100) is null, "a closed book answers nothing instead of failing");

    var (linked, _) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(Fixtures + "sample_links.pdf");
    var links = linked!.ReadLinks(0);
    Check(links.Count == 3 && links[0].Uri == "https://example.com/path?q=1" && links[2].TargetPage == 1, $"links: {links.Count}, first {links.FirstOrDefault()?.Uri}");
    Check(Math.Abs(links[0].Left - 190.5 / 612) < 1e-4 && Math.Abs(links[0].Top - (792 - 656.25) / 612) < 1e-4, "a link's box is in fractions of the page width");
    Check(linked.PageSizes[0] == (612, 792), $"page size in points: {linked.PageSizes[0]}");
    var page = linked.RenderPage(0, 1024);
    Check(page is { Width: 1024, Height: 1325 } p0 && p0.Bgra.Length == 1024 * 1325 * 4, $"a whole page 1024 wide: {page?.Width}x{page?.Height}");
    linked.Dispose();

    var (_, locked) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(Fixtures + "sample_encrypted.pdf");
    var (unlocked, st2) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(Fixtures + "sample_encrypted.pdf", "hunter2");
    Check(locked == AryanEbookLibrary.Reader.Pdf.PdfOpenStatus.NeedsPassword && st2 == AryanEbookLibrary.Reader.Pdf.PdfOpenStatus.Ok,
        "a protected book asks for its password and opens with it");
    unlocked?.Dispose();
    var (_, missing) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(Fixtures + "no-such-file.pdf");
    Check(missing == AryanEbookLibrary.Reader.Pdf.PdfOpenStatus.Failed, "a missing file fails plainly");

    // ---- sepia and night keep the page readable ----
    var px = new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 };
    AryanEbookLibrary.Reader.Pdf.PageColors.Apply(px, 2);
    Check(px[0] == AryanEbookLibrary.Reader.Pdf.NightMode.Floor && px[4] == AryanEbookLibrary.Reader.Pdf.NightMode.Ceiling && px[3] == 255, "night: white paper goes dark, black ink goes light, alpha stays");
    px = new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 };
    AryanEbookLibrary.Reader.Pdf.PageColors.Apply(px, 1);
    Check(px[2] == 0xF4 && px[1] == 0xEC && px[0] == 0xD8 && px[6] == 0x5B && px[4] == 0x36, "sepia: white is warm paper (BGR D8 EC F4), black is brown ink");

    // ---- the page stack ----
    var sizes = Enumerable.Range(0, 5000).Select(i => i % 7 == 0 ? (w: 1000.0, h: 1500.0) : (w: 800.0, h: 1000.0)).ToList();
    var stack = new AryanEbookLibrary.Reader.Pdf.PageStackGeometry(16);
    stack.Rebuild(sizes.Count, i => sizes[i]);
    var rng = new Random(7);
    var stackOk = true;
    for (int t = 0; t < 2000 && stackOk; t++)
    {
        var top = rng.NextDouble() * stack.Height;
        var bottom = top + rng.NextDouble() * 3000;
        var linear = Enumerable.Range(0, sizes.Count).Where(i => stack.TopOf(i) + stack.HeightOf(i) >= top && stack.TopOf(i) <= bottom).ToList();
        var (f, l) = stack.Range(top, bottom);
        stackOk = linear.Count == 0 ? f == -1 : (f == linear[0] && l == linear[^1]);
    }
    Check(stackOk, "the page stack's binary search agrees with a walk, 2000 random views of a 5000-page mixed book");

    // ---- Define ----
    WordDefinitionsFor(out var english, out var myanmar, out var hindi);
    var running = english.Lookup("running");
    Check(running is not null && running.Senses.Any(s => s.Headword == "run" && s.PartOfSpeech == "verb"), "\"running\" is looked up as the verb \"run\"");
    Check(english.Lookup("children")?.Senses[0].Headword == "child", "\"children\" is \"child\"");
    Check(english.Lookup("the") is null && english.Lookup("was") is null, "grammar words get no definition");
    Check(myanmar.For("book", "noun").Count > 0, "\"book\" has Myanmar meanings: " + string.Join(", ", myanmar.For("book", "noun")));
    Check(hindi.For("water", "noun").Count > 0, "\"water\" has Hindi meanings: " + string.Join(", ", hindi.For("water", "noun")));
    Check(AryanEbookLibrary.Reader.Define.EnglishWord.TryNormalize("\"Well-being.\"", out var w1) && w1 == "Well-being", "a quoted word with its full stop is one word");
    Check(!AryanEbookLibrary.Reader.Define.EnglishWord.TryNormalize("two words", out _), "two words are not one");

    // ---- real books, only read ----
    var pdfs = Directory.EnumerateFiles(@"E:\Books", "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
    var sample = pdfs.Where((_, i) => i % Math.Max(1, pdfs.Count / 30) == 0).Take(30).ToList();
    Console.WriteLine($"{pdfs.Count} PDFs in E:\\Books, reading {sample.Count}");
    int opened = 0, blank = 0, withText = 0, withOutline = 0;
    var openMs = new List<long>();
    var renderMs = new List<long>();
    var tileMs = new List<long>();
    foreach (var file in sample)
    {
        var clock = Stopwatch.StartNew();
        var (book, status) = AryanEbookLibrary.Reader.Pdf.PdfBook.Open(file);
        openMs.Add(clock.ElapsedMilliseconds);
        if (book is null)
        {
            Console.WriteLine($"     {status}: {Path.GetFileName(file)}");
            if (status != AryanEbookLibrary.Reader.Pdf.PdfOpenStatus.NeedsPassword) fails++;
            continue;
        }
        opened++;
        var (w, h) = book.PageSizes[0];
        clock.Restart();
        var whole = book.RenderPage(0, 2048);
        renderMs.Add(clock.ElapsedMilliseconds);
        var expectH = (int)Math.Round(2048 * h / w);
        if (whole is not { } r || r.Width != 2048 || Math.Abs(r.Height - expectH) > 1)
        {
            Console.WriteLine($"FAIL render {Path.GetFileName(file)}: {whole?.Width}x{whole?.Height}, expected 2048x{expectH}");
            fails++;
        }
        else
        {
            var ink = 0;
            for (int i = 0; i < r.Bgra.Length; i += 4 * 97) if (r.Bgra[i] < 200 || r.Bgra[i + 1] < 200 || r.Bgra[i + 2] < 200) ink++;
            if (ink == 0) blank++;
        }
        clock.Restart();
        var tile = book.RenderTile(Math.Min(1, book.PageCount - 1), 3, 2, 2);
        tileMs.Add(clock.ElapsedMilliseconds);
        if (tile is not { Width: 512, Height: 512 })
        {
            Console.WriteLine($"FAIL tile {Path.GetFileName(file)}");
            fails++;
        }
        if (book.ReadText(0, 1000) is { CharCount: > 0 }) withText++;
        if (book.ReadOutline().Count > 0) withOutline++;
        book.ReadLinks(0);
        book.Dispose();
    }
    Console.WriteLine($"opened {opened}/{sample.Count}; open median {Median(openMs)} ms, max {openMs.DefaultIfEmpty().Max()} ms; " +
                      $"page at 2048 median {Median(renderMs)} ms, max {renderMs.DefaultIfEmpty().Max()} ms; tile median {Median(tileMs)} ms; " +
                      $"blank first pages {blank}, with text {withText}, with an outline {withOutline}");
    Check(opened >= sample.Count - 2, "nearly every real PDF opens");

    // ---- the reading record ----
    var sessions = new ReadingSessions(db);
    var today = DateTime.Today;
    DateTime At(int daysAgo, int hour) => today.AddDays(-daysAgo).AddHours(hour).ToUniversalTime();
    var id = sessions.Add(new ReadingSession(0, "d|a.pdf", At(0, 9), At(0, 9), 600, 12, 0, 12, 300));
    sessions.Update(new ReadingSession(id, "d|a.pdf", At(0, 9), At(0, 10), 1800, 30, 0, 30, 300));
    sessions.Add(new ReadingSession(0, "d|a.pdf", At(1, 21), At(1, 22), 1200, 20, 30, 50, 300));
    sessions.Add(new ReadingSession(0, "d|b.pdf", At(2, 20), At(2, 21), 900, 5, 0, 5, 40));
    sessions.Add(new ReadingSession(0, "d|b.pdf", At(4, 20), At(4, 21), 300, 3, 5, 8, 40));
    var all = sessions.All();
    Check(all.Count == 4 && all.First(s => s.Id == id).Seconds == 1800, "a sitting is written once and kept up to date");
    var byDay = ReadingTime.ByDay(all);
    Check(byDay[today] == 1800 && byDay[today.AddDays(-1)] == 1200, "time is counted on the local day it was read");
    Check(ReadingTime.Streak(byDay, today) == 3, $"three days in a row, broken by the day with nothing: {ReadingTime.Streak(byDay, today)}");
    Check(ReadingTime.Streak(byDay, today.AddDays(1)) == 3, "a day not read YET does not break the streak");
    Check(ReadingTime.Between(byDay, today.AddDays(-2), today) == 3900, "a range of days adds up");
    var pace = ReadingTime.SecondsPerPage(sessions.ForBook("d|a.pdf"), all);
    Check(pace is { } pc && Math.Abs(pc - 3000.0 / 50) < 0.01, $"this book's own pace: {pace} s a page");
    Check(ReadingTime.SecondsPerPage(new List<ReadingSession>(), all) is { } pa && Math.Abs(pa - 4200.0 / 58) < 0.01, "a book with no record of its own uses the reader's pace");
    Check(ReadingTime.Format(3900) == "1 h 5 min" && ReadingTime.Format(45) == "under a minute" && ReadingTime.Format(7200) == "2 h", "time reads as people say it");

    sessions.SavePosition("d|a.pdf", new ReadingPosition(41, 300, "v1;0.25;W;1.3"));
    sessions.SavePosition("d|a.pdf", new ReadingPosition(42, 300, "v1;0.5;C;2"));
    var pos = sessions.GetPosition("d|a.pdf");
    Check(pos == new ReadingPosition(42, 300, "v1;0.5;C;2") && sessions.GetPosition("d|zzz") is null, "a place is saved over, and a new book has none");

    // A moved book keeps its reading history and its place.
    var folder = repo.AddFolder("d", "shelf");
    db.Exec("INSERT INTO books (folder_id, drive_id, rel_path, state_key, format, title) VALUES ($f, 'd', 'a.pdf', 'd|a.pdf', 2, 'A')", ("$f", folder.Id));
    var bookId = db.Scalar<long>("SELECT id FROM books WHERE rel_path='a.pdf'");
    repo.MoveBook(bookId, "d", "a.pdf", @"shelf\a.pdf", folder.Id);
    var movedKey = Book.MakeKey("d", @"shelf\a.pdf");
    Check(sessions.ForBook(movedKey).Count == 2 && sessions.ForBook("d|a.pdf").Count == 0, "sittings follow a moved book");
    Check(sessions.GetPosition(movedKey)?.Page == 42, "so does its place");

    Console.WriteLine($"-- {fails} failed");
    return;

    static long Median(List<long> xs) => xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt(xs.Count / 2);

    static void WordDefinitionsFor(out AryanEbookLibrary.Reader.Define.WordDefinitions en, out AryanEbookLibrary.Reader.Define.MyanmarGlosses my,
        out AryanEbookLibrary.Reader.Define.HindiGlosses hi)
    {
        TextReader Open(string name) => new StreamReader(new System.IO.Compression.GZipStream(
            File.OpenRead(Path.Combine(@"E:\Aryan\Assets\Dictionary", name)), System.IO.Compression.CompressionMode.Decompress));
        using (var r = Open("wordnet-en.tsv.gz")) en = AryanEbookLibrary.Reader.Define.WordDefinitions.Load(r);
        using (var r = Open("akk-en-my.tsv.gz")) my = AryanEbookLibrary.Reader.Define.MyanmarGlosses.Load(r);
        using (var r = Open("hindi-en-hi.tsv.gz")) hi = AryanEbookLibrary.Reader.Define.HindiGlosses.Load(r);
    }
}

if (args.Length > 1 && args[1] == "tagrename")
{
    // Renaming and removing tags, on words first, then on a copy of the real library's tags (in memory).
    var fails = 0;
    void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    }
    Check(Tags.Rename("Magazines, Hindi", "Magazines", "Assorted Magazines") == "Assorted Magazines, Hindi", "renamed in place");
    Check(Tags.Rename("Magazines, Assorted Magazines", "Magazines", "Assorted Magazines") == "Assorted Magazines", "renaming onto a tag the book has leaves it once");
    Check(Tags.Rename("magazines", "Magazines", "Mags") == "Mags", "the old name is matched whatever its capitals");
    Check(Tags.Rename("Hindi, Osho", "Magazines", "Mags") == "Hindi, Osho", "a book without the tag is left alone");
    Check(Tags.Rename("Hindi Comics, Hindi", "Hindi", "Indian") == "Hindi Comics, Indian", "a longer tag that starts the same is not touched");
    Check(Tags.Remove("Hindi Comics, Hindi", "Hindi") == "Hindi Comics", "remove takes the whole tag only");

    var books = repo.LoadAll();
    var before = Tags.Build(books);
    Console.WriteLine($"{before.Count} tags: " + string.Join(", ", before.Take(8).Select(t => $"{t.Name} ({t.Count})")));
    var small = before.Where(t => t.Count >= 2).OrderBy(t => t.Count).First();
    var big = before.First(t => t != small);
    var union = small.Books.Concat(big.Books).Distinct().Count();
    foreach (var b in small.Books) b.UserTags = Tags.Rename(b.UserTags, small.Name, big.Name);
    var after = Tags.Build(books);
    Check(after.All(t => t.Name != small.Name), $"\"{small.Name}\" is gone after renaming it to \"{big.Name}\"");
    Check(after.First(t => t.Name == big.Name).Count == union, $"\"{big.Name}\" now has both sets of books ({union})");
    Check(after.Count == before.Count - 1, "one tag fewer");
    Check(books.All(b => Tags.Split(b.UserTags).Count() == Tags.Split(b.UserTags).Distinct(StringComparer.CurrentCultureIgnoreCase).Count()),
        "no book wears a tag twice");

    Console.WriteLine($"-- {fails} failed");
    return;
}

if (args.Length > 1 && args[1] == "shelves")
{
    // Filters, their counted choices and saved shelves, on a copy of the real library. Every count a
    // Filters list shows must be exactly what choosing it gives, with the other filters on.
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var books = repo.LoadAll();
    foreach (var b in books) b.IsAvailable = DriveRegistry.IsOnline(b.DriveId);
    var fails = 0;
    void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    }

    var any = new Shelf();
    Check(ShelfFilter.Apply(books, any).Count() == books.Count, $"no filter shows all {books.Count} books");

    var languages = ShelfFilter.Languages(books, any);
    Console.WriteLine("languages: " + string.Join(", ", languages.Select(o => o.ToString())));
    Check(languages.Skip(1).Sum(o => o.Count) == books.Count, "language choices add up to the library");
    foreach (var o in languages.Skip(1))
        Check(ShelfFilter.Apply(books, new Shelf { Language = o.Key }).Count() == o.Count, $"language {o.Label}: {o.Count} shown");
    var myanmarByScript = books.Count(b => b.Title.Any(c => c >= (char)0x1000 && c <= (char)0x109F) || DuplicateFinder.LangKey(b.Language) == "my");
    Check(languages.FirstOrDefault(o => o.Key == "my")?.Count == myanmarByScript, $"Myanmar = titles in Myanmar script or stated my ({myanmarByScript})");

    var decades = ShelfFilter.Decades(books, any);
    Console.WriteLine("decades: " + string.Join(", ", decades.Select(o => o.ToString())));
    Check(decades.Skip(1).Sum(o => o.Count) == books.Count, "decade choices add up to the library");
    foreach (var o in decades.Skip(1))
        Check(ShelfFilter.Apply(books, new Shelf { Decade = o.Key }).Count() == o.Count, $"decade {o.Label}: {o.Count} shown");

    var publishers = ShelfFilter.Publishers(books, any);
    Console.WriteLine($"publishers: {publishers.Count - 1}, top: " + string.Join(", ", publishers.Skip(1).Take(5).Select(o => o.ToString())));
    foreach (var o in publishers.Skip(1).Take(8))
        Check(ShelfFilter.Apply(books, new Shelf { Publisher = o.Key }).Count() == o.Count, $"publisher {o.Label}: {o.Count} shown");
    Check(publishers.Skip(1).Select(o => o.Key.ToLowerInvariant()).Distinct().Count() == publishers.Count - 1,
        "no publisher listed twice in different capitals");

    var connected = books.Count(b => b.IsAvailable);
    Check(ShelfFilter.Apply(books, new Shelf { ConnectedOnly = true }).Count() == connected, $"connected drives only: {connected}");

    // Choices are counted with the other filters on: inside Myanmar, the decades add up to Myanmar.
    var burmese = new Shelf { Language = "my" };
    var inside = ShelfFilter.Decades(books, burmese);
    Check(inside.Skip(1).Sum(o => o.Count) == myanmarByScript && inside[0].Count == myanmarByScript,
        $"decades inside Myanmar add up to {myanmarByScript}");
    var pdfBurmese = new Shelf { Language = "my", Format = 2 };
    var manual = books.Count(b => b.Format == BookFormat.Pdf && ShelfFilter.LanguageKey(b) == "my");
    Check(ShelfFilter.Apply(books, pdfBurmese).Count() == manual, $"Myanmar + PDF: {manual}");
    Check(ShelfFilter.Languages(books, pdfBurmese).First(o => o.Key == "my").Count == manual, "the chosen language shows the count of the view");

    // A shelf goes to settings.json and back and shows the same books.
    var shelf = new Shelf
    {
        Name = "Burmese PDFs", List = LibraryFilter.Unread, Format = 2, Language = "my", Decade = "unknown",
        MinRating = 0, ConnectedOnly = true, Search = "the"
    };
    var json = System.Text.Json.JsonSerializer.Serialize(new List<Shelf> { shelf });
    var back = System.Text.Json.JsonSerializer.Deserialize<List<Shelf>>(json)![0];
    Check(back.Name == shelf.Name && back.SameRules(shelf), "a shelf survives settings.json");
    var before = ShelfFilter.Apply(books, shelf).Select(b => b.Id).ToList();
    var after = ShelfFilter.Apply(books, back).Select(b => b.Id).ToList();
    Check(before.SequenceEqual(after), $"the saved shelf shows the same {before.Count} books");
    Console.WriteLine("suggested names: " + ShelfFilter.SuggestName(shelf) + "  |  " +
                      ShelfFilter.SuggestName(new Shelf { Tag = "Osho" }) + "  |  " + ShelfFilter.SuggestName(new Shelf()));
    Check(!ShelfFilter.IsFiltered(new Shelf { List = LibraryFilter.Unread }) && ShelfFilter.IsFiltered(burmese),
        "the plain list is not filtered, a language is");

    Console.WriteLine($"-- {fails} failed");
    return;
}

if (args.Length > 1 && args[1] == "reading")
{
    // The reading log on made-up books, so every number is known in advance.
    var fails = 0;
    void Check(bool ok, string what)
    {
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    }
    long id = 1;
    Book Make(string title, string series = "", double? index = null) =>
        new() { Id = id++, Title = title, Series = series, SeriesIndex = index, RelPath = title + ".epub", DriveId = "D" };
    DateTime Local(int y, int m, int d, int h = 12) => new DateTime(y, m, d, h, 0, 0, DateTimeKind.Local).ToUniversalTime();

    var jan1 = Make("Jan one"); ReadingLog.SetStatus(jan1, ReadStatus.Finished, Local(2026, 1, 15));
    var jan2 = Make("Jan two"); ReadingLog.SetStatus(jan2, ReadStatus.Finished, Local(2026, 1, 31, 23));
    var mar = Make("March"); ReadingLog.SetStatus(mar, ReadStatus.Finished, Local(2026, 3, 5));
    var old = Make("Last year"); ReadingLog.SetStatus(old, ReadStatus.Finished, Local(2025, 12, 31, 23));
    var undated = Make("Some time ago"); ReadingLog.SetStatus(undated, ReadStatus.Finished, null);
    var books = new List<Book> { jan1, jan2, mar, old, undated };

    var y2026 = ReadingLog.Year(books, 2026);
    Check(y2026.Total == 3 && y2026.Months[0] == 2 && y2026.Months[2] == 1 && y2026.Months.Sum() == 3,
        $"2026: 2 in January, 1 in March (got {string.Join(",", y2026.Months)})");
    Check(y2026.Finished[0] == mar && y2026.Finished[^1] == jan1, "the log lists the newest first");
    Check(ReadingLog.Year(books, 2025).Total == 1, "the one finished late on 31 December stays in 2025");
    Check(ReadingLog.Undated(books) == 1 && ReadingLog.AllTime(books) == 5, "the undated one counts for all time only");
    Check(ReadingLog.Years(books, 2026).SequenceEqual(new[] { 2026, 2025 }), "years: 2026, 2025");
    Check(ReadingLog.Years(new List<Book>(), 2026).SequenceEqual(new[] { 2026 }), "an empty log still offers this year");

    // Status changes: finishing again keeps the date, Unread forgets it, Reading keeps it.
    var first = jan1.FinishedUtc;
    ReadingLog.SetStatus(jan1, ReadStatus.Finished, Local(2026, 9, 1));
    Check(jan1.FinishedUtc == first, "marking a finished book finished again keeps its date");
    ReadingLog.SetStatus(jan1, ReadStatus.Reading, null);
    Check(jan1.FinishedUtc == first && ReadingLog.FinishedLocal(jan1) is null, "a book read again is not in the log while it is being read");
    ReadingLog.SetStatus(jan1, ReadStatus.Finished, Local(2026, 9, 1));
    Check(ReadingLog.FinishedLocal(jan1)?.Month == 9, "finishing it again moves it to the new date");
    ReadingLog.SetStatus(jan1, ReadStatus.Unread, null);
    Check(jan1.FinishedUtc is null && jan1.Progress == 0, "unread forgets the date and the progress");

    // Up next: what is being read, then the next unread volume of each series in progress.
    var s1 = Make("S one", "Saga", 1); ReadingLog.SetStatus(s1, ReadStatus.Finished, Local(2026, 2, 1));
    var s2 = Make("S two", "Saga", 2); ReadingLog.SetStatus(s2, ReadStatus.Finished, Local(2026, 2, 10));
    var s3 = Make("S three", "Saga", 3);
    var s4 = Make("S four", "Saga", 4);
    var t1 = Make("T one", "Trilogy", 1); ReadingLog.SetStatus(t1, ReadStatus.Reading, null);
    var t2 = Make("T two", "Trilogy", 2);
    var u1 = Make("U one", "Done", 1); ReadingLog.SetStatus(u1, ReadStatus.Finished, Local(2026, 4, 1));
    var v2 = Make("V two", "Skipped", 2); ReadingLog.SetStatus(v2, ReadStatus.Finished, Local(2026, 5, 1));
    var v1 = Make("V one", "Skipped", 1);
    var v3 = Make("V three", "Skipped", 3);
    var next = ReadingLog.UpNext(new List<Book> { s1, s2, s3, s4, t1, t2, u1, v1, v2, v3 });
    Console.WriteLine("up next: " + string.Join(" | ", next.Select(i => $"{i.Book.Title} ({i.Why})")));
    Check(next.Count == 3 && next[0].Book == t1, "the book being read comes first");
    Check(next.Any(i => i.Book == s3) && !next.Any(i => i.Book == s4), "Saga: #3 is next, not #4");
    Check(!next.Any(i => i.Book == t2), "Trilogy: nothing more while #1 is being read");
    Check(next.Any(i => i.Book == v3) && !next.Any(i => i.Book == v1), "Skipped: after #2 comes #3, not the #1 left behind");
    Check(next.IndexOf(next.First(i => i.Book == v3)) < next.IndexOf(next.First(i => i.Book == s3)),
        "the series finished from most recently comes first");

    Console.WriteLine($"-- {fails} failed");
    return;
}


if (args.Length > 1 && args[1] == "online")
{
    await Online(int.Parse(args[2]));
    return;
}

if (args.Length > 1 && args[1] == "wiki")
{
    await Wiki(int.Parse(args[2]));
    return;
}

if (args.Length > 1 && args[1] == "zawgyi")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var report = new System.Text.StringBuilder();

    // Ground truth from the library itself: the same book's Zawgyi title and Unicode file name.
    var pairs = new (string Zawgyi, string Unicode)[]
    {
        ("ဗန္းေမာ္တင္ေအာင္",
         "ဗန်းမော်တင်အောင်"),
        ("အညၾတ", "အညတြ"),
        ("မင္းခိုက္စိုးစန္",
         "မင်းခိုက်စိုးစန်"),
    };
    var bad = 0;
    foreach (var (zg, uni) in pairs)
    {
        var got = Zawgyi.Fix(zg);
        var ok = got == uni;
        if (!ok) bad++;
        report.AppendLine($"{(ok ? "ok  " : "FAIL")} {zg}\n     got {got}\n     want {uni}");
    }
    // A control: correct Unicode must come back untouched.
    foreach (var uni in new[] { "မင်းခိုက်", "ကောင်း", "Plain English" })
    {
        var same = Zawgyi.Fix(uni) == uni && !Zawgyi.Looks(uni);
        if (!same) bad++;
        report.AppendLine($"{(same ? "ok  " : "FAIL")} control {uni} -> {Zawgyi.Fix(uni)} (looks zawgyi: {Zawgyi.Looks(uni)})");
    }
    report.AppendLine($"-- {pairs.Length + 3} checks, {bad} failed");

    var books = repo.LoadAll();
    var hits = 0;
    foreach (var b in books)
    {
        foreach (var (what, s) in new[] { ("title", b.Title), ("author", b.Author), ("file", b.RelPath) })
        {
            if (!Zawgyi.Looks(s)) continue;
            hits++;
            report.AppendLine($"{what}\n   was {s}\n   now {Zawgyi.Fix(s)}");
        }
    }
    report.AppendLine($"-- {hits} Zawgyi fields in {books.Count} books");
    File.WriteAllText("zawgyi_fix.txt", report.ToString(), new System.Text.UTF8Encoding(true));
    Console.WriteLine($"{pairs.Length + 3} checks, {bad} failed; {hits} Zawgyi fields. See zawgyi_fix.txt");
    return;
}

if (args.Length > 1 && args[1] == "tags")
{
    var books = repo.LoadAll();
    var found = TagSuggester.Build(books);
    var report = new System.Text.StringBuilder();
    report.AppendLine($"{found.Count} tags suggested for {books.Count} books");
    foreach (var s in found)
    {
        report.AppendLine($"{s.Count,5}  {s.Tag}   ({s.Why})");
        report.AppendLine($"         {s.Sample}");
    }
    File.WriteAllText("tag_suggestions.txt", report.ToString(), new System.Text.UTF8Encoding(true));
    Console.WriteLine($"{found.Count} tags suggested, covering {found.Sum(x => x.Count)} book slots. See tag_suggestions.txt");

    // Taking a suggestion must land that tag on exactly those books, and leave the others alone.
    var top = found[0];
    var wanted = top.Count;
    foreach (var b in top.Books) b.UserTags = Tags.Merge(b.UserTags, top.Tag);
    var after = Tags.Build(books).FirstOrDefault(t => t.Name == top.Tag);
    var again = TagSuggester.Build(books).Any(s => s.Tag == top.Tag);
    Console.WriteLine($"applied \"{top.Tag}\": {after?.Count ?? 0} books wear it (wanted {wanted}); " +
                      $"offered again: {again} (wanted False)");

    // A second take adds nothing, and taking it off puts the books back where they were.
    foreach (var b in top.Books) b.UserTags = Tags.Merge(b.UserTags, top.Tag);
    var twice = Tags.Build(books).FirstOrDefault(t => t.Name == top.Tag)?.Count ?? 0;
    foreach (var b in top.Books) b.UserTags = Tags.Remove(b.UserTags, top.Tag);
    var gone = Tags.Build(books).Any(t => t.Name == top.Tag);
    Console.WriteLine($"twice: {twice} (wanted {wanted}); removed: {(gone ? "still there" : "clean")}");
    return;
}

if (args.Length > 1 && args[1] == "export")
{
    var books = repo.LoadAll();
    var file = Path.Combine(Directory.GetCurrentDirectory(), "catalogue.csv");
    var n = CatalogExport.Csv(file, books);
    var lines = File.ReadAllLines(file);
    Console.WriteLine($"{n} books written, {lines.Length} lines, {new FileInfo(file).Length / 1024} KB -> {file}");
    Console.WriteLine("header: " + lines[0]);
    return;
}

if (args.Length > 1 && args[1] == "moved")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    // A book whose file "moved": the row is marked missing at an old path, and the real file is still in
    // the catalogue at its own path. The finder must propose it, and relinking must carry the state over.
    var all = repo.LoadAll();
    var sample = all.First(b => b.FileSize > 0);
    var realPath = sample.RelPath;
    var oldPath = System.IO.Path.Combine("Moved", System.IO.Path.GetFileName(realPath));

    // the user's own details on the old row
    var state = new AryanEbookLibrary.Models.BookState
    {
        IsFavorite = true, Rating = 5, Notes = "carried over", UserTags = "test",
        CustomTitle = "My own title", UpdatedUtc = DateTime.UtcNow
    };
    var oldKey = AryanEbookLibrary.Models.Book.MakeKey(sample.DriveId, oldPath);
    db.Exec("INSERT INTO books (folder_id, drive_id, rel_path, state_key, format, title, author, file_size, is_missing, added_utc, meta_version, name_fields, cover_weak) " +
            "VALUES ($f, $d, $r, $k, $fmt, $t, '', $size, 1, $added, 4, 0, 0)",
        ("$f", sample.FolderId), ("$d", sample.DriveId), ("$r", oldPath), ("$k", oldKey),
        ("$fmt", (int)sample.Format), ("$t", sample.Title), ("$size", sample.FileSize),
        ("$added", DateTime.UtcNow.ToString("o")));
    repo.UpsertState(sample.DriveId, oldPath, oldKey, state);

    var missing = MissingBooks.All(repo);
    Console.WriteLine($"missing rows: {missing.Count}");
    var moved = MissingBooks.FindMoved(repo, missing);
    Console.WriteLine($"moved found: {moved.Count}");
    foreach (var m in moved) Console.WriteLine($"   [{m.Why}] {m.Book.RelPath}  ->  {m.NewRelPath}");

    var mine = moved.FirstOrDefault(m => m.Book.RelPath == oldPath);
    if (mine is null) { Console.WriteLine("FAIL: the moved book was not proposed"); return; }
    if (mine.NewRelPath != realPath) { Console.WriteLine($"FAIL: proposed {mine.NewRelPath}, wanted {realPath}"); return; }

    MissingBooks.Relink(repo, mine);
    var after = repo.LoadAll().FirstOrDefault(b => b.RelPath == realPath);
    var rows = db.Scalar<long>("SELECT COUNT(*) FROM books WHERE drive_id=$d AND rel_path=$r", ("$d", sample.DriveId), ("$r", realPath));
    var stillMissing = MissingBooks.All(repo).Count;
    Console.WriteLine($"after relink: rows at that path {rows}, missing rows {stillMissing}");
    Console.WriteLine($"   title now '{after?.Title}' (wanted 'My own title'), favorite {after?.IsFavorite}, rating {after?.Rating}, tags '{after?.UserTags}'");
    var ok = rows == 1 && stillMissing == 0 && after is { IsFavorite: true, Rating: 5 } && after.Title == "My own title";
    Console.WriteLine(ok ? "OK: the file moved, the book and everything on it came along" : "FAIL");
    return;
}

if (args.Length > 1 && args[1] == "health")
{
    var list = repo.LoadAll();
    var authors = AuthorIndex.Build(list, repo.GetAllAuthorsOnline());
    Console.WriteLine($"{authors.Count} authors on {list.Count} books; {authors.Count(a2 => a2.HasQid)} known to Wikidata");
    var similar = AuthorIndex.Similar(authors);
    Console.WriteLine($"-- {similar.Count} groups of names that look like one person");
    foreach (var g in similar.Take(40)) Console.WriteLine($"   {g.Canonical}  <=  {g.Others}   ({g.BookCount} books)");

    // "health <language> <format>" also tries the keep rule, e.g. "health en EPUB".
    var rule = new KeepRule
    {
        Language = args.Length > 2 && args[2] != "-" ? args[2] : "",
        Format = args.Length > 3 && args[3] != "-" ? args[3] : ""
    };
    Console.WriteLine($"-- keep rule: language '{rule.Language}', format '{rule.Format}'");

    var dupes = DuplicateFinder.Find(list, rule);
    Console.WriteLine($"-- {dupes.Count(d => !d.SameBookOtherFormat)} duplicate groups, {dupes.Count(d => d.SameBookOtherFormat)} same book in another format or language");
    foreach (var r in dupes.GroupBy(d => d.Reason)) Console.WriteLine($"   {r.Count(),4}  {r.Key}");
    Console.WriteLine($"-- the rule picks something other than the biggest file in {dupes.Count(d => !d.KeepLine.StartsWith("Keeping the biggest"))} groups");
    foreach (var d in dupes.Where(d => d.SameBookOtherFormat || !d.KeepLine.StartsWith("Keeping the biggest")).Take(30))
    {
        Console.WriteLine($"   [{d.Reason}] {d.Title}  -- {d.KeepLine}");
        foreach (var c in d.Copies)
            Console.WriteLine($"      {(c.IsKeep ? "KEEP " : "     ")}{c.Book.RelPath}  {c.Book.FormatLabel} {c.Book.Language} {c.Book.FileSize / 1024}K");
    }
    foreach (var d in dupes.Take(30))
    {
        Console.WriteLine($"   [{d.Reason}] {d.Title}  -- {d.KeepLine}");
        foreach (var c in d.Copies)
            Console.WriteLine($"      {(c.IsKeep ? "KEEP " : "     ")}{c.Book.RelPath}  {c.Book.FormatLabel} {c.Book.Language} {c.Book.FileSize / 1024}K");
    }
    Console.WriteLine($"-- needs details: {list.Count(b => b.NeedsDetails)} of {list.Count}");

    var series = SeriesIndex.Build(list);
    Console.WriteLine($"-- {series.Count} series, {series.Sum(x => x.Count)} books, {series.Count(x => x.HasGaps)} with a gap");
    foreach (var x in series.Take(20))
        Console.WriteLine($"   {x.Name} ({x.Authors}) {x.CountText} {x.NumbersText} {x.GapsText}");

    var tags = Tags.Build(list);
    Console.WriteLine($"-- {tags.Count} tags on {list.Count(b => b.UserTags.Length > 0)} books");
    foreach (var t in tags.Take(20)) Console.WriteLine($"   {t.Name}  {t.CountText}");

    var missingNow = MissingBooks.All(repo);
    Console.WriteLine($"-- {missingNow.Count} missing books");
    foreach (var m in missingNow.Take(10)) Console.WriteLine($"   {m.Title}  ({m.Where})");
    return;
}

var scanner = new ScannerService(repo);
var sw = Stopwatch.StartNew();
var last = TimeSpan.Zero;
var progress = new Progress<ScanProgress>(p =>
{
    if (sw.Elapsed - last < TimeSpan.FromSeconds(30) && !p.Done) return;
    last = sw.Elapsed;
    Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:N0}s  checked {p.Checked}/{p.Found}  updated {p.Updated}  skipped {p.Skipped}");
});
var result = await scanner.ScanAsync(repo.GetFolders(), progress, CancellationToken.None);
Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:N0}s: {result.Summary}; peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");

async Task Online(int count)
{
    // Same selection and order as OnlineLookupService.Start / NeedsLookup.
    LookupBook ToLookup(Book b)
    {
        var isbn = string.IsNullOrEmpty(b.Isbn) ? null : Isbn.Digits(b.Isbn);
        return new LookupBook(b.StateKey, b.Title, b.Author, b.CustomAuthor is null && (b.NameFields & BookMetadata.NameField.Author) != 0,
            Path.GetFileNameWithoutExtension(b.RelPath), Isbn.IsValid(isbn) ? isbn : null, b.Series);
    }
    bool Needs(Book b)
    {
        if (b.Online is not null) return false;
        var fromName = (b.NameFields & (BookMetadata.NameField.Title | BookMetadata.NameField.Author)) != 0;
        if (!(b.FileAuthor.Length == 0 || fromName || b.NeedsCover || b.FileDescription.Length == 0)) return false;
        var l = ToLookup(b);
        return l.Isbn is not null || OnlineMatcher.CanSearch(l);
    }
    var books = repo.LoadAll();
    var eligible = books.Where(Needs).ToList();
    Console.WriteLine($"{books.Count} books, {eligible.Count} need a lookup");
    var queue = eligible.OrderBy(b => b.Isbn.Length > 0 ? 0 : b.FileAuthor.Length == 0 ? 1 : 2)
        .Where((b, i) => i % Math.Max(1, eligible.Count / count) == 0).Take(count).ToList();

    var client = new OpenLibraryClient();
    var sw2 = Stopwatch.StartNew();
    int errors = 0;
    await Parallel.ForEachAsync(queue, new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (b, t) =>
    {
        var l = ToLookup(b);
        try
        {
            var d = await OnlineMatcher.LookupAsync(client, l, t);
            var o = d.Candidate is { } c
                ? new OnlineDetails { Status = d.Status, How = d.How, SourceKey = c.WorkKey, Title = c.Title,
                      Author = c.Authors.Count > 0 ? c.AuthorText : null, Publisher = c.Publisher, Year = c.Year,
                      Subjects = c.Subjects.Count > 0 ? string.Join(", ", c.Subjects) : null, CoverId = c.CoverId }
                : new OnlineDetails { Status = d.Status };
            if (o.IsApplied)
            {
                try { o.Description = await client.DescriptionAsync(o.SourceKey!, t); } catch (OnlineUnavailableException) { }
                if (b.NeedsCover && o.CoverId is { } id)
                    try { if (await client.CoverAsync(id, t) is { } bytes) o.CoverFile = await CoverStore.SaveOnlineAsync(bytes, l.Key); }
                    catch (OnlineUnavailableException) { }
            }
            repo.UpsertOnline(l.Key, o);
        }
        catch (OnlineUnavailableException)
        {
            Interlocked.Increment(ref errors);
            repo.UpsertOnline(l.Key, new OnlineDetails { Status = OnlineDetails.Error, Tries = 1 });
        }
    });
    Console.WriteLine($"{queue.Count} looked up in {sw2.Elapsed.TotalSeconds:N0}s, {errors} errors");

    var keys = queue.Select(b => b.StateKey).ToHashSet();
    var after = repo.LoadAll().Where(b => keys.Contains(b.StateKey)).OrderBy(b => b.Online?.Status).ToList();
    foreach (var b in after)
    {
        var o = b.Online!;
        var cover = b.CoverFile is null ? "no cover" : b.CoverFile.StartsWith("ol-") ? "OL cover" : b.CoverWeak ? "text-page cover" : "own cover";
        Console.WriteLine($"[{o.Status} {o.How}] {Path.GetFileName(b.RelPath)}");
        Console.WriteLine($"    file:  {b.FileTitle} | {b.FileAuthor} | {b.FileYear} | {b.FilePublisher}");
        if (o.Status is OnlineDetails.Found or OnlineDetails.Suggested)
            Console.WriteLine($"    OL:    {o.Title} | {o.Author} | {o.Year} | {o.Publisher} | desc {o.Description?.Length ?? 0} | subj {o.Subjects}");
        Console.WriteLine($"    shows: {b.Title} | {b.Author} | {b.Year} | {b.Publisher} | {cover} | desc {b.Description.Length}");
    }
    var all = repo.GetAllOnline().Values.SelectMany(x => x).Where(o => o.Source == OnlineSource.OpenLibrary);
    Console.WriteLine(string.Join(", ", all.GroupBy(o => o.Status + (o.How is null ? "" : "/" + o.How)).Select(g => $"{g.Key} {g.Count()}")));
}


// Wikidata per author, then Wikipedia for the works it ties to a page. Writes the rows the app writes,
// then reloads the library and prints what each book shows and where each detail came from.
async Task Wiki(int authorCount)
{
    LookupBook ToLookup2(Book b)
    {
        var isbn = string.IsNullOrEmpty(b.Isbn) ? null : Isbn.Digits(b.Isbn);
        return new LookupBook(b.StateKey, b.Title, b.Author, b.CustomAuthor is null && (b.NameFields & BookMetadata.NameField.Author) != 0,
            Path.GetFileNameWithoutExtension(b.RelPath), Isbn.IsValid(isbn) ? isbn : null, b.Series);
    }
    bool Myanmar(string t) => t.Any(c => c is >= 'က' and <= '႟');
    var books = repo.LoadAll();
    var byAuthor = books
        .Where(b => b.Author.Length > 0 && !Myanmar(b.Author) && !Myanmar(b.Title) && !b.Author.Contains('.') && b.Author.Trim().Contains(' '))
        .GroupBy(b => b.Author.Split(',')[0].Trim(), StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Count())
        .Take(authorCount)
        .ToList();
    Console.WriteLine($"{byAuthor.Count} authors, {byAuthor.Sum(g => g.Count())} books");

    var wd = new WikidataClient();
    var wp = new WikipediaClient();
    int found = 0, matched = 0, described = 0, withCover = 0, withSeries = 0, errors = 0;
    var touched = new HashSet<string>();
    var sw3 = Stopwatch.StartNew();
    foreach (var group in byAuthor)
    {
        string? qid;
        try { qid = await wd.FindAuthorAsync(group.Key, CancellationToken.None); }
        catch (OnlineUnavailableException ex) { Console.WriteLine($"[error] {group.Key}: {ex.InnerException?.Message}"); errors++; continue; }
        repo.UpsertAuthorOnline(new LibraryRepository.AuthorOnline(group.Key.ToLowerInvariant(), group.Key, qid,
            qid is null ? OnlineDetails.None : OnlineDetails.Found, null, 0, DateTime.UtcNow));
        if (qid is null) { Console.WriteLine($"[no author] {group.Key} ({group.Count()} books)"); continue; }
        found++;

        List<WikidataWork> works;
        try { works = await wd.WorksByAuthorAsync(qid, CancellationToken.None); }
        catch (OnlineUnavailableException ex) { Console.WriteLine($"[error] {group.Key} works: {ex.InnerException?.Message}"); errors++; continue; }
        Console.WriteLine($"[{group.Key}] {qid}: {works.Count} works, {group.Count()} books here");

        foreach (var b in group)
        {
            var l = ToLookup2(b);
            var work = works.Where(w => OnlineMatcher.WorkTitleAgrees(l, w.Title)).MaxBy(w => OnlineMatcher.TitleOverlap(l, w.Title));
            var facts = work is null
                ? new OnlineDetails { Source = OnlineSource.Wikidata, Status = OnlineDetails.None }
                : new OnlineDetails { Source = OnlineSource.Wikidata, Status = OnlineDetails.Found, How = OnlineDetails.ByMatch,
                      SourceKey = work.Key, Title = work.Title, Year = work.Year, Series = work.Series,
                      SeriesIndex = work.SeriesIndex, PageTitle = work.WikipediaTitle };
            repo.UpsertOnline(b.StateKey, facts);
            touched.Add(b.StateKey);
            if (work is null) continue;
            matched++;
            if (work.Series is not null) withSeries++;
            if (work.WikipediaTitle is null) continue;

            WikipediaArticle? art = null;
            try { art = await wp.ArticleAsync(work.WikipediaTitle, CancellationToken.None); }
            catch (OnlineUnavailableException) { errors++; }
            if (art is null) continue;
            described++;
            var article = new OnlineDetails { Source = OnlineSource.Wikipedia, Status = OnlineDetails.Found, How = OnlineDetails.ByMatch,
                SourceKey = art.Title, PageTitle = art.Title, Description = art.Summary, CoverUrl = art.ImageUrl };
            if (b.NeedsCover && art.ImageUrl is { } url)
            {
                try
                {
                    if (await wp.ImageAsync(url, CancellationToken.None) is { } bytes)
                    {
                        article.CoverFile = await CoverStore.SaveOnlineAsync(bytes, b.StateKey + "|wikipedia");
                        withCover++;
                    }
                }
                catch (OnlineUnavailableException) { errors++; }
            }
            repo.UpsertOnline(b.StateKey, article);
        }
    }
    Console.WriteLine($"authors found {found}/{byAuthor.Count}, books matched {matched}, series {withSeries}, described {described}, covers {withCover}, errors {errors}, {sw3.Elapsed.TotalSeconds:N0}s");

    Console.WriteLine("---- what each touched book shows now");
    foreach (var b in repo.LoadAll().Where(x => touched.Contains(x.StateKey)).OrderBy(x => x.Title))
    {
        if (b.OnlineFrom(OnlineSource.Wikidata) is not { IsApplied: true }) continue;
        var where = new List<string>();
        if (b.SeriesSource is { } s2) where.Add("series " + s2);
        if (b.YearSource is { } y2) where.Add("year " + y2);
        if (b.DescriptionSource is { } d2) where.Add("description " + d2);
        if (b.CoverSource is { } c2) where.Add("cover " + c2);
        Console.WriteLine($"  {b.Title} | {b.Author} | {b.Year} | {b.SeriesLine} | desc {b.Description.Length} | {string.Join(", ", where)}");
    }
}
