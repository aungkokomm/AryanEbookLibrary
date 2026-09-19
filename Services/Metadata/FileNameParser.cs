using System.Globalization;
using System.Text.RegularExpressions;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Title (and, where the name says so unambiguously, author and series) from a book's file name, for
/// books whose own metadata has none. 42% of a real 2,114-book library ends up here, so the rules come
/// from those names:
///  - download-site noise is dropped: "(Z-Library)", "- PDF Room", "pdfcoffee com", archive.org ids,
///    and Z-Library's "Title (Author) (Z-Library)" gives the author;
///  - zero-padded catalogue numbers ("00297 The Lost Prince") become the series index, while plain
///    numbers that belong to the title ("1001 Magic Tricks") stay;
///  - "ack ..." (and "(Amar Chitra Katha)") is the Amar Chitra Katha series;
///  - "Title by Author" and Anna's Archive "Title -- Author -- ..." give the author;
///  - Myanmar "Author၊ Title" (the Myanmar comma after a short name) gives the author;
///  - "slug-style-names" get their spaces back, and all-lowercase / ALL-CAPS Latin names get Title Case.
/// A hyphen between two Myanmar parts is left alone: libraries use both Author-Title and Title-Author.
/// </summary>
public static class FileNameParser
{
    private const string AmarChitraKatha = "Amar Chitra Katha";

    private static readonly Regex[] Noise =
    {
        new(@"\((z-?lib(rary|\.org)?|anna'?s archive|libgen[^)]*)\)", RegexOptions.IgnoreCase),
        new(@"\s-\s*pdf\s*room$", RegexOptions.IgnoreCase),
        new(@"^pdfcoffee(\.|\s)com\s*", RegexOptions.IgnoreCase),
        new(@"[\s-]*NoRestriction$", RegexOptions.IgnoreCase),   // Myanmar ebook site tag
        new(@"\s\(\d\)$")                                   // "name (1)" copies
    };

    private static readonly Regex ArchiveOrgId = new(@"^\d{4}\.\d{4,}\.");
    private static readonly Regex CatalogueNumber = new(@"^(0\d{1,5})\s+");
    private static readonly Regex AckPrefix = new(@"^ack\s+", RegexOptions.IgnoreCase);
    private static readonly Regex AckSuffix = new(
        @"\(\s*amar chitra katha(\s+comics)?\s*\)|\s*(-\s*)?amar chitra katha\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex ZLibrary = new(@"\(z-?lib(rary|\.org)?\)", RegexOptions.IgnoreCase);
    private static readonly Regex TrailingParenthesis = new(@"^(?<title>.+?)\s*\((?<author>[^()]+)\)$");
    private static readonly Regex ByAuthor = new(@"^(?<title>.+?)\s+by\s+(?<author>[A-Z][\w.'-]*(\s+[A-Z][\w.'-]*){0,3})$");
    private static readonly Regex AnnasArchive = new(@"^(?<title>.+?)\s+--\s+(?<author>[^-]+?)(\s+--.*)?$");
    private static readonly Regex AuthorRole = new(@"\(\s*(auth|author|editor|ed|eds|trans|translator)\.?\s*\)", RegexOptions.IgnoreCase);
    private static readonly Regex MyanmarDigit = new(@"[၀-၉႐-႙]");

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "to", "for", "with", "at", "by", "from"
    };

    public static BookMetadata Parse(string path)
    {
        var md = new BookMetadata();
        var name = Path.GetFileNameWithoutExtension(path);

        name = Regex.Replace(name, @"(?<=[A-Za-z])_s_", "'s_");   // "valmiki_s_ramayana" keeps its apostrophe
        name = Squash(name.Replace('_', ' '));
        var fromZLibrary = ZLibrary.IsMatch(name);   // "Title (Author) (Z-Library)"
        foreach (var rx in Noise) name = Squash(rx.Replace(name, " "));
        name = ArchiveOrgId.Replace(name, "");

        var number = CatalogueNumber.Match(name);
        if (number.Success)
        {
            md.SeriesIndex = int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture);
            name = name[number.Length..];
        }

