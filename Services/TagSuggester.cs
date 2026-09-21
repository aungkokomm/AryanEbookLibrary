using System.Text.RegularExpressions;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>A tag the library suggests for itself, the books it would go on, and where it came from.</summary>
public sealed class TagSuggestion
{
    public string Tag { get; init; } = "";
    public string Why { get; init; } = "";
    public List<Book> Books { get; init; } = new();
    public int Count => Books.Count;
    public string CountText => Count == 1 ? "1 book" : $"{Count:N0} books";
    public string Sample => string.Join("  ·  ", Books.Take(3).Select(b => b.Title));

    /// <summary>Same tag and same books tomorrow: how a refusal is remembered.</summary>
    public string Key => Tag.ToLowerInvariant();
}

/// <summary>
/// Tags the library can work out by itself: the folder a book sits in, what it is written in, what kind of
/// file it is, the subjects the book declares, and words that keep coming back in the titles. Nothing is
/// written until the user says yes to a suggestion, because tags are their own words for their own shelves.
/// </summary>
public static class TagSuggester
{
    /// <summary>A tag has to fit at least this many books to be worth having.</summary>
    private const int Least = 3;

    public static List<TagSuggestion> Build(IReadOnlyList<Book> books, IEnumerable<string>? refused = null)
    {
        var no = new HashSet<string>(refused ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var found = new List<TagSuggestion>();

        found.AddRange(FromFolders(books));
        found.AddRange(FromScript(books));
        found.AddRange(FromFormat(books));
        found.AddRange(FromSubjects(books));
        found.AddRange(FromTitles(books));

        // The same shelf found twice (a word in the title AND the book's own subject) is one shelf.
        found = found
            .GroupBy(s => s.Tag, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Count() == 1 ? g.First() : new TagSuggestion
            {
                Tag = g.First().Tag,
                Why = string.Join(", and ", g.Select(x => x.Why).Distinct()),
                Books = g.SelectMany(x => x.Books).Distinct().ToList()
            })
            .ToList();

        // A book that already wears the tag needs nothing, and a tag with too few books left is dropped.
        var kept = new List<TagSuggestion>();
        foreach (var s in found)
        {
            if (no.Contains(s.Key)) continue;
            var missing = s.Books.Where(b => !Tags.Has(b, s.Tag)).Distinct().ToList();
            if (missing.Count < Least) continue;
            kept.Add(new TagSuggestion { Tag = s.Tag, Why = s.Why, Books = missing });
        }

        // Two names for the same shelf: keep the one that reads better (the shorter name wins).
        var final = new List<TagSuggestion>();
        foreach (var s in kept.OrderByDescending(s => s.Count))
        {
            var mine = s.Books.Select(b => b.Id).ToHashSet();
            var twin = final.FirstOrDefault(other => Overlap(other.Books.Select(b => b.Id).ToHashSet(), mine) >= 0.9);
            if (twin is null) { final.Add(s); continue; }
            if (s.Tag.Length < twin.Tag.Length)
            {
                final[final.IndexOf(twin)] = s;
            }
        }
        return final.OrderByDescending(s => s.Count).ThenBy(s => s.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static double Overlap(HashSet<long> a, HashSet<long> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Intersect(b).Count();
        return shared / (double)Math.Min(a.Count, b.Count);
    }

    // ---------------------------------------------------------------- folders

    /// <summary>Folders that say nothing about a book: where everything lives, or when it was put there.</summary>
    private static readonly HashSet<string> PlainFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "books", "book", "ebooks", "ebook", "library", "new", "new books", "newbooks", "raw", "misc",
        "downloads", "download", "documents", "pdf", "pdfs", "epub", "epubs", "files", "stuff", "temp"
    };

    private static readonly Regex DateLike = new(@"^[\d\s._-]+$|^(19|20)\d\d$", RegexOptions.Compiled);
    private static readonly Regex SiteBracket = new(@"\[[^\]]*\.(com|net|org|info|me|io|cc)[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingNoise = new(
        @"\b(collection|collections|ebooks?|books?|comics?|pdfs?|true\s+pdf|pootled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Assorted Magazines October 11 2025" is a shelf called "Assorted Magazines".</summary>
    private static readonly Regex DateInName = new(
        @"\b(january|february|march|april|may|june|july|august|september|october|november|december)\b[\s\d,]*|\b(19|20)\d\d\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A count the folder carried, as in "Hacker HighSchool - 13 ebooks".</summary>
    private static readonly Regex TrailingCount = new(@"\s+\d+\s*$", RegexOptions.Compiled);

    private static IEnumerable<TagSuggestion> FromFolders(IReadOnlyList<Book> books)
    {
        var byFolder = new Dictionary<string, List<Book>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in books)
        {
            var parts = b.RelPath.Replace('/', '\\').Split('\\');
            for (var i = 0; i < parts.Length - 1; i++)       // every folder above the file
            {
                var name = Folder(parts[i]);
                if (name.Length == 0) continue;
                if (!byFolder.TryGetValue(name, out var list)) byFolder[name] = list = new List<Book>();
                list.Add(b);
            }
        }

        foreach (var (name, list) in byFolder)
        {
            if (list.Count < Least) continue;
            if (list.Count > books.Count * 0.6) continue;    // a folder holding nearly everything says nothing
            yield return new TagSuggestion
            {
                Tag = name,
                Why = "the folder they are in",
                Books = list
            };
        }
    }

    /// <summary>A folder name as a shelf name: no site tag, no separators, no "collection" at the end.</summary>
    private static string Folder(string raw)
    {
        var name = SiteBracket.Replace(raw, " ");
        name = name.Replace('_', ' ').Replace('-', ' ');
        name = DateInName.Replace(name, " ");
        name = TrailingNoise.Replace(name, " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        name = TrailingCount.Replace(name, "");
        name = name.Trim(" .-_()[]".ToCharArray());
        if (name.Length < 3 || name.Length > 40) return "";
        if (PlainFolders.Contains(name) || DateLike.IsMatch(name)) return "";
        if (!name.Any(char.IsLetter)) return "";
        return name;
    }

    // ---------------------------------------------------------------- what it is written in

    private static IEnumerable<TagSuggestion> FromScript(IReadOnlyList<Book> books)
    {
        var burmese = books.Where(b => Burmese(b)).ToList();
        if (burmese.Count >= Least)
            yield return new TagSuggestion { Tag = "Burmese", Why = "the title is in Burmese", Books = burmese };

        var hindi = books.Where(b => Devanagari(b)).ToList();
        if (hindi.Count >= Least)
            yield return new TagSuggestion { Tag = "Hindi", Why = "the title is in Devanagari", Books = hindi };
    }

    private static bool Burmese(Book b) =>
        b.Title.Any(c => c is >= 'က' and <= '႟') ||
        DuplicateFinder.LangKey(b.Language) == "my";

    private static bool Devanagari(Book b) =>
        b.Title.Any(c => c is >= 'ऀ' and <= 'ॿ') ||
        DuplicateFinder.LangKey(b.Language) == "hi";

    // ---------------------------------------------------------------- what kind of file

    private static IEnumerable<TagSuggestion> FromFormat(IReadOnlyList<Book> books)
    {
        var comics = books.Where(b => b.Format is BookFormat.Cbz or BookFormat.Cbr).ToList();
        if (comics.Count >= Least)
            yield return new TagSuggestion { Tag = "Comics", Why = "they are comic book files", Books = comics };
    }

    // ---------------------------------------------------------------- what the book says about itself

    private static IEnumerable<TagSuggestion> FromSubjects(IReadOnlyList<Book> books)
    {
        var bySubject = new Dictionary<string, List<Book>>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var b in books)
            foreach (var raw in b.Subjects.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var subject = Subject(raw);
                if (subject.Length == 0) continue;
                if (!bySubject.TryGetValue(subject, out var list)) bySubject[subject] = list = new List<Book>();
                list.Add(b);
            }

        foreach (var (subject, list) in bySubject.Where(x => x.Value.Count >= Least))
            yield return new TagSuggestion { Tag = subject, Why = "the books say so themselves", Books = list };
    }

    /// <summary>Subjects that say nothing, because they would fit any book at all.</summary>
    private static readonly HashSet<string> EmptyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "none", "general", "generalities", "misc", "miscellaneous", "other", "others", "unknown",
        "book", "books", "ebook", "ebooks", "text", "texts", "subject", "no subject"
    };

    /// <summary>A subject worth keeping: not a web address, not a whole sentence, not a filler word.</summary>
    private static string Subject(string raw)
    {
        var s = Regex.Replace(raw, @"\s+", " ").Trim(" .-".ToCharArray());
        if (EmptyWords.Contains(s)) return "";
        if (s.Length < 3 || s.Length > 30) return "";
        if (s.Contains("http", StringComparison.OrdinalIgnoreCase) || s.Contains('@') ||
            Regex.IsMatch(s, @"\.(com|net|org|info|me|io|cc)\b", RegexOptions.IgnoreCase)) return "";
        if (s.Count(char.IsWhiteSpace) > 3) return "";
        if (!s.Any(char.IsLetter)) return "";
        return s;
    }

    // ---------------------------------------------------------------- words that keep coming back

    /// <summary>Subjects worth a shelf, and the words in a title that mean it. Deliberately short.</summary>
    private static readonly (string Tag, string[] Words)[] Topics =
    {
        ("Hacking", new[] { "hacking", "hacker", "hackers", "penetration", "pentest", "cybersecurity", "malware", "kali" }),
        ("Programming", new[] { "programming", "python", "javascript", "java", "coding", "algorithms", "developer", "css", "html" }),
        ("Data and AI", new[] { "machine learning", "deep learning", "artificial intelligence", "data science", "neural", "chatgpt", "llm" }),
        ("Mathematics", new[] { "mathematics", "mathematical", "algebra", "calculus", "geometry", "statistics" }),
        ("Science", new[] { "physics", "chemistry", "biology", "quantum", "astronomy", "neuroscience", "evolution" }),
        ("History", new[] { "history", "historical", "civilisation", "civilization", "empire" }),
        ("Business", new[] { "business", "marketing", "startup", "entrepreneur", "investing", "finance", "economics" }),
        ("Health", new[] { "health", "diet", "nutrition", "fitness", "yoga", "medicine", "healing" }),
        ("Spiritual", new[] { "buddhism", "buddhist", "dhamma", "dharma", "meditation", "osho", "vedanta", "spiritual", "zen" }),
        ("Magazines", new[] { "magazine", "magazines" }),
        ("Textbooks", new[] { "textbook", "textbooks", "syllabus" }),
    };

    private static IEnumerable<TagSuggestion> FromTitles(IReadOnlyList<Book> books)
    {
        foreach (var (tag, words) in Topics)
        {
            var list = books.Where(b => Says(b, words)).ToList();
            if (list.Count >= Least)
                yield return new TagSuggestion { Tag = tag, Why = "the words in the title", Books = list };
        }
    }

    private static bool Says(Book b, string[] words)
    {
        var text = (b.Title + " " + b.Series + " " + b.Subjects).ToLowerInvariant();
        foreach (var w in words)
        {
            var at = text.IndexOf(w, StringComparison.Ordinal);
            if (at < 0) continue;
            // a whole word, so "art" does not come from "start"
            var before = at == 0 || !char.IsLetter(text[at - 1]);
            var end = at + w.Length;
            var after = end >= text.Length || !char.IsLetter(text[end]);
            if (before && after) return true;
        }
        return false;
    }
}
