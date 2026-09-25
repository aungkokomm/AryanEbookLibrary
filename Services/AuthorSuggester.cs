using System.Globalization;
using System.Text.RegularExpressions;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>Books that came from one website with no author, kept together in a folder named like a person.</summary>
public sealed record AuthorOffer(string Site, string Name, IReadOnlyList<Book> Books)
{
    /// <summary>What a "No" is remembered by.</summary>
    public string Key => Site + "|" + Name;

    public string Question => $"{Books.Count:N0} books from {Site} have no author and are in the folder “{Name}”.";
    public string Answer => $"Set {Name} as their author";
}

/// <summary>
/// The author a website's books leave out, offered from where they are kept: 34 PDFs whose files say only
/// "www.oshoworld.com" (now their publisher) sit in "Books\Osho\Osho Hindi Book", so Osho is offered. Nothing is
/// written until the user says yes, and a "No" is not asked again.
/// </summary>
public static class AuthorSuggester
{
    private static readonly Regex Domain = new(@"^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}$", RegexOptions.IgnoreCase);

    /// <summary>Folder names that say what is in a folder, not who wrote it.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "books", "book", "ebooks", "ebook", "e-books", "new", "pdf", "pdfs", "epub", "hindi", "english", "burmese", "myanmar",
        "urdu", "marathi", "sanskrit", "tamil", "bengali", "collection", "collections", "library", "download", "downloads",
        "comics", "comic", "magazines", "magazine", "misc", "other", "others", "old", "series", "novels", "novel", "stories",
        "story", "all", "my", "documents", "desktop", "reading", "read", "texts", "archive", "files", "general", "reference",
        "fiction", "nonfiction", "poetry", "science", "history", "religion", "spiritual", "philosophy", "complete", "works",
        "vol", "volume", "part", "and", "the", "of"
    };

    public static List<AuthorOffer> Build(IEnumerable<Book> books, ICollection<string> dismissed)
    {
        var offers = new List<AuthorOffer>();
        var groups = books
            .Where(b => b.Author.Length == 0 && b.CustomAuthor is null && Domain.IsMatch(b.Publisher))
            .GroupBy(b => b.Publisher.ToLowerInvariant());
        foreach (var group in groups)
        {
            var list = group.ToList();
            if (list.Count < 3) continue;
            if (FoldersAbove(list).FirstOrDefault(PersonLike) is not { } name) continue;
            var offer = new AuthorOffer(group.Key, name, list);
            if (!dismissed.Contains(offer.Key)) offers.Add(offer);
        }
        return offers.OrderByDescending(o => o.Books.Count).ToList();
    }

    /// <summary>The folders every one of the books is in, the nearest first.</summary>
    private static IEnumerable<string> FoldersAbove(List<Book> books)
    {
        var paths = books.Select(b => (Path.GetDirectoryName(b.RelPath) ?? "").Split('\\', '/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var shared = 0;
        while (paths.All(p => p.Length > shared) &&
               paths.All(p => string.Equals(p[shared], paths[0][shared], StringComparison.OrdinalIgnoreCase)))
            shared++;
        return paths[0].Take(shared).Reverse();
    }

    /// <summary>"Osho", "Stephen King", "မြသန်းတင့်": one to four words of letters, none of them a word for a kind of book.</summary>
    private static bool PersonLike(string folder)
    {
        var words = folder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 4 || words.Any(Generic.Contains)) return false;
        if (folder.Count(char.IsLetter) < 3) return false;
        foreach (var c in folder)
        {
            var kind = CharUnicodeInfo.GetUnicodeCategory(c);
            if (!(char.IsLetter(c) || c is ' ' or '.' or '\'' or '-' ||
                  kind is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
                return false;
        }
        // A Latin name is written with capitals: "Osho", not "osho-hindi".
        return folder.Any(c => c > 'ɏ') || words.All(w => char.IsUpper(w[0]));
    }
}
