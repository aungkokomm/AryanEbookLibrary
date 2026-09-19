using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AryanEbookLibrary.Services.Metadata;

internal static class ImageSniffer
{
    /// <summary>Returns ".jpg", ".png", ".gif", ".webp" or null if the bytes are not a known image.</summary>
    public static string? Ext(byte[] b)
    {
        if (b.Length < 12) return null;
        if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return ".webp";
        return null;
    }

    public static bool IsImageName(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class XmlUtil
{
    public static XDocument Load(Stream s)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false
        };
        using var reader = XmlReader.Create(s, settings);
        return XDocument.Load(reader);
    }

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t\r\f\v]+", RegexOptions.Compiled);

    /// <summary>Strips HTML tags/entities from descriptions (Calibre and EPUB store HTML there).</summary>
    public static string StripHtml(string html)
    {
        var withBreaks = Regex.Replace(html, @"<\s*(br|/p|/div|/li)\s*/?>", "\n", RegexOptions.IgnoreCase);
        var text = System.Net.WebUtility.HtmlDecode(Tags.Replace(withBreaks, ""));
        text = Spaces.Replace(text, " ");
        text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n");
        return text.Trim();
    }

    /// <summary>Removes control characters and trims. Returns null for empty input.</summary>
    public static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsControl(c) || c is '\n' or '\t') sb.Append(c);
        var r = sb.ToString().Trim();
        return r.Length == 0 ? null : r;
    }

    public static int? ParseYear(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = Regex.Match(s, @"\b(1[0-9]{3}|20[0-9]{2})\b");
        return m.Success ? int.Parse(m.Value) : null;
    }
}

/// <summary>Natural ("Page 2" before "Page 10") string comparer, used to find the first page of a comic.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = a.CompareTo(b, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
