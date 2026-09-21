namespace AryanEbookLibrary.Models;

/// <summary>What Open Library says about one book (a row of book_online), kept apart from the file's own details.</summary>
public sealed class OnlineDetails
{
    public const string Found = "found", Suggested = "suggested", None = "none", Error = "error";
    public const string ByIsbn = "isbn", ByMatch = "match", ByPick = "picked";

    public string Status { get; set; } = None;
    public string? How { get; set; }
    public string? SourceKey { get; set; }     // Open Library work key, "/works/OL…W"
    public string? Isbn { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Publisher { get; set; }
    public int? Year { get; set; }
    public string? Description { get; set; }
    public string? Subjects { get; set; }
    public long? CoverId { get; set; }         // Open Library's cover, downloaded only when it is used
    public string? CoverFile { get; set; }
    public bool UseCover { get; set; }         // the user chose Open Library's cover over the file's
    public int Tries { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public OnlineDetails Copy() => (OnlineDetails)MemberwiseClone();

    public bool IsApplied => Status == Found;
    public bool IsPicked => Status == Found && How == ByPick;
}
