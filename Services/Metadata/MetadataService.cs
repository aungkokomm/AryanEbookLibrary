using System.Text.RegularExpressions;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Picks the right reader for a file. Reader-only, like CineLibrary: nothing is written to the user's book
/// folders. Order: embedded metadata first, then Calibre's metadata.opf / cover.jpg override it when present,
/// then the file name fills what is still missing (and says which details it filled).
/// </summary>
public static class MetadataService
{
    /// <summary>
    /// Bumped whenever the readers get better. Books read by an older version are read again by the next scan
    /// (without re-rendering covers), so an improvement reaches the whole library by itself.
    /// 2: file-name parser with library context, author lists tidied, ISBN from PDF text, text-page covers.
    /// 3: scanner stamps ("ACDSee", "GonVisor", "Full page photo") and bare domains are not titles.
    /// 4: Burmese written in Zawgyi, or with ေ and ြ out of order, is shown in Unicode.
    /// 5: any bracketed web address ("[smtebooks.com]") goes, a catalogue number in front of a title becomes its
    ///    number in the series, and a file name left as the title reads with spaces ("Aath_Pahar_Youn_Jhumte").
    /// 6: no web address, e-mail or program ("ComicRack", "CamScanner") as an author (a site becomes the publisher),
    ///    and a PDF's copyright page gives its year and publisher.
    /// </summary>
    public const int Version = 6;

    public static async Task<BookMetadata> ReadAsync(string path, BookFormat format, bool useCalibre, bool readCover = true)
    {
        var md = new BookMetadata();

        try
        {
            md = format switch
            {
                BookFormat.Epub => await Task.Run(() => EpubReader.Read(path)),
                BookFormat.Pdf => await PdfReader.ReadAsync(path, readCover),
                BookFormat.Mobi or BookFormat.Azw3 => await Task.Run(() => MobiReader.Read(path)),
                BookFormat.Cbz or BookFormat.Cbr => await Task.Run(() => ComicReader.Read(path)),
                BookFormat.Kfx => await ReadKfxAsync(path),
                _ => new BookMetadata()
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Metadata read failed ({Path.GetFileName(path)}): {ex.Message}");
        }

        if (useCalibre)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                var calibre = dir is null ? null : CalibreReader.Read(dir);
                if (calibre is not null) md.MergeFrom(calibre, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write($"Calibre metadata failed ({path}): {ex.Message}");
            }
        }

        // Every reader's title without a download site's tag, and no title at all when it is only a web address.
        if (md.Title is { } t)
        {
            t = FileNameParser.StripSiteTags(t);
            // A file name left as the title: "Aath_Pahar_Youn_Jhumte", "Harsha-The Great Ruler of Thaneshwar.cbz".
            if (t.Contains('_') && !t.Contains(' ')) t = FileNameParser.Squash(t.Replace('_', ' '));
            t = Regex.Replace(t, @"\.(pdf|epub|mobi|azw3|kfx|cbz|cbr)$", "", RegexOptions.IgnoreCase);
            md.Title = t.Length == 0 || Regex.IsMatch(t, @"^(https?://\S+|www\.\S+|[\w-]+\.(com|net|org|info|biz|ru|cc|to))$", RegexOptions.IgnoreCase) ? null : t;
        }
        // Every reader's author field in one display form: "Harari, Yuval Noah" → "Yuval Noah Harari", "Jason Hannan;" → "Jason Hannan"
        // A web address written there instead is where the file came from, kept as its publisher when it has none.
        md.FallbackPublisher ??= PeopleParser.SiteIn(md.Author);
        if (!string.IsNullOrWhiteSpace(md.Author)) md.Author = PeopleParser.Tidy(md.Author);
        var name = FillFromFileName(md, path);
        // The copyright page's publisher, or the website, only when neither the book nor its file name names one.
        if (string.IsNullOrWhiteSpace(md.Publisher)) md.Publisher = md.FallbackPublisher;
        // "00095 Jasma of Odes": the number in front is the book's place in its series, or the one its file name gives
        // too, not part of its title. Any other number stays: "1001 Magic Tricks", "090 Hari Maut" filed as 120.
        if (md.Title is { } numbered && FileNameParser.SplitCatalogueNumber(numbered) is { } catalogue
            && (!string.IsNullOrWhiteSpace(md.Series) || name.SeriesIndex == catalogue.Number))
        {
            md.Title = catalogue.Title;
            md.SeriesIndex ??= catalogue.Number;
        }
        FixBurmese(md);
        if (string.IsNullOrWhiteSpace(md.Title)) md.Title = "Untitled";
        return md;
    }

    /// <summary>A KFX book's details, from the EPUB copy it is read through (made now, once, if there is none yet).</summary>
    private static async Task<BookMetadata> ReadKfxAsync(string path)
    {
        var (epub, problem) = await KfxBook.EpubAsync(path);
        if (epub is null)
        {
            Log.Write($"KFX details not read ({Path.GetFileName(path)}): {problem?.Text}");
            return new BookMetadata();
        }
        return await Task.Run(() => EpubReader.Read(epub));
    }

    /// <summary>
    /// Burmese that a file (or its name) carries in Zawgyi, or with ေ and ြ in front of their consonant,
    /// shown in Unicode instead. The file itself is never changed.
    /// </summary>
    private static void FixBurmese(BookMetadata md)
    {
        md.Title = Burmese(md.Title);
        md.Author = Burmese(md.Author);
        md.Series = Burmese(md.Series);
        md.Publisher = Burmese(md.Publisher);
        md.Subjects = Burmese(md.Subjects);
        md.Description = Burmese(md.Description);

        static string? Burmese(string? s) => string.IsNullOrEmpty(s) ? s : Zawgyi.Fix(s);
    }

    /// <summary>
    /// Whatever the book itself did not say, from the file name, remembering which details those were. Returns what the
    /// file name says.
    /// </summary>
    public static BookMetadata FillFromFileName(BookMetadata md, string path)
    {
        var name = FileNameParser.Parse(path);
        var fields = 0;
        if (string.IsNullOrWhiteSpace(md.Title) && !string.IsNullOrWhiteSpace(name.Title)) fields |= BookMetadata.NameField.Title;
        if (string.IsNullOrWhiteSpace(md.Author) && !string.IsNullOrWhiteSpace(name.Author)) fields |= BookMetadata.NameField.Author;
        if (string.IsNullOrWhiteSpace(md.Series) && !string.IsNullOrWhiteSpace(name.Series)) fields |= BookMetadata.NameField.Series;
        if (!md.Year.HasValue && name.Year.HasValue) fields |= BookMetadata.NameField.Year;
        if (string.IsNullOrWhiteSpace(md.Publisher) && !string.IsNullOrWhiteSpace(name.Publisher)) fields |= BookMetadata.NameField.Publisher;
        md.MergeFrom(name, overwrite: false);
        md.NameFields = fields;
        return name;
    }
}
