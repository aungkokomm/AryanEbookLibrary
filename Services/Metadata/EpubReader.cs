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
