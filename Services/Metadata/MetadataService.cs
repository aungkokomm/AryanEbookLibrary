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
    /// </summary>
    public const int Version = 2;

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
            md.Title = t.Length == 0 || Regex.IsMatch(t, @"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase) ? null : t;
        }
        // Every reader's author field in one display form: "Harari, Yuval Noah" → "Yuval Noah Harari", "Jason Hannan;" → "Jason Hannan"
        if (!string.IsNullOrWhiteSpace(md.Author)) md.Author = PeopleParser.Tidy(md.Author);
        FillFromFileName(md, path);
        if (string.IsNullOrWhiteSpace(md.Title)) md.Title = "Untitled";
        return md;
    }

    /// <summary>Whatever the book itself did not say, from the file name, remembering which details those were.</summary>
    public static void FillFromFileName(BookMetadata md, string path)
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
    }
}
