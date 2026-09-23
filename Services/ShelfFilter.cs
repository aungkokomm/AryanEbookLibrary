using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>One choice in a filter list, and how many books it would show.</summary>
public sealed record FacetOption(string Key, string Label, int Count)
{
    public override string ToString() => $"{Label} ({Count:N0})";
}

/// <summary>A filter from the Filters menu, left out when working out that filter's own choices.</summary>
public enum Facet { None, Language, Decade, Publisher, Rating }

/// <summary>
/// Which books a view shows, and what each filter can offer in it. The choices are counted with every other
/// filter applied, so a choice never promises books the view cannot show. Nothing here touches the UI, so
/// the harness checks it against a copy of the real library.
/// </summary>
public static class ShelfFilter
{
    public static readonly BookFormat[][] FormatGroups =
    {
        Array.Empty<BookFormat>(),
        new[] { BookFormat.Epub },
        new[] { BookFormat.Pdf },
        new[] { BookFormat.Mobi, BookFormat.Azw3 },
        new[] { BookFormat.Cbz, BookFormat.Cbr }
    };

    private static readonly string[] FormatNames = { "", "EPUB", "PDF", "Kindle", "Comics" };

    public const string NotStated = "-";

    public static IEnumerable<Book> Apply(IEnumerable<Book> books, Shelf v, Facet except = Facet.None)
    {
        var q = books;

        if (v.Format > 0 && v.Format < FormatGroups.Length)
        {
            var formats = FormatGroups[v.Format];
            q = q.Where(b => formats.Contains(b.Format));
        }

        q = v.List switch
        {
            LibraryFilter.ContinueReading => q.Where(b => b.Status == ReadStatus.Reading),
            LibraryFilter.Favorites => q.Where(b => b.IsFavorite),
            LibraryFilter.Unread => q.Where(b => b.Status == ReadStatus.Unread),
            LibraryFilter.Finished => q.Where(b => b.Status == ReadStatus.Finished),
            LibraryFilter.NeedsDetails => q.Where(b => b.NeedsDetails),
            _ => q
        };

        if (v.Series.Length > 0)
            q = q.Where(b => string.Equals(b.Series, v.Series, StringComparison.CurrentCultureIgnoreCase));
        if (v.Tag.Length > 0)
            q = q.Where(b => Tags.Has(b, v.Tag));

        if (except != Facet.Language && v.Language.Length > 0)
            q = q.Where(b => LanguageKey(b) == v.Language);
        if (except != Facet.Decade && v.Decade.Length > 0)
            q = q.Where(b => DecadeKey(b) == v.Decade);
        if (except != Facet.Publisher && v.Publisher.Length > 0)
            q = q.Where(b => string.Equals(b.Publisher.Trim(), v.Publisher, StringComparison.CurrentCultureIgnoreCase));
        if (except != Facet.Rating && v.MinRating > 0)
            q = q.Where(b => b.Rating >= v.MinRating);
        if (v.ConnectedOnly)
            q = q.Where(b => b.IsAvailable);

        foreach (var token in v.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = token;
            q = q.Where(b => b.SearchBlob.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        return q;
    }

    // ---- language ----

    /// <summary>
    /// A title in Myanmar or Devanagari letters settles the language, whatever the file says: 274 Burmese books
    /// in this library say "en", the default of the tools they were made with. Marathi, Sanskrit and Nepali are
    /// written in Devanagari too, so a Devanagari title keeps one of those when the file says so. Otherwise the
    /// file's own language, and a title in Latin letters with none stays "not stated": in this library that is
    /// as often a Hindi comic ("Nagraj Ki Kabra") or a Burmese name in English letters as an English book.
    /// </summary>
    public static string LanguageKey(Book b)
    {
        var declared = DuplicateFinder.LangKey(b.Language);
        if (b.Title.Any(c => c is >= (char)0x1000 and <= (char)0x109F)) return "my";
        if (b.Title.Any(c => c is >= (char)0x0900 and <= (char)0x097F)) return declared is "mr" or "sa" or "ne" ? declared : "hi";
        return declared.Length > 0 ? declared : NotStated;
    }

    public static string LanguageName(string key) => key == NotStated ? "Not stated" : DuplicateFinder.LanguageName(key);

    public static List<FacetOption> Languages(IEnumerable<Book> books, Shelf v)
    {
        var pool = Apply(books, v, Facet.Language).ToList();
        var options = pool.GroupBy(LanguageKey)
            .Select(g => new FacetOption(g.Key, LanguageName(g.Key), g.Count()))
            .OrderBy(o => o.Key == NotStated).ThenByDescending(o => o.Count).ThenBy(o => o.Label)
            .ToList();
        return WithAny(options, "Any language", pool.Count, v.Language, LanguageName);
    }

    // ---- year ----

    /// <summary>Decades, since only some books know their year and one year is too fine to browse by.</summary>
    public static string DecadeKey(Book b) => b.Year switch
    {
        null => "unknown",
        < 1950 => "old",
        int y => (y / 10 * 10).ToString()
    };

    public static string DecadeName(string key) => key switch
    {
        "unknown" => "Year not known",
        "old" => "Before 1950",
        _ => key + "s"
    };

    public static List<FacetOption> Decades(IEnumerable<Book> books, Shelf v)
    {
        var pool = Apply(books, v, Facet.Decade).ToList();
        var options = pool.GroupBy(DecadeKey)
            .Select(g => new FacetOption(g.Key, DecadeName(g.Key), g.Count()))
            .OrderBy(o => o.Key == "unknown").ThenBy(o => o.Key == "old").ThenByDescending(o => o.Key)
            .ToList();
        return WithAny(options, "Any year", pool.Count, v.Decade, DecadeName);
    }

    // ---- publisher ----

    public static List<FacetOption> Publishers(IEnumerable<Book> books, Shelf v)
    {
        var pool = Apply(books, v, Facet.Publisher).ToList();
        // "Whisper Of Words" and "Whisper of Words" are one publisher, shown the way most books spell it.
        var options = pool.Where(b => b.Publisher.Trim().Length > 0)
            .GroupBy(b => b.Publisher.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var name = g.GroupBy(b => b.Publisher.Trim()).OrderByDescending(x => x.Count()).First().Key;
                return new FacetOption(name, name, g.Count());
            })
            .OrderByDescending(o => o.Count).ThenBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return WithAny(options, "Any publisher", pool.Count, v.Publisher, p => p);
    }

    // ---- rating ----

    public static string RatingName(int stars) => stars >= 5 ? "5 stars" : stars == 1 ? "Rated" : $"{stars} stars and up";

    public static List<FacetOption> Ratings(IEnumerable<Book> books, Shelf v)
    {
        var pool = Apply(books, v, Facet.Rating).ToList();
        var options = new List<FacetOption> { new("0", "Any rating", pool.Count) };
        for (var stars = 5; stars >= 1; stars--)
        {
            var n = stars;
            options.Add(new FacetOption(n.ToString(), RatingName(n), pool.Count(b => b.Rating >= n)));
        }
        return options;
    }

    // ---- describing a view ----

    public static string ListName(LibraryFilter list) => list switch
    {
        LibraryFilter.ContinueReading => "Continue Reading",
        LibraryFilter.RecentlyAdded => "Recently Added",
        LibraryFilter.Favorites => "Favorites",
        LibraryFilter.Unread => "Unread",
        LibraryFilter.Finished => "Finished",
        LibraryFilter.NeedsDetails => "Needs Details",
        _ => "All Books"
    };

    /// <summary>The filters that live in the Filters menu, in words ("Myanmar", "2010s", "On connected drives").</summary>
    public static List<string> MenuFilters(Shelf v)
    {
        var parts = new List<string>();
        if (v.Language.Length > 0) parts.Add(LanguageName(v.Language));
        if (v.Decade.Length > 0) parts.Add(DecadeName(v.Decade));
        if (v.Publisher.Length > 0) parts.Add(v.Publisher);
        if (v.MinRating > 0) parts.Add(RatingName(v.MinRating));
        if (v.ConnectedOnly) parts.Add("On connected drives");
        return parts;
    }

    /// <summary>A name for a new shelf that says what is on it ("Unread · EPUB · Myanmar").</summary>
    public static string SuggestName(Shelf v)
    {
        var parts = new List<string>();
        if (v.List is not (LibraryFilter.All or LibraryFilter.RecentlyAdded)) parts.Add(ListName(v.List));
        if (v.Series.Length > 0) parts.Add(v.Series);
        if (v.Tag.Length > 0) parts.Add(v.Tag);
        if (v.Format > 0 && v.Format < FormatNames.Length) parts.Add(FormatNames[v.Format]);
        parts.AddRange(MenuFilters(v));
        if (v.Search.Trim().Length > 0) parts.Add("“" + v.Search.Trim() + "”");
        return parts.Count == 0 ? "My shelf" : string.Join(" · ", parts);
    }

    /// <summary>Anything narrower than the plain list: worth saving as a shelf, and worth a "Clear filters".</summary>
    public static bool IsFiltered(Shelf v) =>
        v.Format > 0 || v.Search.Trim().Length > 0 || v.Series.Length > 0 || v.Tag.Length > 0 || MenuFilters(v).Count > 0;

    /// <summary>"Any" first, and the chosen value kept even when nothing in this view has it any more.</summary>
    private static List<FacetOption> WithAny(List<FacetOption> options, string any, int total, string chosen, Func<string, string> name)
    {
        if (chosen.Length > 0 && !options.Any(o => string.Equals(o.Key, chosen, StringComparison.CurrentCultureIgnoreCase)))
            options.Add(new FacetOption(chosen, name(chosen), 0));
        options.Insert(0, new FacetOption("", any, total));
        return options;
    }
}
