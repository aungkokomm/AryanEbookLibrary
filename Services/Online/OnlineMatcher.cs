using System.Globalization;
using System.Text;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Online;

/// <summary>The details of a book that a lookup goes by (a copy, so the lookup can run off the UI thread).</summary>
public sealed record LookupBook(string Key, string Title, string Author, bool AuthorFromName, string FileName, string? Isbn,
    string Series = "");

/// <summary>
/// Decides whether an Open Library result is this book. Automatic fills need certainty, as Calibre and
/// Audiobookshelf rank it: the ISBN printed in the book with a title that agrees, or a search result whose
/// title and author both agree. A result that is only likely becomes a suggestion the user accepts or rejects.
/// </summary>
public static class OnlineMatcher
{
    public sealed record Decision(string Status, string? How, OnlineCandidate? Candidate);

    /// <summary>Looks the book up and decides. Throws <see cref="OnlineUnavailableException"/> when the catalogue does not answer.</summary>
    public static async Task<Decision> LookupAsync(IBookCatalogue client, LookupBook b, CancellationToken ct)
    {
        OnlineCandidate? byIsbn = null;
        if (!string.IsNullOrEmpty(b.Isbn))
        {
            byIsbn = await client.ByIsbnAsync(b.Isbn, ct);
            if (byIsbn is not null && IsbnTitleAgrees(b, byIsbn) && (b.Author.Length == 0 || AuthorAgrees(b.Author, byIsbn.Authors)))
                return new(OnlineDetails.Found, OnlineDetails.ByIsbn, byIsbn);
        }

        if (!CanSearch(b))
            return byIsbn is not null ? new(OnlineDetails.Suggested, OnlineDetails.ByIsbn, byIsbn) : new(OnlineDetails.None, null, null);

        var title = MainTitleOf(b);
        var surname = Surname(b.Author);
        var results = await client.SearchAsync(title, surname, 8, ct);
        return Decide(b, byIsbn, results);
    }

    public static Decision Decide(LookupBook b, OnlineCandidate? byIsbn, IReadOnlyList<OnlineCandidate> results)
    {
        // Of several agreeing records, the one with the most editions is the work itself; the others are often
        // a single translated edition ("Homo Deus" in Spanish).
        var agreeing = results.Where(c => TitleAgrees(b, c) && AuthorAgrees(b.Author, c.Authors)).ToList();
        if (agreeing.Where(c => !SubtitleUnconfirmed(b, c)).MaxBy(c => c.EditionCount) is { } sure)
            return new(OnlineDetails.Found, OnlineDetails.ByMatch, sure);

        if (byIsbn is not null) return new(OnlineDetails.Suggested, OnlineDetails.ByIsbn, byIsbn);
        if (agreeing.MaxBy(c => c.EditionCount) is { } likely) return new(OnlineDetails.Suggested, OnlineDetails.ByMatch, likely);

        // A book with no author whose title agrees: likely, not certain. A book whose author disagrees
        // (even one read from the file name) is a different book with the same title. A book in a series
        // is only offered a record that names the series: "Stories of Courage" is not Amar Chitra Katha's.
        var titleOnly = string.IsNullOrWhiteSpace(b.Author)
            ? results.Where(c => TitleAgrees(b, c) && NamesSeries(b, c)).MaxBy(c => c.EditionCount)
            : null;
        if (titleOnly is not null) return new(OnlineDetails.Suggested, OnlineDetails.ByMatch, titleOnly);

        return new(OnlineDetails.None, null, null);
    }

    /// <summary>
    /// A short main title says little ("Software Architecture"), so when the book has a subtitle that Open
    /// Library's record does not share ("visual lecture notes"), the record may be another book by the author.
    /// </summary>
    public static bool SubtitleUnconfirmed(LookupBook b, OnlineCandidate c) =>
        SubtitleUnconfirmed(b, c.Title + " " + c.Subtitle);

