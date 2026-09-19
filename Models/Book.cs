using AryanEbookLibrary.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AryanEbookLibrary.Models;

public sealed class Book : ObservableObject
{
    // ---- Indexed metadata (from the SQLite index) ----
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string DriveId { get; set; } = "";
    public string DriveLabel { get; set; } = "";
    public string RelPath { get; set; } = "";
    public BookFormat Format { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Series { get; set; } = "";
    public double? SeriesIndex { get; set; }
    public string Publisher { get; set; } = "";
    public int? Year { get; set; }
    public string Language { get; set; } = "";
    public string Description { get; set; } = "";
    public string Isbn { get; set; } = "";
    public string Subjects { get; set; } = "";
    public string? CoverFile { get; set; }
    public long FileSize { get; set; }
    public long ModifiedTicks { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    // ---- Runtime ----
    private bool _isAvailable;
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value)) OnPropertyChanged(nameof(IsOffline));
        }
    }
    public bool IsOffline => !_isAvailable;

    // ---- Personal state ----
    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }

    private ReadStatus _status;
    public ReadStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowProgress));
            }
        }
    }

    private int _rating;
    public int Rating { get => _rating; set => SetProperty(ref _rating, value); }

    private int _progress;
    public int Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ShowProgress));
        }
    }

    private string _notes = "";
    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? ""); }

    private string _userTags = "";
    public string UserTags { get => _userTags; set => SetProperty(ref _userTags, value ?? ""); }

    private DateTime? _lastOpenedUtc;
    public DateTime? LastOpenedUtc { get => _lastOpenedUtc; set => SetProperty(ref _lastOpenedUtc, value); }

    public DateTime? FinishedUtc { get; set; }
    public DateTime StateUpdatedUtc { get; set; } = DateTime.MinValue;

    // ---- Derived ----
    private string? _stateKey;
    public string StateKey => _stateKey ??= MakeKey(DriveId, RelPath);

    public static string MakeKey(string driveId, string relPath) =>
        driveId + "|" + relPath.Replace('/', '\\').ToLowerInvariant();

    public string? FullPath => DriveRegistry.Resolve(DriveId, RelPath);
    public string? CoverPath => string.IsNullOrEmpty(CoverFile) ? null : Path.Combine(AppPaths.Covers, CoverFile);
    public string FormatLabel => FormatHelper.Label(Format);
    public string DisplayAuthor => string.IsNullOrWhiteSpace(Author) ? "Unknown author" : Author;
    public bool ShowProgress => _status == ReadStatus.Reading && _progress > 0;

    public string StatusText => _status switch
    {
        ReadStatus.Reading => "Reading",
        ReadStatus.Finished => "Finished",
        _ => "Unread"
    };

    public string SeriesLine
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Series)) return "";
            return SeriesIndex.HasValue ? $"{Series} #{SeriesIndex.Value:0.##}" : Series;
        }
    }

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Publisher)) parts.Add(Publisher);
            if (Year.HasValue) parts.Add(Year.Value.ToString());
            if (!string.IsNullOrWhiteSpace(Language)) parts.Add(Language.ToUpperInvariant());
            parts.Add(FormatLabel);
            parts.Add(FormatSize(FileSize));
            return string.Join("  ·  ", parts);
        }
    }

    private string? _sortTitle;
    public string SortTitle => _sortTitle ??= ComputeSortTitle(Title);

    private string? _searchBlob;
    public string SearchBlob => _searchBlob ??=
        string.Join('\n', Title, Author, Series, Publisher, Subjects, UserTags, Isbn, Path.GetFileName(RelPath));

    public void ApplyState(BookState s)
    {
        IsFavorite = s.IsFavorite;
        Status = s.Status;
        Rating = s.Rating;
        Progress = s.Progress;
        Notes = s.Notes;
        UserTags = s.UserTags;
        LastOpenedUtc = s.LastOpenedUtc;
        FinishedUtc = s.FinishedUtc;
        StateUpdatedUtc = s.UpdatedUtc;
        _searchBlob = null;
    }

    public BookState ToState() => new()
    {
        IsFavorite = IsFavorite,
        Status = Status,
        Rating = Rating,
        Progress = Progress,
        Notes = Notes,
        UserTags = UserTags,
        LastOpenedUtc = LastOpenedUtc,
        FinishedUtc = FinishedUtc,
        UpdatedUtc = StateUpdatedUtc
    };

    public void ResetSearchBlob() => _searchBlob = null;

    private static string ComputeSortTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();
        foreach (var article in new[] { "the ", "a ", "an " })
            if (t.StartsWith(article, StringComparison.Ordinal)) return t[article.Length..];
        return t;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }
}
