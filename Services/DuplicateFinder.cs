using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AryanEbookLibrary.Services;

/// <summary>One file in a group, and whether it is the copy to keep.</summary>
public sealed class DuplicateCopy
{
    public Book Book { get; init; } = null!;
    public bool IsKeep { get; init; }
}

/// <summary>
/// Which copy to keep when a book is in the library more than once: the language and the format the user
/// prefers, and the copies they picked by hand, which always win. These come from Settings.
/// </summary>
public sealed class KeepRule
{
    public string Language { get; init; } = "";
    public string Format { get; init; } = "";

    /// <summary>Group key to that group's chosen file. Held by the caller, so a choice is remembered.</summary>
    public Dictionary<string, string> Chosen { get; init; } = new();
}

/// <summary>Books that look like the same book, and why.</summary>
public sealed class DuplicateGroup : ObservableObject
{
    public string Title { get; init; } = "";
    public string Reason { get; init; } = "";

    /// <summary>The same book in another format or another language: worth knowing, not a duplicate.</summary>
    public bool SameBookOtherFormat { get; init; }

    public List<Book> Books { get; init; } = new();
    public string CountText => $"{Books.Count} files";

    /// <summary>These files, whichever ones they are: the same group tomorrow has the same key.</summary>
    public string Key => string.Join("|", Books.Select(b => b.StateKey).OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>The copy to keep: the user's own choice, or the rule in Settings.</summary>
    public Book Keep { get; private set; } = null!;

    /// <summary>Why that one, to show under the title.</summary>
    public string KeepLine { get; private set; } = "";

    public List<DuplicateCopy> Copies =>
        Books.Select(b => new DuplicateCopy { Book = b, IsKeep = b == Keep }).ToList();

    public KeepRule Rule { get; init; } = new();

    /// <summary>Applies the rule, unless the user already picked a copy in this group.</summary>
    public void ChooseKeeper()
    {
        var chosen = Rule.Chosen.TryGetValue(Key, out var stateKey)
            ? Books.FirstOrDefault(b => b.StateKey == stateKey)
            : null;
        if (chosen is not null)
        {
            Keep = chosen;
            KeepLine = "You chose this copy.";
            return;
        }
        (Keep, KeepLine) = DuplicateFinder.Keeper(Books, Rule);
    }

    private void Told()
    {
        OnPropertyChanged(nameof(Keep));
        OnPropertyChanged(nameof(KeepLine));
        OnPropertyChanged(nameof(Copies));
    }

    /// <summary>The user picked a copy here. It is remembered, and still nothing is deleted.</summary>
    public void KeepInstead(Book book)
    {
        Rule.Chosen[Key] = book.StateKey;
        ChooseKeeper();
        Told();
    }
}

/// <summary>
/// Finds the same book twice, in Colophon's three layers, surest first: the same ISBN, then the same title
/// and author in the same format, then near-identical titles by the same author. The same book in two
/// FORMATS (an EPUB and a PDF) is not a duplicate, so it is listed apart. Nothing is ever deleted here:
/// the page only shows the files, and the user decides.
/// </summary>
public static class DuplicateFinder
{
    public static List<DuplicateGroup> Find(IEnumerable<Book> books, KeepRule? rule = null)
    {
        rule ??= new KeepRule();
        var all = books.Where(b => b.Title.Length > 0).ToList();
        var groups = new List<DuplicateGroup>();
        var taken = new HashSet<long>();
        // A "title" on many books is a stamp a scanner left or the name of a comic series, not a book's title.
        var shared = all.GroupBy(b => Key(b.Title)).Where(g => g.Count() > 3).Select(g => g.Key).ToHashSet();

        // 1. The same ISBN is the same book, whatever the names say.
        foreach (var g in all.Where(b => Isbn.IsValid(Isbn.Digits(b.Isbn))).GroupBy(b => Isbn.Digits(b.Isbn)))
        {
            var list = g.ToList();
            if (list.Count < 2) continue;
            Add(rule, groups, taken, list, "Same ISBN");
        }

        // 2. The same title and author.
        foreach (var g in all.Where(b => !taken.Contains(b.Id)).GroupBy(b => Key(b.Title) + "|" + Key(b.Author)))
        {
            var list = g.ToList();
            if (list.Count < 2 || Key(list[0].Title).Length < 3) continue;
            foreach (var byFormat in list.GroupBy(b => b.Format))
                foreach (var same in ByLanguage(byFormat.ToList()))
                {
                    if (same.Count < 2) continue;
                    if (same.All(b => b.FileSize == same[0].FileSize))
                    {
                        Add(rule, groups, taken, same, "Same file");   // byte for byte the same size: a copy
                        continue;
                    }
                    if (same[0].Author.Length == 0 && shared.Contains(Key(same[0].Title))) continue;
                    Add(rule, groups, taken, same, "Same title and author");
                }

            // The same book in two languages, or in two formats, is not a duplicate: it is worth knowing
            // which copy you would keep, so it is listed apart.
            var left = list.Where(b => !taken.Contains(b.Id)).ToList();
            var languages = left.GroupBy(b => LangKey(b.Language)).Where(x => x.Key.Length > 0).ToList();
            if (languages.Count >= 2)
            {
                Add(rule, groups, taken, languages.Select(x => x.First()).ToList(),
                    "Same book, different languages", sameBookOtherFormat: true);
                continue;
            }
            var formats = left.GroupBy(b => b.Format).Select(x => x.First()).ToList();
            if (formats.Count >= 2) Add(rule, groups, taken, formats, "Same book, different formats", sameBookOtherFormat: true);
        }

        // 3. Nearly the same title by the same author ("The Art of War" and "Art of War").
        foreach (var bucket in all.Where(b => !taken.Contains(b.Id) && Key(b.Title).Length >= 6 && !shared.Contains(Key(b.Title)))
                     .GroupBy(b => Key(b.Title)[..4]))
        {
            var list = bucket.ToList();
            if (list.Count < 2) continue;
            for (var i = 0; i < list.Count; i++)
            {
                if (taken.Contains(list[i].Id)) continue;
                var like = new List<Book> { list[i] };
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (taken.Contains(list[j].Id)) continue;
                    if (AuthorIndex.Similarity(Key(list[i].Title), Key(list[j].Title)) < 0.9) continue;
                    if (!SpellingApart(list[i].Title, list[j].Title)) continue;   // "Vol 1" / "Vol 2", "UK" / "USA"
                    if (!AuthorsAgree(list[i], list[j])) continue;
                    like.Add(list[j]);
                }
                if (like.Count >= 2) Add(rule, groups, taken, like, "Nearly the same title");
            }
        }

