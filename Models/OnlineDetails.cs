namespace AryanEbookLibrary.Models;

/// <summary>Where online details came from, and how that reads on screen.</summary>
public static class OnlineSource
{
    public const string OpenLibrary = "openlibrary", Wikidata = "wikidata", Wikipedia = "wikipedia";

    public static string Name(string? source) => source switch
    {
        OpenLibrary => "Open Library",
        Wikidata => "Wikidata",
        Wikipedia => "Wikipedia",
        _ => "online"
    };
}

/// <summary>What one source says about one book (a row of book_online), kept apart from the file's own details.</summary>
public sealed class OnlineDetails
{
    public const string Found = "found", Suggested = "suggested", None = "none", Error = "error";
    public const string ByIsbn = "isbn", ByMatch = "match", ByPick = "picked";

    public string Source { get; set; } = OnlineSource.OpenLibrary;
    public string Status { get; set; } = None;
    public string? How { get; set; }
    public string? SourceKey { get; set; }     // Open Library work key, Wikidata item id, Wikipedia page
    public string? Isbn { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Publisher { get; set; }
    public int? Year { get; set; }
    public string? Series { get; set; }
    public double? SeriesIndex { get; set; }
    public string? Description { get; set; }
    public string? Subjects { get; set; }
    public string? PageTitle { get; set; }     // the Wikipedia article about this book, when there is one
    public long? CoverId { get; set; }         // Open Library's cover, downloaded only when it is used
    public string? CoverUrl { get; set; }      // Wikipedia's picture
    public string? CoverFile { get; set; }
    public bool UseCover { get; set; }         // the user chose this source's cover over the book's
    public int Tries { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public OnlineDetails Copy() => (OnlineDetails)MemberwiseClone();

    public bool IsApplied => Status == Found;
    public bool IsPicked => Status == Found && How == ByPick;
}
