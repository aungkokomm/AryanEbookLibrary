using System.IO.Compression;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>Reads metadata and the cover straight from an .epub (a zip with an OPF package inside).</summary>
public static class EpubReader
{
    private const int MaxCoverBytes = 20 * 1024 * 1024;

    public static BookMetadata Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var opfPath = FindOpfPath(zip);
        if (opfPath is null) return new BookMetadata();

        var opfEntry = Find(zip, opfPath);
        if (opfEntry is null) return new BookMetadata();

        OpfResult parsed;
        using (var s = opfEntry.Open())
            parsed = OpfParser.Parse(XmlUtil.Load(s));

        var meta = parsed.Meta;
        try
        {
            meta.PageCount ??= EstimatePages(zip) is > 0 and var pages ? pages : null;
        }
        catch (Exception)
        {
            // a damaged chapter: the length stays unknown, the rest of the details are still good
        }

        if (parsed.CoverHref is not null)
        {
            var baseDir = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";
            var coverEntry = Find(zip, Resolve(baseDir, parsed.CoverHref));
            if (coverEntry is not null && coverEntry.Length is > 0 and <= MaxCoverBytes)
            {
                var bytes = ReadAll(coverEntry);
                var ext = ImageSniffer.Ext(bytes);
                if (ext is not null)
                {
                    meta.Cover = bytes;
                    meta.CoverExt = ext;
                }
            }
        }

        return meta;
    }

    /// <summary>A printed page holds about this much text, spaces included.</summary>
    public const int CharsPerPage = 1800;

    /// <summary>
    /// About how many printed pages the book's text would fill, tags left out. A picture book (next to no text in
    /// many documents) has a page per document. Reads at most 40 MB of chapters, so a huge book cannot hold up a scan.
    /// </summary>
    internal static int EstimatePages(ZipArchive zip)
    {
        long chars = 0, budget = 40L * 1024 * 1024;
        var docs = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!(name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))) continue;
            if (budget <= 0) break;
            budget -= entry.Length;
            docs++;
            chars += TextLength(entry);
        }
        if (docs == 0) return 0;
        if (docs >= 5 && chars < docs * 100L) return docs;
        return (int)Math.Max(1, Math.Round(chars / (double)CharsPerPage));
    }

    /// <summary>The characters a reader sees: no tags, and a run of spaces and line breaks counts as one.</summary>
    private static long TextLength(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        var buffer = new char[16384];
        long count = 0;
        bool inTag = false, space = true;
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (inTag) { if (c == '>') inTag = false; continue; }
                if (c == '<') { inTag = true; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (!space) count++;
                    space = true;
                    continue;
                }
                space = false;
                count++;
            }
        return count;
    }

    internal static string? FindOpfPath(ZipArchive zip)
    {
        var container = Find(zip, "META-INF/container.xml");
        if (container is not null)
        {
            try
            {
                using var s = container.Open();
                var doc = XmlUtil.Load(s);
                var full = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "rootfile")
                    ?.Attribute("full-path")?.Value;
                if (!string.IsNullOrWhiteSpace(full)) return Normalize(full);
            }
            catch
            {
                // fall through to the scan below
            }
        }

        return zip.Entries
            .FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
            ?.FullName;
    }

    private static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');

    internal static ZipArchiveEntry? Find(ZipArchive zip, string path)
    {
        path = Normalize(path);
        return zip.GetEntry(path) ??
               zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
    }

    internal static string Resolve(string baseDir, string href)
    {
        href = Uri.UnescapeDataString(href.Split('#')[0]).Replace('\\', '/');
        var combined = href.StartsWith('/') || baseDir.Length == 0 ? href : baseDir + "/" + href;

        var parts = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
