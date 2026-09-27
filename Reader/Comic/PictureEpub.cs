using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Reader.Comic;

/// <summary>
/// A fixed-layout EPUB that is only pictures, as comics and graphic novels are made: every page in the spine is one
/// picture, with nothing else on it to read (invisible links laid over the picture are allowed). The comic reader
/// shows such a book one page at a time, fitted to the width and scrolling, where the book reader showed spreads.
/// </summary>
internal static class PictureEpub
{
    private const string CfiSpine = "epub1;epubcfi(/6/";

    /// <summary>Whether the file is a picture book. Anything unreadable is not, and goes to the book reader.</summary>
    public static bool Is(string path)
    {
        try
        {
            using var zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), ZipArchiveMode.Read);
            return Pages(zip) is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The book's pictures in reading order, or null when it is not a picture book.</summary>
    public static List<ZipArchiveEntry>? Pages(ZipArchive zip)
    {
        try
        {
            if (EpubReader.FindOpfPath(zip) is not { } opfPath || EpubReader.Find(zip, opfPath) is not { } opfEntry) return null;
            var opf = Load(opfEntry);
            var fixedLayout = opf.Descendants().Any(e => e.Name.LocalName == "meta" &&
                (string?)e.Attribute("property") == "rendition:layout" && e.Value.Trim() == "pre-paginated");
            if (!fixedLayout) return null;

            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
                if ((string?)item.Attribute("id") is { } id && (string?)item.Attribute("href") is { } href) items.TryAdd(id, href);

            var pages = new List<ZipArchiveEntry>();
            foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
            {
                if ((string?)itemref.Attribute("idref") is not { } idref || !items.TryGetValue(idref, out var href)) return null;
                var pagePath = EpubReader.Resolve(Folder(opfPath), href);
                if (EpubReader.Find(zip, pagePath) is not { } pageEntry) return null;
                var body = Load(pageEntry).Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
                if (body is null) return null;

                var pictures = body.Descendants().Where(e => e.Name.LocalName is "img" or "image").Take(2).ToList();
                if (pictures.Count != 1) return null;
                var text = body.DescendantNodes().OfType<XText>()
                    .Any(t => !string.IsNullOrWhiteSpace(t.Value) && !t.Ancestors().Any(a => a.Name.LocalName == "a"));
                if (text) return null;

                var picture = pictures[0];
                var src = (string?)picture.Attribute("src") ?? (string?)picture.Attribute("href") ??
                          (string?)picture.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"));
                if (src is null || EpubReader.Find(zip, EpubReader.Resolve(Folder(pagePath), src)) is not { } image) return null;
                pages.Add(image);
            }
            return pages.Count > 0 ? pages : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page a place saved by the book reader names, or null. Its "epubcfi(/6/N" is spine item N/2 - 1, and in a
    /// picture book that is the page; its page numbers are its own screens, not the book's pages.
    /// </summary>
    public static int? SpinePage(string position)
    {
        if (!position.StartsWith(CfiSpine, StringComparison.Ordinal)) return null;
        var digits = new string(position[CfiSpine.Length..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var step) && step >= 2 ? step / 2 - 1 : null;
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        return XmlUtil.Load(s);
    }

    private static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
}
