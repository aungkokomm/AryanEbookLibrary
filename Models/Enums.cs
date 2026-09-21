namespace AryanEbookLibrary.Models;

public enum BookFormat
{
    Unknown = 0,
    Epub = 1,
    Pdf = 2,
    Mobi = 3,
    Azw3 = 4,
    Cbz = 5,
    Cbr = 6
}

public enum ReadStatus
{
    Unread = 0,
    Reading = 1,
    Finished = 2
}

public enum LibraryFilter
{
    All,
    ContinueReading,
    RecentlyAdded,
    Favorites,
    Unread,
    Finished,
    NeedsDetails
}

public enum SortMode
{
    Title = 0,
    Author = 1,
    Series = 2,
    DateAdded = 3,
    LastOpened = 4,
    Rating = 5
}

public enum ViewMode
{
    Grid,
    List
}

public static class FormatHelper
{
    private static readonly Dictionary<string, BookFormat> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".epub"] = BookFormat.Epub,
        [".pdf"] = BookFormat.Pdf,
        [".mobi"] = BookFormat.Mobi,
        [".prc"] = BookFormat.Mobi,
        [".azw"] = BookFormat.Mobi,
        [".azw3"] = BookFormat.Azw3,
        [".cbz"] = BookFormat.Cbz,
        [".cbr"] = BookFormat.Cbr,
    };

    public static BookFormat FromPath(string path) =>
        Map.TryGetValue(Path.GetExtension(path), out var f) ? f : BookFormat.Unknown;

    public static bool IsSupported(string path) => Map.ContainsKey(Path.GetExtension(path));

    public static string Label(BookFormat f) => f switch
    {
        BookFormat.Epub => "EPUB",
        BookFormat.Pdf => "PDF",
        BookFormat.Mobi => "MOBI",
        BookFormat.Azw3 => "AZW3",
        BookFormat.Cbz => "CBZ",
        BookFormat.Cbr => "CBR",
        _ => "?"
    };
}