    public static bool SubtitleUnconfirmed(LookupBook b, string otherFullTitle)
    {
        var main = Tokens(MainTitleOf(b));
        var sub = Tokens(b.Title);
        sub.ExceptWith(main);
        if (main.Count > 2 || sub.Count < 2) return false;
        var theirs = Tokens(otherFullTitle);
        return sub.Count(theirs.Contains) * 2 < sub.Count;
    }

    private static bool NamesSeries(LookupBook b, OnlineCandidate c)
    {
        var series = Tokens(b.Series).Where(t => t.Length >= 3).ToList();
        if (series.Count == 0) return true;
        var said = Tokens(c.Title + " " + c.Subtitle + " " + c.Publisher);
        return series.Any(said.Contains);
    }

    /// <summary>
    /// The same book by title alone, used for a work in an author's catalogue (the author is already known
    /// to be theirs): the book's main title is in the work's title, and the work's main title is in the
    /// book's title or file name.
    /// </summary>
    public static bool WorkTitleAgrees(LookupBook b, string workTitle)
    {
        var bookMain = Tokens(MainTitleOf(b));
        var workMain = Tokens(MainTitle(workTitle));
        if (bookMain.Count == 0 || workMain.Count == 0) return false;
        var have = Tokens(b.Title + " " + b.FileName);
        return bookMain.IsSubsetOf(Tokens(workTitle)) && workMain.IsSubsetOf(have) && !SubtitleUnconfirmed(b, workTitle);
    }

    /// <summary>
    /// The agreeing work that looks most like this book, since an author writes several books with the same
    /// first word: "Sapiens: A Graphic History" must not take "Sapiens: A Brief History of Humankind".
    /// </summary>
    public static double TitleOverlap(LookupBook b, string otherTitle)
    {
        var mine = Tokens(b.Title);
        var theirs = Tokens(otherTitle);
        if (mine.Count == 0 || theirs.Count == 0) return 0;
        var shared = mine.Count(theirs.Contains);
        return shared / (double)(mine.Count + theirs.Count - shared);
    }

    /// <summary>The same words, whatever the case and punctuation: "The art of SQL" and "The Art of SQL".</summary>
    public static bool SameWords(string a, string b) => Tokens(a).SetEquals(Tokens(b));

    /// <summary>Worth an automatic search: a Latin-script title with a real word, and more than one word unless the author is known.</summary>
    public static bool CanSearch(LookupBook b)
    {
        if (b.Title.Any(c => c is >= 'က' and <= '႟')) return false;   // Myanmar: Open Library has little
        if (IssueDate.IsMatch(b.Title)) return false;                     // a magazine issue: "Sea Angler - November 2025"
        var words = Tokens(MainTitleOf(b));
        if (!words.Any(w => w.Count(char.IsLetter) >= 3)) return false;
        return words.Count >= 2 || !string.IsNullOrWhiteSpace(b.Author);
    }

    /// <summary>
    /// Every word of the book's main title is in Open Library's title (or subtitle), and every word of Open
    /// Library's main title is in the book's title or file name. "World War II Map by Map" is not "World War II".
    /// </summary>
    public static bool TitleAgrees(LookupBook b, OnlineCandidate c)
    {
        var bookMain = Tokens(MainTitleOf(b));
        var olMain = Tokens(MainTitle(c.Title));
        if (bookMain.Count == 0 || olMain.Count == 0) return false;
        var olFull = Tokens(c.Title + " " + c.Subtitle);
        var have = Tokens(b.Title + " " + b.FileName);
        return bookMain.IsSubsetOf(olFull) && olMain.IsSubsetOf(have);
    }

    /// <summary>
    /// An ISBN found in a PDF can belong to another book (an advert at the back), so two thirds of its title must
    /// be in the book's name, unless the name is the ISBN itself ("isbn_0671818325").
    /// </summary>
    public static bool IsbnTitleAgrees(LookupBook b, OnlineCandidate c)
    {
        if (b.Isbn is not null && HasNoTitleWords(b.Title) && (b.Title + b.FileName).Contains(b.Isbn, StringComparison.OrdinalIgnoreCase))
            return true;
        var olMain = Tokens(MainTitle(c.Title));
        if (olMain.Count == 0) return false;
        var have = Tokens(b.Title + " " + b.FileName);
        return olMain.Count(have.Contains) * 3 >= olMain.Count * 2;
    }

