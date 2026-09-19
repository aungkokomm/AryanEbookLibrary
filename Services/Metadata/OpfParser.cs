using System.Text.RegularExpressions;
using System.Xml.Linq;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

internal sealed class OpfResult
{
    public BookMetadata Meta { get; init; } = new();
    /// <summary>Cover href as written in the manifest (relative to the OPF file), if the OPF declares one.</summary>
    public string? CoverHref { get; init; }
}

/// <summary>
/// Parses an OPF package document. The same format is used inside EPUB files and by Calibre's
/// metadata.opf, so both readers share this parser.
/// </summary>
internal static class OpfParser
{
    public static OpfResult Parse(XDocument doc)
    {
        var m = new BookMetadata();
        var md = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (md is null) return new OpfResult { Meta = m };

        var elements = md.Elements().ToList();
        IEnumerable<XElement> Dc(string name) =>
            elements.Where(e => e.Name.LocalName == name && !string.IsNullOrWhiteSpace(e.Value));

        m.Title = XmlUtil.Clean(Dc("title").FirstOrDefault()?.Value);

        var creators = Dc("creator")
            .Where(e =>
            {
                var role = AttrLocal(e, "role");
                return role is null || role.Equals("aut", StringComparison.OrdinalIgnoreCase);
            })
            .Select(e => XmlUtil.Clean(e.Value))
            .Where(s => s is not null)
            .Distinct()
            .ToList();
        if (creators.Count > 0) m.Author = string.Join(", ", creators);

        m.Publisher = XmlUtil.Clean(Dc("publisher").FirstOrDefault()?.Value);
        m.Language = XmlUtil.Clean(Dc("language").FirstOrDefault()?.Value);
        m.Year = XmlUtil.ParseYear(Dc("date").FirstOrDefault()?.Value);

        var desc = Dc("description").FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(desc)) m.Description = XmlUtil.Clean(XmlUtil.StripHtml(desc));

        var subjects = Dc("subject").Select(e => XmlUtil.Clean(e.Value)).Where(s => s is not null).Distinct().ToList();
        if (subjects.Count > 0) m.Subjects = string.Join(", ", subjects);

        foreach (var id in Dc("identifier"))
        {
            var isbn = ExtractIsbn(id.Value, AttrLocal(id, "scheme"));
            if (isbn is not null)
            {
                m.Isbn = isbn;
                break;
            }
        }

        // Series: Calibre style <meta name="calibre:series" content="..."/> and EPUB 3 belongs-to-collection
        foreach (var meta in elements.Where(e => e.Name.LocalName == "meta"))
        {
            var name = (string?)meta.Attribute("name");
            var content = (string?)meta.Attribute("content");
            var property = (string?)meta.Attribute("property");

            if (name == "calibre:series" && !string.IsNullOrWhiteSpace(content))
                m.Series ??= XmlUtil.Clean(content);
            else if (name == "calibre:series_index" && double.TryParse(content,
                         System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var idx))
                m.SeriesIndex ??= idx;
            else if (property == "belongs-to-collection" && !string.IsNullOrWhiteSpace(meta.Value))
                m.Series ??= XmlUtil.Clean(meta.Value);
            else if (property == "group-position" && double.TryParse(meta.Value,
                         System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos))
                m.SeriesIndex ??= pos;
        }

        return new OpfResult { Meta = m, CoverHref = FindCoverHref(doc, elements) };
    }

    private static string? FindCoverHref(XDocument doc, List<XElement> metaElements)
    {
        var manifest = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "manifest");
        if (manifest is null) return null;
        var items = manifest.Elements().Where(e => e.Name.LocalName == "item").ToList();

        // 1) <meta name="cover" content="item-id"/>
        var coverId = metaElements
            .Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover")
            .Select(e => (string?)e.Attribute("content"))
            .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (coverId is not null)
        {
            var byId = items.FirstOrDefault(i => (string?)i.Attribute("id") == coverId);
            if (byId is not null && IsImageItem(byId)) return (string?)byId.Attribute("href");
        }

        // 2) EPUB 3: properties="cover-image"
        var byProp = items.FirstOrDefault(i =>
            ((string?)i.Attribute("properties") ?? "").Split(' ').Contains("cover-image"));
        if (byProp is not null) return (string?)byProp.Attribute("href");

        // 3) <guide><reference type="cover" href="..."/> only if it points at an image
        // 4) fall back to any image item whose id/href mentions "cover"
        var guess = items.FirstOrDefault(i => IsImageItem(i) &&
            (((string?)i.Attribute("id") ?? "").Contains("cover", StringComparison.OrdinalIgnoreCase) ||
             ((string?)i.Attribute("href") ?? "").Contains("cover", StringComparison.OrdinalIgnoreCase)));
        return guess is null ? null : (string?)guess.Attribute("href");
    }

    private static bool IsImageItem(XElement item)
    {
        var type = (string?)item.Attribute("media-type") ?? "";
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
        return ImageSniffer.IsImageName((string?)item.Attribute("href") ?? "");
    }

    private static string? AttrLocal(XElement e, string local) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == local)?.Value;

    private static string? ExtractIsbn(string value, string? scheme)
    {
        var v = value.Trim();
        var isIsbn = (scheme?.Equals("ISBN", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     v.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase);
        var digits = Regex.Replace(v, @"^urn:isbn:", "", RegexOptions.IgnoreCase);
        digits = Regex.Replace(digits, @"[\s-]", "");

        if (Regex.IsMatch(digits, @"^(97[89]\d{10}|\d{9}[\dXx])$")) return digits.ToUpperInvariant();
        return isIsbn && digits.Length > 0 ? digits : null;
    }
}
