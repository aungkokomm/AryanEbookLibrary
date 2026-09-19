using System.IO.Compression;
using System.Xml.Linq;
using AryanEbookLibrary.Models;
using SharpCompress.Archives.Rar;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>CBZ (zip) and CBR (rar) comic archives: cover = first page, plus ComicInfo.xml when present.</summary>
public static class ComicReader
{
    private const int MaxImageBytes = 25 * 1024 * 1024;

    public static BookMetadata Read(string path)
    {
        // Extensions lie: many ".cbr" files are really zips and vice versa. Sniff the header instead.
        var isZip = false;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var head = new byte[4];
            var n = fs.Read(head, 0, 4);
            isZip = n >= 2 && head[0] == 0x50 && head[1] == 0x4B;
        }
        return isZip ? ReadZip(path) : ReadRar(path);
    }

    public static BookMetadata ReadCbz(string path) => Read(path);
    public static BookMetadata ReadCbr(string path) => Read(path);

    private static BookMetadata ReadZip(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var meta = new BookMetadata();

        var infoEntry = zip.Entries.FirstOrDefault(e =>
            e.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        if (infoEntry is not null)
        {
            try
            {
                using var s = infoEntry.Open();
                ApplyComicInfo(meta, XmlUtil.Load(s));
            }
            catch
            {
                // ignore a broken ComicInfo.xml
            }
        }

        var pages = zip.Entries
            .Where(e => e.Length > 0 && !e.FullName.Contains("__MACOSX", StringComparison.Ordinal) &&
                        ImageSniffer.IsImageName(e.Name))
            .OrderBy(e => e.FullName, NaturalComparer.Instance)
            .ToList();

        meta.PageCount ??= pages.Count;

        if (pages.Count > 0 && pages[0].Length <= MaxImageBytes)
        {
            using var s = pages[0].Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            SetCover(meta, ms.ToArray());
        }
        return meta;
    }

    private static BookMetadata ReadRar(string path)
    {
        var meta = new BookMetadata();
        using var archive = RarArchive.Open(path);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();

        var infoEntry = entries.FirstOrDefault(e =>
            Path.GetFileName(e.Key ?? "").Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        if (infoEntry is not null)
        {
            try
            {
                using var s = infoEntry.OpenEntryStream();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                ApplyComicInfo(meta, XmlUtil.Load(ms));
            }
            catch
            {
            }
        }

        var pages = entries
            .Where(e => e.Key is not null && e.Size > 0 && ImageSniffer.IsImageName(e.Key))
            .OrderBy(e => e.Key ?? "", NaturalComparer.Instance)
            .ToList();

        meta.PageCount ??= pages.Count;

        if (pages.Count > 0 && pages[0].Size <= MaxImageBytes)
        {
            using var s = pages[0].OpenEntryStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            SetCover(meta, ms.ToArray());
        }
        return meta;
    }

    private static void SetCover(BookMetadata meta, byte[] bytes)
    {
        var ext = ImageSniffer.Ext(bytes);
        if (ext is null) return;
        meta.Cover = bytes;
        meta.CoverExt = ext;
    }

    private static void ApplyComicInfo(BookMetadata meta, XDocument doc)
    {
        string? Get(string name) =>
            XmlUtil.Clean(doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value);

        var series = Get("Series");
        var number = Get("Number");
        var title = Get("Title");

        meta.Series = series;
        if (double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            meta.SeriesIndex = n;

        meta.Title = title ?? (series is null ? null : number is null ? series : $"{series} #{number}");
        meta.Author = Get("Writer");
        meta.Publisher = Get("Publisher");
        meta.Language = Get("LanguageISO");
        meta.Subjects = Get("Genre");
        var summary = Get("Summary");
        if (summary is not null) meta.Description = summary;
        meta.Year = XmlUtil.ParseYear(Get("Year"));
        if (int.TryParse(Get("PageCount"), out var pc) && pc > 0) meta.PageCount = pc;
    }
}
