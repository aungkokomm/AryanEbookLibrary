namespace AryanEbookLibrary.Models;

/// <summary>
/// Personal reading state. Stored in the SQLite index, in a sidecar file on each library folder
/// (so it travels with the drive) and in the JSON backup. Same idea as CineLibrary's personal state.
/// </summary>
public sealed class BookState
{
    public bool IsFavorite { get; set; }
    public ReadStatus Status { get; set; }
    public int Rating { get; set; }            // 0 = not rated, 1..5
    public int Progress { get; set; }          // 0..100 (manual, the external reader owns the real position)
    public string Notes { get; set; } = "";
    public string UserTags { get; set; } = "";
    public DateTime? LastOpenedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // The user's own title/author/series, shown instead of what the file says. Null = use the file's value
    // (so clearing an edit brings the file's value back); "" for author/series = deliberately none.
    // Kept with the personal state so a rescan never overwrites them and they travel with the drive.
    public string? CustomTitle { get; set; }
    public string? CustomAuthor { get; set; }
    public string? CustomSeries { get; set; }

    public bool IsDefault =>
        !IsFavorite && Status == ReadStatus.Unread && Rating == 0 && Progress == 0 &&
        string.IsNullOrEmpty(Notes) && string.IsNullOrEmpty(UserTags) &&
        LastOpenedUtc is null && FinishedUtc is null &&
        CustomTitle is null && CustomAuthor is null && CustomSeries is null;
}
