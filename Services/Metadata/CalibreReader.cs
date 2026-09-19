using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Calibre stores each book as  Author/Title (id)/book.epub  next to  metadata.opf  and  cover.jpg.
/// When those exist we treat them as the user-curated truth and let them override embedded metadata.
/// This is a reader only, nothing is ever written to the Calibre folder.
/// </summary>
public static class CalibreReader
{
    private static readonly string[] CoverNames = { "cover.jpg", "cover.jpeg", "cover.png", "cover.webp" };

    public static BookMetadata? Read(string directory)
    {
        var opf = Path.Combine(directory, "metadata.opf");
        var hasOpf = File.Exists(opf);

        var meta = new BookMetadata();
        var found = false;

        if (hasOpf)
        {
            using var fs = new FileStream(opf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            meta = OpfParser.Parse(XmlUtil.Load(fs)).Meta;
            found = true;
        }

        foreach (var name in CoverNames)
        {
            var cover = Path.Combine(directory, name);
            if (!File.Exists(cover)) continue;

            var fi = new FileInfo(cover);
            if (fi.Length is 0 or > 20 * 1024 * 1024) continue;

            var bytes = File.ReadAllBytes(cover);
            var ext = ImageSniffer.Ext(bytes);
            if (ext is null) continue;

            meta.Cover = bytes;
            meta.CoverExt = ext;
            found = true;
            break;
        }

        return found ? meta : null;
    }
}
