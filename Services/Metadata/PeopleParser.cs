using System.Text.RegularExpressions;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Author fields as books really write them, turned into display names:
///   "Arden, John B.;"                         → John B. Arden
///   "Barnes, Mark, Gonzalez, Jennifer"         → Mark Barnes, Jennifer Gonzalez
///   "Baker, Chris; Phongpaichit, Pasuk"        → Chris Baker, Pasuk Phongpaichit
///   "A. C. Bhaktivedanta Swami Prabhupada, 1896-1977" → A. C. Bhaktivedanta Swami Prabhupada
///   "Campbell, Josette, author"                → Josette Campbell
/// Names that are already "First Last, First Last" are kept as they are.
/// </summary>
public static class PeopleParser
{
    private static readonly Regex Parenthesised = new(@"\([^)]*\)");
    private static readonly Regex LifeDates = new(@",?\s*\b(1[5-9]|20)\d\d\s*-\s*((1[5-9]|20)\d\d)?(?!\d)");
    private static readonly Regex Roles = new(
        @"(,\s*|\s+)\b(author|editor|editors|ed|eds|translator|trans|illustrator|compiler|contributor|foreword)\b\.?", RegexOptions.IgnoreCase);
    // A degree written as its own comma piece: "Ian Stevenson, M.D.", "Mark F. Bear, Ph.D., Barry W. Connors, Ph.D."
    private static readonly Regex Degree = new(
        @"^(M\.?\s?D|Ph\.?\s?D|D\.?\s?Phil|D\.?\s?Litt|M\.?\s?Sc|M\.?\s?A|B\.?\s?A|MBBS|FRCP|FRS)\.?$", RegexOptions.IgnoreCase);
    private static readonly Regex WholeNameRole = new(
        @"[^\s,]\s+[^\s,]+\s*\((translator|trans|editor|ed|eds|illustrator|compiler|foreword)\.?\)", RegexOptions.IgnoreCase);
    private static readonly Regex Suffix = new(@"^(Jr|Sr|II|III|IV|LLC|Inc|Ltd)\.?$");   // belongs to the name before it
    private static readonly Regex EtAl = new(@",?\s*\bet\.?\s*al\b\.?", RegexOptions.IgnoreCase);
    // "by Matthew G. Naugle, PDFed by UncleVan": the "by" goes, and so does the scanner's credit.
    private static readonly Regex LeadingBy = new(@"^by\s+", RegexOptions.IgnoreCase);
    private static readonly Regex ScanCredit = new(
        @"^(pdfed|scanned|ocr'?ed|converted|uploaded|ripped|typed|digiti[sz]ed)\s+by\b", RegexOptions.IgnoreCase);
    // "Ikenna Nwaiwu<br><i>Foreword by Melissa van der Hecht</i>": markup from a web page, and whoever wrote the
    // foreword, who is not an author of the book.
    private static readonly Regex HtmlBreak = new(@"<\s*(br|/p|/div|/li)\s*/?>", RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTag = new(@"<[^>]{1,40}>");
    private static readonly Regex Foreword = new(
        @"(\bwith\s+)?(\ban?\s+)?\b(foreword|introduction|preface|afterword)\s+by\b[^;]*", RegexOptions.IgnoreCase);

    // Not people: a web address or its site ("www.oshoworld.com", "https://www.pdfmagaz.in", "savarkar.org"), and the
    // program that made the file ("ComicRack", "CamScanner", "Microsoft Office User", "JPG To PDF Converter").
    private static readonly Regex Site = new(
        @"^(https?://)?(www\.)?(?<host>[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|net|org|info|biz|in|co|io|me|ru|to|cc|us|uk|de|edu|gov|xyz|site|online|tk))(\.[a-z]{2})?(/\S*)?$",
        RegexOptions.IgnoreCase);
    private static readonly Regex Program = new(
        @"^(comicrack|camscanner|calibre|pdfcreator|pdf creator|pdf24|ilovepdf|smallpdf|ghostscript|pdftex|genius scan|adobe scan|scanbot|" +
        @"adobe systems( incorporated)?|adobe acrobat.*|acrobat .*|nitro( pdf.*)?|foxit.*|abbyy.*|finereader.*|office lens|microsoft lens|" +
        @"(microsoft )?office user|.*\b(converter|scanner)\b.*)$", RegexOptions.IgnoreCase);

    public static List<string> Parse(string? raw)
    {
        var people = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return people;

        var s = raw.Trim();
        if (s.Contains('<'))
        {
            s = HtmlBreak.Replace(s, ";");
            s = HtmlTag.Replace(s, " ");
        }
        if (s.Contains('&')) s = System.Net.WebUtility.HtmlDecode(s);
        s = Foreword.Replace(s, ";");
        if (s.StartsWith('(') && s.EndsWith(')') && s.IndexOf(')') == s.Length - 1) s = s[1..^1];   // "( Mg-Tun-Thu )"
        // "Confucius, James Legge (Translator)": a role in brackets after a full name shows the names are whole
        // names, not "Last, First" pairs.
        var wholeNames = WholeNameRole.IsMatch(s);
        s = Parenthesised.Replace(s, " ");
        s = LifeDates.Replace(s, " ");
        s = Roles.Replace(s, " ");
        s = EtAl.Replace(s, " ");

        // "and" separates people as ";" and "&" do. Only commas make "Last, First" pairs: splitting "Pride and Prejudice"
        // there made it the pair "Pride", "Prejudice", read back as one person, "Prejudice Pride".
        foreach (var chunk in Regex.Split(s, @"[;&]|\s+and\s+"))
        {
            var pieces = new List<string>();
            foreach (var raw0 in chunk.Split(','))
            {
                var p = LeadingBy.Replace(Squash(raw0), "");
                if (p.Length == 0 || Degree.IsMatch(p) || ScanCredit.IsMatch(p)) continue;
                if (Suffix.IsMatch(p) && pieces.Count > 0) { pieces[^1] += " " + p.TrimEnd('.') + (p.EndsWith('.') ? "." : ""); continue; }
                pieces.Add(TrimDot(p));
            }
            if (pieces.Count == 0) continue;

            bool AllMultiWord() => pieces.All(p => p.Contains(' '));
            if (pieces.Count == 1 || AllMultiWord() || pieces.Count % 2 == 1 || wholeNames || !pieces.All(NameLike))
            {
                people.AddRange(pieces);
                continue;
            }
            // Even number of pieces with single words among them: "Last, First" pairs.
            for (var i = 0; i + 1 < pieces.Count; i += 2)
                people.Add($"{pieces[i + 1]} {pieces[i]}");
        }

        return people
            .Select(p => FixCase(TrimDot(p.Trim(' ', ',', '-'))))
            .Select(p => p.EndsWith(" Jr", StringComparison.Ordinal) || Regex.IsMatch(p, @"\b[A-Z]$") ? p + "." : p)
            .Where(p => p.Count(char.IsLetter) >= 2 && !IsNotAPerson(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Display form: the people joined with ", " (the separator EPUB authors use too).</summary>
    public static string Tidy(string? raw) => string.Join(", ", Parse(raw));

    /// <summary>A web address, an e-mail address, or the program that made the file, written where the author goes.</summary>
    public static bool IsNotAPerson(string name)
    {
        name = name.Trim();
        return name.Contains('@') || Site.IsMatch(name) || Program.IsMatch(name);
    }

    /// <summary>
    /// The site an author field names instead of a person ("www.oshoworld.com" gives "oshoworld.com"): where the file
    /// came from, which is kept as its publisher when it has none. Null when no piece of it is a web address.
    /// </summary>
    public static string? SiteIn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var piece in raw.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!piece.Contains('@') && Site.Match(piece) is { Success: true } m) return m.Groups["host"].Value.ToLowerInvariant();
        return null;
    }

    /// <summary>"Thomas." → "Thomas", but an initial keeps its dot: "Sam J.", "A.J.", "G. D.".</summary>
    private static string TrimDot(string p)
    {
        if (!p.EndsWith('.')) return p;
        var last = p[(p.LastIndexOf(' ') + 1)..].TrimEnd('.');
        return last.Length <= 1 || last.Contains('.') ? p : p.TrimEnd('.');
    }

    /// <summary>"KAMALA CHANDRAKANT" and "shravasti dhammika" in the usual case; one word ("DK", "ISECOM") stays.</summary>
    private static string FixCase(string p)
    {
        var letters = p.Where(char.IsLetter).ToList();
        if (!p.Contains(' ') || letters.Count == 0 || letters.Any(c => c > 'ɏ')) return p;
        if (letters.Any(char.IsUpper) && letters.Any(char.IsLower)) return p;
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p.ToLowerInvariant());
    }

    /// <summary>Only a name can be half of a "Last, First" pair: not "[D3$!B3B9]".</summary>
    private static bool NameLike(string p) => p.All(c => char.IsLetter(c) || c is ' ' or '.' or '-' or '\'' or '’');

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
