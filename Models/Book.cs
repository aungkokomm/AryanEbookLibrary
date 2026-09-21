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

    // Shown values: the user's edit when there is one, otherwise what the file says (File*).
    private string _title = "";
    public string Title
    {
        get => _title;
        set
        {
            if (!SetProperty(ref _title, value ?? "")) return;
            _sortTitle = null;
            _searchBlob = null;
        }
    }

    private string _author = "";
    public string Author
    {
        get => _author;
        set
        {
            if (!SetProperty(ref _author, value ?? "")) return;
            OnPropertyChanged(nameof(DisplayAuthor));
            _searchBlob = null;
        }
    }

    private string _series = "";
    public string Series
    {
        get => _series;
        set
        {
            if (!SetProperty(ref _series, value ?? "")) return;
            OnPropertyChanged(nameof(SeriesLine));
            _searchBlob = null;
        }
    }

    // What the file says (its metadata, else its name), what Open Library says, and the user's own edit.
    public string FileTitle { get; set; } = "";
    public string FileAuthor { get; set; } = "";
    public string FileSeries { get; set; } = "";
    public string FilePublisher { get; set; } = "";
    public int? FileYear { get; set; }
    public string FileDescription { get; set; } = "";
    public string FileSubjects { get; set; } = "";
    public string? FileCoverFile { get; set; }
    public OnlineDetails? Online { get; private set; }
    public string? CustomTitle { get; private set; }
    public string? CustomAuthor { get; private set; }
    public string? CustomSeries { get; private set; }

    /// <summary>What would show without the user's edit: the file's details, or Open Library's where they apply.</summary>
    public string BaseTitle { get; private set; } = "";
    public string BaseAuthor { get; private set; } = "";

    /// <summary>The loaded details are the file's own; call once after loading, before any edit or online details.</summary>
    public void KeepFileDetails()
    {
        FileTitle = Title;
        FileAuthor = Author;
        FileSeries = Series;
        FilePublisher = Publisher;
        FileYear = Year;
        FileDescription = Description;
        FileSubjects = Subjects;
        FileCoverFile = CoverFile;
        BaseTitle = Title;
        BaseAuthor = Author;
    }

    /// <summary>Sets (or clears, with null) the user's own title/author/series and shows the result.</summary>
    public void SetCustomDetails(string? title, string? author, string? series)
    {
        CustomTitle = title;
        CustomAuthor = author;
        CustomSeries = series;
        ShowDetails();
    }

    /// <summary>Sets (or clears) what Open Library says and shows the result.</summary>
    public void SetOnline(OnlineDetails? online)
    {
        Online = online;
        ShowDetails();
        OnPropertyChanged(nameof(Online));
    }

    /// <summary>
    /// The user's edit wins, then Open Library details the user picked, then the file's own details. Details
    /// Open Library found by itself only fill what the file lacks: an empty field, an author read from the file
    /// name, a title that is no title ("isbn 0671818325"), a missing cover or a page of text as the cover.
    /// </summary>
    private void ShowDetails()
    {
        var o = Online is { IsApplied: true } ? Online : null;
        var picked = o?.IsPicked == true;
        bool Use(string? online, bool fileIsWeak) => o is not null && !string.IsNullOrWhiteSpace(online) && (picked || fileIsWeak);

        var nameTitle = (NameFields & BookMetadata.NameField.Title) != 0;
        var nameAuthor = (NameFields & BookMetadata.NameField.Author) != 0;
        // A title read from the file name gives way when it is no title at all, or when the book's own ISBN
        // found the record and the name is mangled ("Design Thinking Diana Lakatos Crafting Docs for Success An End to").
        var weakTitle = nameTitle && (Services.Online.OnlineMatcher.HasNoTitleWords(FileTitle) ||
                                      o?.How == OnlineDetails.ByIsbn && !Services.Online.OnlineMatcher.SameWords(FileTitle, o.Title ?? ""));
        BaseTitle = Use(o?.Title, weakTitle) ? o!.Title! : FileTitle;
        BaseAuthor = Use(o?.Author, FileAuthor.Length == 0 || nameAuthor) ? o!.Author! : FileAuthor;

        Title = CustomTitle ?? BaseTitle;
        Author = CustomAuthor ?? BaseAuthor;
        Series = CustomSeries ?? FileSeries;
        Publisher = Use(o?.Publisher, FilePublisher.Length == 0) ? o!.Publisher! : FilePublisher;
        Year = o?.Year is { } y && (picked || FileYear is null) ? y : FileYear;
        Description = Use(o?.Description, FileDescription.Length == 0) ? o!.Description! : FileDescription;
        Subjects = Use(o?.Subjects, FileSubjects.Length == 0) ? o!.Subjects! : FileSubjects;
        // A picked record's cover shows only if the user ticked "Use its cover"; a found one fills a missing cover.
        CoverFile = o is not null && !string.IsNullOrEmpty(o.CoverFile) && (picked ? o.UseCover : o.UseCover || NeedsCover)
            ? o.CoverFile : FileCoverFile;
        _searchBlob = null;
    }

    /// <summary>The book's cover is missing or is a page of text, so another one is better.</summary>
    public bool NeedsCover => FileCoverFile is null || CoverWeak;

    public double? SeriesIndex { get; set; }

    private string _publisher = "";
    public string Publisher
    {
        get => _publisher;
        set { if (SetProperty(ref _publisher, value ?? "")) OnPropertyChanged(nameof(InfoLine)); }
    }

    private int? _year;
    public int? Year
    {
        get => _year;
        set { if (SetProperty(ref _year, value)) OnPropertyChanged(nameof(InfoLine)); }
    }

    public string Language { get; set; } = "";

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value ?? ""); }

    public string Isbn { get; set; } = "";

    private string _subjects = "";
    public string Subjects { get => _subjects; set => SetProperty(ref _subjects, value ?? ""); }

    private string? _coverFile;
    public string? CoverFile
    {
        get => _coverFile;
        set { if (SetProperty(ref _coverFile, value)) OnPropertyChanged(nameof(CoverPath)); }
    }

    public long FileSize { get; set; }
    public long ModifiedTicks { get; set; }
    public int MetaVersion { get; set; }
    public int NameFields { get; set; }     // BookMetadata.NameField bits: details taken from the file name
    public bool CoverWeak { get; set; }     // the "cover" is a page of text
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
        SetCustomDetails(s.CustomTitle, s.CustomAuthor, s.CustomSeries);
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
        UpdatedUtc = StateUpdatedUtc,
        CustomTitle = CustomTitle,
        CustomAuthor = CustomAuthor,
        CustomSeries = CustomSeries
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
