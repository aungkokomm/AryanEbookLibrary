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
    private static readonly Regex Suffix = new(@"^(Jr|Sr|II|III|IV|LLC|Inc|Ltd)\.?$");   // belongs to the name before it

    public static List<string> Parse(string? raw)
    {
        var people = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return people;

        var s = Parenthesised.Replace(raw, " ");
        s = LifeDates.Replace(s, " ");
        s = Roles.Replace(s, " ");

        foreach (var chunk in s.Split(new[] { ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = new List<string>();
            foreach (var raw0 in Regex.Split(chunk, @",|\s+and\s+"))
            {
                var p = Squash(raw0);
                if (p.Length == 0 || Degree.IsMatch(p)) continue;
                if (Suffix.IsMatch(p) && pieces.Count > 0) { pieces[^1] += " " + p.TrimEnd('.') + (p.EndsWith('.') ? "." : ""); continue; }
                pieces.Add(TrimDot(p));
            }
            if (pieces.Count == 0) continue;

            bool AllMultiWord() => pieces.All(p => p.Contains(' '));
            if (pieces.Count == 1 || AllMultiWord() || pieces.Count % 2 == 1)
            {
                people.AddRange(pieces);
                continue;
            }
            // Even number of pieces with single words among them: "Last, First" pairs.
            for (var i = 0; i + 1 < pieces.Count; i += 2)
                people.Add($"{pieces[i + 1]} {pieces[i]}");
        }

        return people
            .Select(p => TrimDot(p.Trim(' ', ',', '-')))
            .Select(p => p.EndsWith(" Jr", StringComparison.Ordinal) || Regex.IsMatch(p, @"\b[A-Z]$") ? p + "." : p)
            .Where(p => p.Count(char.IsLetter) >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Display form: the people joined with ", " (the separator EPUB authors use too).</summary>
    public static string Tidy(string? raw) => string.Join(", ", Parse(raw));

    /// <summary>"Thomas." → "Thomas", but an initial keeps its dot: "Sam J.", "A.J.", "G. D.".</summary>
    private static string TrimDot(string p)
    {
        if (!p.EndsWith('.')) return p;
        var last = p[(p.LastIndexOf(' ') + 1)..].TrimEnd('.');
        return last.Length <= 1 || last.Contains('.') ? p : p.TrimEnd('.');
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
