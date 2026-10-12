using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// What the search box asks for. Plain words are found anywhere in a book's details, as before; a word with a field
/// in front looks only there, and a minus in front leaves those books out:
/// <c>author:tolstoy tag:history year:1990-2000 format:pdf rating:4+ is:unread -tag:lent "war and peace"</c>.
/// A value with spaces goes in quotes (<c>author:"leo tolstoy"</c>). A word whose field Aryan does not know is
/// searched for as it is typed, so a title with a colon in it is still found.
/// </summary>
public sealed class SearchQuery
{
    private readonly List<Func<Book, bool>> _terms = new();

    public bool IsEmpty => _terms.Count == 0;

    public bool Matches(Book book)
    {
        foreach (var term in _terms)
            if (!term(book)) return false;
        return true;
    }

    public static SearchQuery Parse(string? text)
    {
        var query = new SearchQuery();
        foreach (var raw in Tokens(text ?? ""))
        {
            var token = raw;
            var not = token.Length > 1 && token[0] == '-';
            if (not) token = token[1..];
            var term = Term(token);
            if (term is null) continue;
            query._terms.Add(not ? b => !term(b) : term);
        }
        return query;
    }

    /// <summary>Words split at spaces, except inside double quotes, which are taken off.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var word = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (word.Length > 0) yield return word.ToString();
                word.Clear();
                continue;
            }
            word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private static Func<Book, bool>? Term(string token)
    {
        var colon = token.IndexOf(':');
        if (colon > 0)
        {
            var field = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..].Trim();
            if (value.Length == 0) return null;   // "author:" typed, the name not yet
            var term = Field(field, value);
            if (term is not null) return term;
        }
        return b => Has(b.SearchBlob, token);
    }

    private static Func<Book, bool>? Field(string field, string value) => field switch
    {
        "title" => b => Has(b.Title, value),
        "author" or "by" => b => Has(b.Author, value),
        "series" => b => Has(b.Series, value),
        "publisher" or "pub" => b => Has(b.Publisher, value),
        "tag" => b => Tags.Split(b.UserTags).Any(t => Has(t, value)),
        "list" => b => b.Lists.Any(l => string.Equals(l, value, StringComparison.CurrentCultureIgnoreCase)),
        "isbn" => b => Has(b.Isbn.Replace("-", ""), value.Replace("-", "")),
        "format" or "type" => Format(value),
        "lang" or "language" => b => Language(b, value),
        "year" => Number(value) is { } year ? b => b.Year is { } y && year(y) : null,
        "rating" or "stars" => Number(value) is { } stars ? b => stars(b.Rating) : null,
        "is" => Is(value),
        _ => null
    };

    private static bool Has(string? field, string value) =>
        (field ?? "").Contains(value, StringComparison.CurrentCultureIgnoreCase);

    private static Func<Book, bool>? Format(string value)
    {
        BookFormat[] formats = value.ToLowerInvariant() switch
        {
            "epub" => [BookFormat.Epub],
            "pdf" => [BookFormat.Pdf],
            "mobi" => [BookFormat.Mobi],
            "azw3" or "azw" => [BookFormat.Azw3],
            "kfx" => [BookFormat.Kfx],
            "kindle" => [BookFormat.Mobi, BookFormat.Azw3, BookFormat.Kfx],
            "cbz" => [BookFormat.Cbz],
            "cbr" => [BookFormat.Cbr],
            "comic" or "comics" => [BookFormat.Cbz, BookFormat.Cbr],
            _ => []
        };
        return formats.Length == 0 ? null : b => formats.Contains(b.Format);
    }

    /// <summary>A language by its code ("my", "hi") or the start of its name ("burm", "hindi").</summary>
    private static bool Language(Book b, string value)
    {
        var key = ShelfFilter.LanguageKey(b);
        return string.Equals(key, value, StringComparison.OrdinalIgnoreCase)
               || key != ShelfFilter.NotStated && ShelfFilter.LanguageName(key).StartsWith(value, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>"4" exactly, "4+" or "&gt;=4" and up, "4-" or "&lt;=4" and down, "&gt;4", "&lt;4", or "1990-2000" between.</summary>
    private static Func<int, bool>? Number(string value)
    {
        static bool Int(string s, out int n) => int.TryParse(s.Trim(), out n);
        if (value.StartsWith(">=") && Int(value[2..], out var ge)) return n => n >= ge;
        if (value.StartsWith("<=") && Int(value[2..], out var le)) return n => n <= le;
        if (value.StartsWith('>') && Int(value[1..], out var gt)) return n => n > gt;
        if (value.StartsWith('<') && Int(value[1..], out var lt)) return n => n < lt;
        if (value.EndsWith('+') && Int(value[..^1], out var up)) return n => n >= up;
        if (value.EndsWith('-') && Int(value[..^1], out var down)) return n => n <= down;
        var dash = value.IndexOf('-', 1);
        if (dash > 0 && Int(value[..dash], out var from) && Int(value[(dash + 1)..], out var to))
            return n => n >= Math.Min(from, to) && n <= Math.Max(from, to);
        if (Int(value, out var exact)) return n => n == exact;
        return null;
    }

    private static Func<Book, bool>? Is(string value) => value.ToLowerInvariant() switch
    {
        "unread" => b => b.Status == ReadStatus.Unread,
        "reading" => b => b.Status == ReadStatus.Reading,
        "finished" or "read" or "done" => b => b.Status == ReadStatus.Finished,
        "favorite" or "favourite" or "fav" => b => b.IsFavorite,
        "rated" => b => b.Rating > 0,
        "unrated" => b => b.Rating == 0,
        "offline" => b => b.IsOffline,
        "online" or "connected" => b => b.IsAvailable,
        "note" or "notes" => b => b.HasNote,
        "series" => b => b.Series.Trim().Length > 0,
        _ => null
    };
}