    /// <summary>No real word in it: "isbn 0671818325", "-285363926", "00078".</summary>
    public static bool HasNoTitleWords(string title) =>
        !Tokens(title).Any(t => t != "isbn" && t.Count(char.IsLetter) >= 3);

    /// <summary>A surname of one of Open Library's authors is a word of the book's author.</summary>
    public static bool AuthorAgrees(string bookAuthor, IReadOnlyList<string> olAuthors)
    {
        if (string.IsNullOrWhiteSpace(bookAuthor)) return false;
        var mine = Tokens(bookAuthor).Where(t => t.Length >= 3).ToHashSet();
        return olAuthors.Any(a => WordList(a).LastOrDefault() is { Length: >= 3 } last && mine.Contains(last));
    }

    private static readonly System.Text.RegularExpressions.Regex IssueDate = new(
        @"\b(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sep(t(ember)?)?|oct(ober)?|nov(ember)?|dec(ember)?)\b\.?\s+(\d{1,2},?\s+)?(19|20)\d\d",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The book's main title, skipping a series name written first:
    /// "Amar Chitra Katha - 122 - Ancestors Of Rama (Ajnaabi)" → "Ancestors Of Rama".
    /// </summary>
    public static string MainTitleOf(LookupBook b)
    {
        var main = MainTitle(b.Title);
        var series = Tokens(b.Series);
        if (series.Count == 0 || !Tokens(main).IsSubsetOf(series) || main.Length >= b.Title.Length) return main;
        var rest = b.Title[main.Length..].TrimStart(' ', '-', '–', '—', ':', ';', '|');
        rest = System.Text.RegularExpressions.Regex.Replace(rest, @"^\d+\s*[-–—:]?\s*", "");   // the number in the series
        return Tokens(rest).Count > 0 ? MainTitle(rest) : main;
    }

    /// <summary>The title before its subtitle: "Sapiens: A Brief History" → "Sapiens".</summary>
    public static string MainTitle(string title)
    {
        var cut = title.Length;
        foreach (var sep in new[] { ":", " - ", " – ", " — ", "(", "[", ";", "|" })
        {
            var i = title.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0 && i < cut) cut = i;
        }
        var main = title[..cut].Trim();
        return Tokens(main).Count > 0 ? main : title.Trim();
    }

    /// <summary>The first author's surname, which Open Library's author search matches best.</summary>
    public static string? Surname(string author)
    {
        var first = author.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var last = first is null ? null : WordList(first).LastOrDefault();
        return last is { Length: >= 2 } ? last : null;
    }

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "to", "in", "on", "for", "with", "by", "from", "at", "or", "its",
        "edition", "revised", "updated", "expanded", "unabridged", "illustrated", "annotated", "ebook", "pdf", "epub",
        // edition markers, not part of a title: "Sapiens [Tenth Anniversary Edition]"
        "anniversary", "reprint", "deluxe", "paperback", "hardcover", "kindle", "tenth", "twentieth", "fiftieth"
    };

    /// <summary>Lower-case words without accents, articles, edition words or ordinals ("2nd").</summary>
    public static HashSet<string> Tokens(string? s) =>
        WordList(s).Where(w => !Ignored.Contains(w) && !IsOrdinal(w) && (w.Length > 1 || char.IsDigit(w[0]))).ToHashSet();

    private static List<string> WordList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is '\'' or '’') continue;   // "don't" → "dont" on both sides
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool IsOrdinal(string w) =>
        w.Length >= 3 && char.IsDigit(w[0]) && (w.EndsWith("st") || w.EndsWith("nd") || w.EndsWith("rd") || w.EndsWith("th")) &&
        w[..^2].All(char.IsDigit);
}
