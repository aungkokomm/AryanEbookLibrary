using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Picks the right reader for a file. Reader-only, like CineLibrary: nothing is scraped online and
/// nothing is written to the user's book folders.
/// Order: embedded metadata first, then Calibre's metadata.opf / cover.jpg override it when present.
/// </summary>
public static class MetadataService
{
    public static async Task<BookMetadata> ReadAsync(string path, BookFormat format, bool useCalibre)
    {
        var md = new BookMetadata();

        try
        {
            md = format switch
            {
                BookFormat.Epub => await Task.Run(() => EpubReader.Read(path)),
                BookFormat.Pdf => await PdfReader.ReadAsync(path),
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

        // Whatever the book itself did not say: title, author, series from the file name (fills gaps only).
        md.MergeFrom(FileNameParser.Parse(path), overwrite: false);
        if (string.IsNullOrWhiteSpace(md.Title)) md.Title = "Untitled";
        return md;
    }
}
