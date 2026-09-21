using System.Globalization;
using System.Text;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>One person, with the books of theirs in the library.</summary>
public sealed class AuthorEntry
{
    public string Name { get; init; } = "";
    public List<Book> Books { get; init; } = new();
    public string? Qid { get; set; }                 // Wikidata says this person exists and writes
    public int BookCount => Books.Count;
    public string CountText => BookCount == 1 ? "1 book" : $"{BookCount:N0} books";
    public string? CoverPath => Books.FirstOrDefault(b => b.CoverPath is not null)?.CoverPath;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool HasQid => Qid is not null;
}

/// <summary>Names that look like the same person, with the one to keep.</summary>
public sealed class AuthorVariants
{
    public string Canonical { get; init; } = "";
    public List<AuthorEntry> Members { get; init; } = new();
    public string Others => string.Join(", ", Members.Where(m => m.Name != Canonical).Select(m => m.Name));
    public int BookCount => Members.Sum(m => m.BookCount);
    public string BookCountText => BookCount == 1 ? "1 book between them" : $"{BookCount:N0} books between them";
    public string Key => string.Join("|", Members.Select(m => AuthorIndex.Key(m.Name)).OrderBy(k => k, StringComparer.Ordinal));
}

/// <summary>
/// The library's authors, one entry per person, and the names that are probably the same person written
/// differently ("S. Dhammika" and "Shravasti Dhammika"). Colophon's three ideas, in a smaller form: a
/// normalized key catches spelling noise, the surname plus an initial catches shortened first names, and
/// Wikidata's id (when it is known) says the person is real.
/// </summary>
public static class AuthorIndex
{
    public static List<AuthorEntry> Build(IEnumerable<Book> books, IReadOnlyDictionary<string, LibraryRepository.AuthorOnline>? known = null)
    {
        var people = new Dictionary<string, AuthorEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in books)
        {
            foreach (var name in Split(b.Author))
            {
                if (!people.TryGetValue(name, out var entry))
                    people[name] = entry = new AuthorEntry { Name = name };
                entry.Books.Add(b);
            }
        }

        if (known is not null)
            foreach (var entry in people.Values)
                if (known.TryGetValue(entry.Name.ToLowerInvariant(), out var row) && row.Status == OnlineDetails.Found)
                    entry.Qid = row.Qid;

        return people.Values
            .OrderBy(a => LooksLikeName(a.Name) ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>"[D3$!B3B9]" and "www.oshoworld.com" are marks a file carried, not people: they go last.</summary>
    private static bool LooksLikeName(string name) =>
        name.Length > 0 && char.IsLetter(name[0]) && !name.Contains('@') &&
        !name.Contains("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The people named in one author field ("Chris Baker, Pasuk Phongpaichit").</summary>
    public static IEnumerable<string> Split(string author) =>
        author.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Count(char.IsLetter) >= 2 || n.Any(c => c is >= 'က' and <= '႟'));

    /// <summary>Groups of names that are probably one person. The name on most books is the one to keep.</summary>
    public static List<AuthorVariants> Similar(List<AuthorEntry> authors)
    {
        var groups = new List<List<AuthorEntry>>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Names sharing a surname are the only ones worth comparing, which keeps this a quick pass.
        foreach (var bucket in authors.Where(IsPersonName).GroupBy(a => Surname(a.Name), StringComparer.OrdinalIgnoreCase))
        {
            if (bucket.Key.Length < 3) continue;
            var list = bucket.ToList();
            if (list.Count < 2) continue;
            foreach (var a in list)
            {
                if (taken.Contains(a.Name)) continue;
                var same = list.Where(b => !taken.Contains(b.Name) && b != a && IsSamePerson(a.Name, b.Name)).ToList();
                if (same.Count == 0) continue;
                same.Add(a);
                foreach (var m in same) taken.Add(m.Name);
                groups.Add(same);
            }
        }

        return groups
            .Select(g => new AuthorVariants
            {
                Members = g.OrderByDescending(m => m.BookCount).ThenByDescending(m => m.Name.Length).ToList(),
                // the fullest name that is on the most books
                Canonical = g.OrderByDescending(m => m.Qid is not null).ThenByDescending(m => m.BookCount)
                             .ThenByDescending(m => m.Name.Length).First().Name
            })
            .OrderByDescending(g => g.BookCount)
            .ToList();
    }

    /// <summary>
    /// A website, an e-mail or a Myanmar name: not something to compare by surname. Burmese names are a
    /// string of given names with no family name, so a shared last syllable means nothing there.
    /// </summary>
    private static bool IsPersonName(AuthorEntry a)
    {
        var name = a.Name;
        if (name.Any(c => c is >= 'က' and <= '႟')) return false;
        if (name.Contains('@') || name.Contains("www", StringComparison.OrdinalIgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\.(com|net|org|info|ru|cc)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        return name.Any(char.IsLetter) && !name.Any(char.IsDigit);
    }

    /// <summary>Titles that are not part of a name: "Dr. Varsha Patil" and "Dr. Aboli Patil" are two people.</summary>
    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr", "prof", "professor", "mr", "mrs", "ms", "sir", "sri", "shri", "swami", "rev", "fr", "st",
        "maung", "mg", "ko", "daw", "u", "saya", "sayadaw", "thakin"
    };

    /// <summary>
    /// Same surname, and either the same first name, one first name an initial of the other, or names that
    /// are the same but for spelling noise ("Chekov" / "Chekhov").
    /// </summary>
    public static bool IsSamePerson(string a, string b)
    {
        if (Key(a) == Key(b)) return true;
        var first = Words(a).Where(w => !Honorifics.Contains(w)).ToList();
        var second = Words(b).Where(w => !Honorifics.Contains(w)).ToList();
        if (first.Count == 0 || second.Count == 0) return false;
        if (!string.Equals(first[^1], second[^1], StringComparison.OrdinalIgnoreCase)) return false;   // different surnames
        // "Dhammika" alone is the same person as "S. Dhammika", but a short word ("Kyaw", "Frost") is
        // too common to say that of.
        if (first.Count == 1 || second.Count == 1) return first[^1].Length >= 6;

        var x = first[0];
        var y = second[0];
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        // An initial stands for a first name only when the rest of the name lines up too: "S. Dhammika" is
        // "Shravasti Dhammika", but "S. Balachandra Rao" is not "Subba Rao".
        if (x.Length == 1 || y.Length == 1)
            return first.Count == second.Count && char.ToLowerInvariant(x[0]) == char.ToLowerInvariant(y[0]);
        return Similarity(Key(a), Key(b)) >= 0.87;
    }

    /// <summary>How alike two names are, 0 to 1 (Levenshtein, as Colophon uses a ratio for the same job).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                                      previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1.0 - previous[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    public static string Surname(string name) => Words(name) is { Count: > 0 } w ? w[^1] : "";

    /// <summary>Lower case, no accents, no dots or hyphens: "A. C. Bhaktivedanta" and "AC Bhaktivedanta" match.</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static List<string> Words(string name) =>
        name.Split(new[] { ' ', '.', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Any(char.IsLetter)).ToList();
}
