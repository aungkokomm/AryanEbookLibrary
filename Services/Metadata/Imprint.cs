using System.Globalization;
using System.Text.RegularExpressions;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>What a book's copyright page says: the year it was first published and who published it.</summary>
public sealed record ImprintFacts(int? Year, string? YearLine, string? Publisher, string? PublisherLine);

/// <summary>
/// Reads a book's copyright page ("First published: January 2019", "Published by Packt Publishing Ltd.",
/// "Copyright © 2019 Packt Publishing") for the year and the publisher, which almost no PDF says in its metadata.
/// The year is the first publication, as Open Library and Wikidata give it: "first published" beats the copyright
/// line, and of several copyright years the earliest wins. Only a page that looks like a copyright page is read,
/// so a year or a name in the story is never taken.
/// </summary>
public static class Imprint
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Words that only a copyright page has together.</summary>
    private static readonly Regex Marks = new(
        @"all\s+rights\s+reserved|\bisbn\b|published\s+(in\s+[^\n]{0,40}\s+)?by\b|first\s+published|first\s+edition|copyright|©|\(c\)\s*(1[89]|20)\d\d", Options);

    private static readonly Regex FirstPublished = new(
        @"\b(first|originally)\s+(published|edition|printed|issued|printing)\b[^\n]{0,60}?\b(?<y>1[89]\d\d|20\d\d)\b", Options);

    // "Copyright © 2011, 2015", "© 1994-2019", "Copyright c 2019" (the © drawn by a font that calls it "c"), "(c) 2008".
    private static readonly Regex Copyright = new(
        @"(\bcopyright\b:?\s*(©|\(c\)|\bc\b)?|©|\(c\))\s*(?<years>(1[89]\d\d|20\d\d)(\s*[,–—-]\s*(1[89]\d\d|20\d\d|\d\d))*)(?<holder>[^\n]*)", Options);

    private static readonly Regex PublishedIn = new(
        @"\b(this\s+edition\s+)?published\s+(in\s+)?(?<y>1[89]\d\d|20\d\d)\b", Options);

    // "Published by Packt Publishing Ltd.", "First published in Great Britain in 1997 by Bloomsbury Publishing Plc".
    private static readonly Regex PublishedBy = new(@"\bpublished\b[^\n]{0,60}?\bby\s+(?<p>[^\n]+)", Options);
    private static readonly Regex PublisherLabel = new(@"^\s*publisher\s*[:\-–]\s*(?<p>[^\n]+)", Options | RegexOptions.Multiline);

    /// <summary>A copyright holder that is a company, not the author: "Packt Publishing", "Penguin Books".</summary>
    private static readonly Regex CompanyWord = new(
        @"\b(publishing|publishers?|publications|press|books|media|editions|verlag|house|group|sons|company|ltd|inc|llc|plc|gmbh)\b", Options);

    private static readonly Regex CorporateTail = new(
        @"(,?\s+(ltd|limited|inc|incorporated|llc|pvt|pte|private|plc|co|corp|corporation|gmbh|s\.?a|pty)\.?)+\s*$", Options);

    /// <summary>Small words inside a publisher's name: "Taylor &amp; Francis", "Publicacions de la Universitat".</summary>
    private static readonly HashSet<string> Joining = new(StringComparer.Ordinal)
    {
        "&", "and", "of", "for", "on", "to", "a", "an", "at", "in", "the", "de", "la", "du", "der", "des", "für", "y", "e", "et",
        "von", "van", "di", "del"
    };

    /// <summary>Words whose full stop does not end the name: "St. Martin's Press", "Dr. Babasaheb".</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "St", "Mt", "Dr", "Mr", "Mrs", "Ms", "Jr", "Sr", "Bros"
    };

    private static readonly Regex NotAPublisher = new(
        @"\b(rights|reserved|author|authors|isbn|arrangement|permission|www|http|copyright|printed|edition|the\s+publisher|" +
        @"layout|design|designer|editor|editing|typeset|typesetting|illustrations?|cover|photographs?)\b", Options);

    /// <summary>The page has what only a copyright page has together: "All rights reserved", an ISBN, "First published".</summary>
    public static bool LooksLikeOne(string text) => !string.IsNullOrWhiteSpace(text) && Marks.IsMatch(text);

    public static ImprintFacts Read(string text)
    {
        if (!LooksLikeOne(text)) return new(null, null, null, null);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int? year = null;
        string? yearLine = null;
        int? copyrightYear = null;
        string? copyrightLine = null;
        int? publishedYear = null;
        string? publishedLine = null;
        string? publisher = null;
        string? publisherLine = null;
        string? holder = null;
        string? holderLine = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (year is null && FirstPublished.Match(line) is { Success: true } first && Plausible(first.Groups["y"].Value) is { } y1)
            {
                year = y1;
                yearLine = line;
            }
            foreach (Match c in Copyright.Matches(line))
            {
                var earliest = Regex.Matches(c.Groups["years"].Value, @"(1[89]|20)\d\d")
                    .Select(m => Plausible(m.Value)).Where(v => v is not null).Min();
                if (earliest is { } y2 && (copyrightYear is null || y2 < copyrightYear))
                {
                    copyrightYear = y2;
                    copyrightLine = line;
                }
                if (holder is null && Company(c.Groups["holder"].Value) is { } company)
                {
                    holder = company;
                    holderLine = line;
                }
            }
            if (publishedYear is null && PublishedIn.Match(line) is { Success: true } p && Plausible(p.Groups["y"].Value) is { } y3)
            {
                publishedYear = y3;
                publishedLine = line;
            }
            if (publisher is null && PublishedBy.Match(line) is { Success: true } by && Tidy(by.Groups["p"].Value) is { } name)
            {
                publisher = name;
                publisherLine = line;
            }
        }
        if (publisher is null && PublisherLabel.Match(text) is { Success: true } label && Tidy(label.Groups["p"].Value, labelled: true) is { } labelled)
        {
            publisher = labelled;
            publisherLine = label.Value.Trim();
        }

        if (year is null && copyrightYear is not null)
        {
            year = copyrightYear;
            yearLine = copyrightLine;
        }
        if (year is null && publishedYear is not null)
        {
            year = publishedYear;
            yearLine = publishedLine;
        }
        if (publisher is null && holder is not null)
        {
            publisher = holder;
            publisherLine = holderLine;
        }
        return new(year, yearLine, publisher, publisherLine);
    }

    private static int? Plausible(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y >= 1800 && y <= DateTime.Now.Year + 1 ? y : null;

    /// <summary>The copyright holder when it is a company: "2019 Packt Publishing" gives "Packt Publishing", "2011 by John Smith" nothing.</summary>
    private static string? Company(string holder)
    {
        holder = Regex.Replace(holder, @"^\s*(by\s+)?", "", Options);
        return CompanyWord.IsMatch(holder) ? Tidy(holder) : null;
    }

    /// <summary>
    /// A publisher's name as a catalogue shows it: up to the first comma, full stop or number (an address follows),
    /// without "Ltd." or a leading "the", and no longer than a name can be.
    /// </summary>
    private static string? Tidy(string s, bool labelled = false)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        // A letter-spaced PDF splits words: "Prince ton University Press", "Th e Johns Hopkins".
        s = Regex.Replace(s, @"\b(?<a>[A-Z][a-z]+) (?<b>[a-z]{1,4}) (?=[A-Z])", m =>
            Joining.Contains(m.Groups["b"].Value) && m.Groups["b"].Value != "e" ? m.Value : m.Groups["a"].Value + m.Groups["b"].Value + " ");
        // "H.G. Mirchandani for IBH Publishers Pvt Ltd": the company it was done for is the publisher.
        if (Regex.Match(s, @"\sfor\s+(?<company>.+)$", Options) is { Success: true } forWhom && CompanyWord.IsMatch(forWhom.Groups["company"].Value))
            s = forWhom.Groups["company"].Value;
        var cut = s.Length;
        foreach (var stop in new[] { ",", ";", ":", " - ", "(", " in ", " at " })
        {
            var i = s.IndexOf(stop, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && i < cut) cut = i;
        }
        // A comma or semicolon inside a word is a scan's misread ("Indi;a Book House"), not the end of the name.
        if (cut < s.Length - 1 && s[cut] is ',' or ';' && char.IsLetter(s[cut - 1]) && char.IsLetter(s[cut + 1])) return null;
        // A number starting a word begins the address ("14 Marthanda"); one inside a word is a misread ("Amar Chitra K3tha").
        if (Regex.IsMatch(s[..cut], @"\p{L}\d|\d\p{L}")) return null;
        if (Regex.Match(s, @"(^|\s)\d") is { Success: true } number && number.Index < cut) cut = number.Index;
        // A full stop ends the name after a word or "Ltd." ("Packt Publishing Ltd. Livery Place"), not after an
        // initial ("W. W. Norton & Company").
        foreach (Match dot in Regex.Matches(s, @"(?<word>[A-Za-z]+)\.\s"))
        {
            var word = dot.Groups["word"].Value;
            if (dot.Index < cut && word.Length >= 2 && !Abbreviations.Contains(word))
            {
                cut = dot.Index + word.Length;
                break;
            }
        }
        s = s[..cut].Trim().TrimEnd('.', ':', '-', '–').Trim();
        s = CorporateTail.Replace(s, "").Trim();
        s = Regex.Replace(s, @"^the\s+", "", Options);
        // A name's words are capitalised but for small joining words; the first other lower-case word ends it
        // ("Taylor & Francis and available online", "Pelckmans Pro with the", "POCKET BOOKS g +").
        var kept = new List<string>();
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!char.IsUpper(w[0]) && !char.IsDigit(w[0]) && !Joining.Contains(w)) break;
            kept.Add(w);
        }
        while (kept.Count > 0 && Joining.Contains(kept[^1])) kept.RemoveAt(kept.Count - 1);
        s = string.Join(' ', kept);
        if (s.Length == 0) return null;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 8 || s.Length > 70 || NotAPublisher.IsMatch(s)) return null;
        // "H.G. Mirchandani": initials and a surname are the person who publishes, not the company.
        if (Regex.IsMatch(s, @"^([A-Z]\.?\s?){1,3}[A-Z][a-z]") && !CompanyWord.IsMatch(s)) return null;
        if (!char.IsUpper(s[0]) && !char.IsDigit(s[0])) return null;   // "published by us", "by permission"
        // Too short to be sure of ("Published online by Cam" + "bridge" on the next line), unless it is initials ("DK").
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count < 2 || letters.Count < 4 && !letters.All(char.IsUpper)) return null;
        // "Publisher: William Pollock" names the person who runs the publisher, not the company.
        if (labelled && words.Length is 2 or 3 && !CompanyWord.IsMatch(s) && words.All(w => char.IsUpper(w[0]))) return null;
        // "PENGUIN BOOKS" reads "Penguin Books"; a short one ("DK", "HBR") stays.
        if (words.Length > 1 && s.Where(char.IsLetter).All(char.IsUpper))
            s = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
        return s;
    }
}
