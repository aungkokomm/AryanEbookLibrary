using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>One series, its books in order, and the volumes that are not here.</summary>
public sealed class SeriesEntry
{
    public string Name { get; init; } = "";
    public List<Book> Books { get; init; } = new();
    public int Count => Books.Count;
    public string CountText => Count == 1 ? "1 book" : $"{Count:N0} books";
    public string? CoverPath => Books.FirstOrDefault(b => b.CoverPath is not null)?.CoverPath;

    /// <summary>The people who wrote it, as the books name them.</summary>
    public string Authors { get; init; } = "";

    /// <summary>"1 to 7" when the books are numbered, empty when they are not.</summary>
    public string NumbersText { get; init; } = "";

    /// <summary>"Missing 3, 4" between the lowest and highest number you have.</summary>
    public string GapsText { get; init; } = "";

    public bool HasGaps => GapsText.Length > 0;
}

/// <summary>
/// The library's series: what belongs together, in reading order, with the holes shown. Series names come
/// from the books themselves, from Calibre, or from Wikidata (which also gives the volume number).
/// </summary>
public static class SeriesIndex
{
    public static List<SeriesEntry> Build(IEnumerable<Book> books)
    {
        var groups = books
            .Where(b => !string.IsNullOrWhiteSpace(b.Series))
            .GroupBy(b => b.Series.Trim(), StringComparer.CurrentCultureIgnoreCase);

        var list = new List<SeriesEntry>();
        foreach (var g in groups)
        {
            var ordered = g.OrderBy(b => b.SeriesIndex ?? double.MaxValue)
                .ThenBy(b => b.SortTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var numbers = ordered.Where(b => b.SeriesIndex.HasValue).Select(b => b.SeriesIndex!.Value).ToList();
            list.Add(new SeriesEntry
            {
                Name = g.Key,
                Books = ordered,
                Authors = People(ordered),
                NumbersText = Span(numbers),
                GapsText = Gaps(numbers)
            });
        }

        return list
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The two people named on most of the books, which is enough to recognise a series.</summary>
    private static string People(List<Book> books)
    {
        var names = books
            .SelectMany(b => AuthorIndex.Split(b.Author))
            .Where(AuthorIndex.LooksLikeName)
            .GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(2)
            .ToList();
        return string.Join(", ", names);
    }

    private static string Span(List<double> numbers) =>
        numbers.Count < 2 ? "" : $"{Text(numbers.Min())} to {Text(numbers.Max())}";

    /// <summary>
    /// Whole numbers missing between the first and the last you have. Half numbers (2.5) are novellas and
    /// are never called missing. Numbers that are a publisher's catalogue numbers rather than volumes
    /// (Amar Chitra Katha runs from 11 to 10029) say nothing about what is missing, so a run that is
    /// mostly holes is left alone.
    /// </summary>
    private static string Gaps(List<double> numbers)
    {
        var whole = numbers.Where(n => Math.Abs(n % 1) < 0.001).Select(n => (int)n).ToHashSet();
        if (whole.Count < 3) return "";

        var span = whole.Max() - whole.Min() + 1;
        if (span > 200 || whole.Count * 2 < span) return "";   // catalogue numbers, not a series in order

        var missing = Enumerable.Range(whole.Min(), span).Where(n => !whole.Contains(n)).ToList();
        if (missing.Count == 0) return "";
        return missing.Count <= 8
            ? "Missing " + string.Join(", ", missing)
            : $"Missing {missing.Count} of {span} volumes";
    }

    private static string Text(double n) => n.ToString("0.##");
}