        if (AckPrefix.IsMatch(name) || AckSuffix.IsMatch(name))
        {
            md.Series = AmarChitraKatha;
            name = Squash(AckSuffix.Replace(AckPrefix.Replace(name, ""), ""));
        }

        if (fromZLibrary && TrailingParenthesis.Match(name) is { Success: true } zl)
        {
            name = zl.Groups["title"].Value;
            md.Author = zl.Groups["author"].Value.Trim();
        }
        else if (AnnasArchive.Match(name) is { Success: true } aa)
        {
            name = aa.Groups["title"].Value;
            md.Author = aa.Groups["author"].Value.Trim();
        }
        else if (ByAuthor.Match(name) is { Success: true } by)
        {
            name = by.Groups["title"].Value;
            md.Author = by.Groups["author"].Value;
        }
        else if (SplitMyanmarAuthor(name) is { } mm)
        {
            md.Author = mm.Author;
            name = mm.Title;
        }

        if (!HasMyanmar(name) && !name.Contains(' ') && name.Contains('-')) name = name.Replace('-', ' ');
        name = Squash(string.Join(' ', name.Split(' ').Select(SplitCamelCase))).Trim(' ', '-', ',', '.');
        if (IsSingleCaseLatin(name)) name = TitleCase(name);

        md.Title = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path);
        if (md.Author is not null) md.Author = Squash(AuthorRole.Replace(md.Author, " "));   // "Jean Armstrong (auth )"
        return md;
    }

    /// <summary>
    /// The Myanmar comma separates author and title, and the space after it tells the order (true for all
    /// 80 such names in a real library): "ဇော်ဂျီ၊ ရွှေမောင်းသံ" is Author၊ Title, "ကြာ၊နန္ဒာသိန်းဇံ" is
    /// Title၊Author. The name side is short (at most 17 characters there) and has no digits, which keeps
    /// volume numbers ("အတွဲ ၄၊ အမှတ် ၁") and years out.
    /// </summary>
    private static (string Author, string Title)? SplitMyanmarAuthor(string name)
    {
        var i = name.IndexOf('၊');   // ၊
        if (i <= 0 || i >= name.Length - 1) return null;
        var left = name[..i].Trim();
        var rest = name[(i + 1)..];
        var authorFirst = rest.StartsWith(' ');
        var right = rest.Trim();
        var (author, title) = authorFirst ? (left, right) : (right, left);

        if (!HasMyanmar(author) || title.Length == 0 || author.Length > 20) return null;
        if (author.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return null;
        if (MyanmarDigit.IsMatch(author) || author.Any(char.IsAsciiDigit)) return null;
        return (author, title);
    }

    private static bool HasMyanmar(string s) => s.Any(c => c is >= 'က' and <= '႟');

    /// <summary>All lowercase, or ALL CAPS with a real word in it ("RANI OF JHANSI", not codes like "STJ 03-26 S").</summary>
    private static bool IsSingleCaseLatin(string s)
    {
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count == 0 || !letters.All(char.IsAscii)) return false;
        if (letters.All(char.IsLower)) return true;
        return letters.All(char.IsUpper) && s.Split(' ').Any(w => w.Count(char.IsLetter) >= 4);
    }

    /// <summary>"ShrenikAndTheHiddenTruth" → "Shrenik and the Hidden Truth". Needs two or more joins, so "iPhone" or "McDonald" stay.</summary>
    private static string SplitCamelCase(string word)
    {
        var joins = CamelJoin.Matches(word).Count;
        if (joins < 2) return word;
        var parts = CamelJoin.Replace(word, " ").Split(' ');
        for (var i = 1; i < parts.Length; i++)
            if (SmallWords.Contains(parts[i])) parts[i] = parts[i].ToLowerInvariant();
        return string.Join(' ', parts);
    }

    private static readonly Regex CamelJoin = new(@"(?<=[a-z])(?=[A-Z])");

    private static string TitleCase(string s)
    {
        var words = s.ToLowerInvariant().Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (words[i].Length > 0 && (i == 0 || !SmallWords.Contains(words[i])))
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