        return groups
            .OrderBy(g => g.SameBookOtherFormat)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Add(KeepRule rule, List<DuplicateGroup> groups, HashSet<long> taken, List<Book> books, string reason,
        bool sameBookOtherFormat = false)
    {
        var group = new DuplicateGroup
        {
            Title = books[0].Title,
            Reason = reason,
            SameBookOtherFormat = sameBookOtherFormat,
            Books = books.OrderByDescending(b => b.FileSize).ToList(),
            Rule = rule
        };
        group.ChooseKeeper();
        groups.Add(group);
        foreach (var b in books) taken.Add(b.Id);
    }

    /// <summary>
    /// Which copy to keep: the language the user prefers first, then the format they prefer, then the
    /// biggest file, which for the same book usually means the fuller scan. The user can pick another one
    /// on the page, and that choice wins from then on.
    /// </summary>
    public static (Book Book, string Line) Keeper(IReadOnlyList<Book> books, KeepRule rule)
    {
        var language = LangKey(rule.Language);
        var format = rule.Format;

        // A copy that says nothing about its language does not argue with one that does, so the language
        // only decides when the copies really are in two languages.
        if (language.Length > 0 && books.Select(b => LangKey(b.Language)).Where(k => k.Length > 0).Distinct().Count() > 1)
        {
            var mine = books.Where(b => LangKey(b.Language) == language).ToList();
            if (mine.Count > 0)
                return (Pick(mine, format), $"Keeping the copy in {LanguageName(language)}, the language you prefer.");
        }
        if (format.Length > 0 && books.Any(FormatIsMine) && !books.All(FormatIsMine))
            return (Pick(books, format), $"Keeping the {format}, the format you prefer.");
        if (books.All(b => b.FileSize == books[0].FileSize))
            return (books[0], "The files are the same size, so either copy will do.");
        return (books.OrderByDescending(b => b.FileSize).First(), "Keeping the biggest file.");

        bool FormatIsMine(Book b) => FormatIs(b, format);
    }

