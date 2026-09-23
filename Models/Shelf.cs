namespace AryanEbookLibrary.Models;

/// <summary>
/// A view of the library: which list, and every filter on it. The library page shows the current one, and a
/// saved one with a name is a shelf in the navigation pane that fills itself as books come and go.
/// </summary>
public sealed class Shelf
{
    public string Name { get; set; } = "";
    public LibraryFilter List { get; set; } = LibraryFilter.All;
    public int Format { get; set; }                  // 0 all, 1 EPUB, 2 PDF, 3 Kindle, 4 comics
    public string Language { get; set; } = "";       // a language key, "-" for not stated, "" for any
    public string Decade { get; set; } = "";         // "2010", "old", "unknown", "" for any
    public string Publisher { get; set; } = "";      // "" for any
    public int MinRating { get; set; }               // 0 for any, else at least this many stars
    public bool ConnectedOnly { get; set; }
    public string Tag { get; set; } = "";
    public string Series { get; set; } = "";
    public string Search { get; set; } = "";

    public Shelf Copy(string? name = null)
    {
        var copy = (Shelf)MemberwiseClone();
        if (name is not null) copy.Name = name;
        return copy;
    }

    /// <summary>The same books, whatever the two views are called.</summary>
    public bool SameRules(Shelf other) =>
        List == other.List && Format == other.Format && Language == other.Language && Decade == other.Decade &&
        string.Equals(Publisher, other.Publisher, StringComparison.CurrentCultureIgnoreCase) &&
        MinRating == other.MinRating && ConnectedOnly == other.ConnectedOnly &&
        string.Equals(Tag, other.Tag, StringComparison.CurrentCultureIgnoreCase) &&
        string.Equals(Series, other.Series, StringComparison.CurrentCultureIgnoreCase) &&
        string.Equals(Search.Trim(), other.Search.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
