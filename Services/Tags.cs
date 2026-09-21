using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>One of the user's own tags, and the books wearing it.</summary>
public sealed class TagEntry
{
    public string Name { get; init; } = "";
    public List<Book> Books { get; init; } = new();
    public int Count => Books.Count;
    public string CountText => Count == 1 ? "1 book" : $"{Count:N0} books";
    public string? CoverPath => Books.FirstOrDefault(b => b.CoverPath is not null)?.CoverPath;
}

/// <summary>
/// The user's tags. A book keeps them in one comma-separated field, which is how they are typed in the
/// book's details, so everything here is just reading that field the same way everywhere.
/// </summary>
public static class Tags
{
    public static IEnumerable<string> Split(string? field) =>
        (field ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Every tag in the library, most used first, then alphabetically.</summary>
    public static List<TagEntry> Build(IEnumerable<Book> books)
    {
        var tags = new Dictionary<string, TagEntry>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var book in books)
            foreach (var name in Split(book.UserTags))
            {
                if (!tags.TryGetValue(name, out var entry))
                    tags[name] = entry = new TagEntry { Name = name };
                entry.Books.Add(book);
            }

        return tags.Values
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The tags already there, plus the new ones, each once, in the order they were added.</summary>
    public static string Merge(string current, string added)
    {
        var tags = Split(current).ToList();
        foreach (var tag in Split(added))
            if (!tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
                tags.Add(tag);
        return string.Join(", ", tags);
    }

    /// <summary>True when the book wears this tag (whole tag, not part of a longer one).</summary>
    public static bool Has(Book book, string tag) =>
        Split(book.UserTags).Any(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>The field without that tag.</summary>
    public static string Remove(string field, string tag) =>
        string.Join(", ", Split(field).Where(t => !string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)));
}