    private static Book Pick(IReadOnlyList<Book> books, string format) =>
        books.OrderByDescending(b => format.Length > 0 && FormatIs(b, format))
            .ThenByDescending(b => b.FileSize)
            .First();

    private static bool FormatIs(Book b, string format) =>
        string.Equals(b.FormatLabel, format, StringComparison.OrdinalIgnoreCase);

    /// <summary>The languages worth naming here, and the codes books carry them under.</summary>
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["hin"] = "hi", ["mya"] = "my", ["bur"] = "my", ["mar"] = "mr", ["san"] = "sa",
        ["ben"] = "bn", ["tam"] = "ta", ["fre"] = "fr", ["fra"] = "fr", ["ger"] = "de", ["deu"] = "de",
        ["spa"] = "es", ["rus"] = "ru", ["jpn"] = "ja", ["zho"] = "zh", ["chi"] = "zh"
    };

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["hi"] = "Hindi", ["my"] = "Myanmar", ["mr"] = "Marathi", ["sa"] = "Sanskrit",
        ["bn"] = "Bengali", ["ta"] = "Tamil", ["fr"] = "French", ["de"] = "German", ["es"] = "Spanish",
        ["ru"] = "Russian", ["ja"] = "Japanese", ["zh"] = "Chinese"
    };

    /// <summary>"en-US", "EN" and "eng" are all English. An empty or unknown language says nothing.</summary>
    public static string LangKey(string language)
    {
        var s = language.Trim().ToLowerInvariant();
        if (s.Length == 0 || s == "und") return "";
        var cut = s.IndexOfAny(new[] { '-', '_' });
        if (cut > 0) s = s[..cut];
        return Languages.TryGetValue(s, out var two) ? two : s;
    }

    public static string LanguageName(string key) =>
        LanguageNames.TryGetValue(key, out var name) ? name : key.ToUpperInvariant();

    /// <summary>
    /// The same book written in two languages is two books. Files that do not say their language stay with
    /// the rest, since most books say nothing at all.
    /// </summary>
    private static IEnumerable<List<Book>> ByLanguage(List<Book> books)
    {
        var known = books.Select(b => LangKey(b.Language)).Where(k => k.Length > 0).Distinct().Count();
        if (known < 2) return new[] { books };
        return books.Where(b => LangKey(b.Language).Length > 0)
            .GroupBy(b => LangKey(b.Language))
            .Select(g => g.ToList());
    }

    /// <summary>
    /// The two titles are the same words, give or take how a word is spelled ("Chekov" and "Chekhov").
    /// Words that are really different mean different books: "Vol 2" and "Vol 3", "December 2025 UK" and
    /// "December 2025 USA".
    /// </summary>
    private static bool SpellingApart(string a, string b)
    {
        var mine = Online.OnlineMatcher.Tokens(a);
        var theirs = Online.OnlineMatcher.Tokens(b);
        var onlyMine = mine.Except(theirs).ToList();
        var onlyTheirs = theirs.Except(mine).ToList();
        if (onlyMine.Count == 0 && onlyTheirs.Count == 0) return true;
        if (onlyMine.Count != 1 || onlyTheirs.Count != 1) return false;
        if (onlyMine[0].Any(char.IsDigit) || onlyTheirs[0].Any(char.IsDigit)) return false;
        return AuthorIndex.Similarity(onlyMine[0], onlyTheirs[0]) >= 0.85;
    }

    /// <summary>One book's author names the other's, or one of them has no author at all.</summary>
    private static bool AuthorsAgree(Book a, Book b)
    {
        if (a.Author.Length == 0 || b.Author.Length == 0) return true;
        var mine = AuthorIndex.Split(a.Author).Select(AuthorIndex.Surname).Where(s => s.Length >= 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AuthorIndex.Split(b.Author).Select(AuthorIndex.Surname).Any(s => s.Length >= 3 && mine.Contains(s));
    }

    private static string Key(string s) => AuthorIndex.Key(s);
}
