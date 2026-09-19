using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace AryanEbookLibrary.ViewModels;

/// <summary>
/// Single shared view model for the catalog: holds every indexed book, applies the current
/// filter / search / sort, runs scans and watches for drives being plugged in or removed.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly DispatcherQueueTimer _driveTimer;
    private readonly Random _random = new();
    private CancellationTokenSource? _scanCts;
    private List<Book> _all = new();
    private bool _driveCheckRunning;

    private static LibraryRepository Repo => AppServices.Repo;
    private static AppSettings Settings => AppServices.Settings;

    public LibraryViewModel()
    {
        _searchTimer = _dispatcher.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ApplyFilter();

        _driveTimer = _dispatcher.CreateTimer();
        _driveTimer.Interval = TimeSpan.FromSeconds(3);
        _driveTimer.IsRepeating = true;
        _driveTimer.Tick += async (_, _) => await CheckDrivesAsync();

        _sortIndex = (int)Settings.SortMode;
        _sortDescending = Settings.SortDescending;
        _viewMode = Settings.ViewMode;
    }

    /// <summary>Raised when a drive is connected or disconnected (Drives page refreshes itself).</summary>
    public event EventHandler? DriveStatusChanged;

    // ------------------------------------------------------------ bindable state

    private IReadOnlyList<Book> _books = Array.Empty<Book>();
    public IReadOnlyList<Book> Books
    {
        get => _books;
        private set => SetProperty(ref _books, value);
    }

    private LibraryFilter _filter = LibraryFilter.All;
    public LibraryFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                OnPropertyChanged(nameof(HeaderText));
                ApplyFilter();
            }
        }
    }

    public string HeaderText => Filter switch
    {
        LibraryFilter.ContinueReading => "Continue Reading",
        LibraryFilter.RecentlyAdded => "Recently Added",
        LibraryFilter.Favorites => "Favorites",
        LibraryFilter.Unread => "Unread",
        LibraryFilter.Finished => "Finished",
        _ => "All Books"
    };

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    }

    private int _formatIndex;   // 0 All, 1 EPUB, 2 PDF, 3 Kindle, 4 Comics
    public int FormatIndex
    {
        get => _formatIndex;
        set
        {
            if (SetProperty(ref _formatIndex, value)) ApplyFilter();
        }
    }

    private int _sortIndex;
    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (SetProperty(ref _sortIndex, Math.Max(0, value)))
            {
                Settings.SortMode = (SortMode)_sortIndex;
                ApplyFilter();
            }
        }
    }

    private bool _sortDescending;
    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (SetProperty(ref _sortDescending, value))
            {
                Settings.SortDescending = value;
                OnPropertyChanged(nameof(SortDirectionGlyph));
                ApplyFilter();
            }
        }
    }

    public string SortDirectionGlyph => SortDescending ? "\uE74B" : "\uE74A";   // down / up arrow

    private ViewMode _viewMode;
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                Settings.ViewMode = value;
                OnPropertyChanged(nameof(IsGridView));
                OnPropertyChanged(nameof(IsListView));
            }
        }
    }

    public bool IsGridView => ViewMode == ViewMode.Grid;
    public bool IsListView => ViewMode == ViewMode.List;

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    private int _scanDone;
    public int ScanDone { get => _scanDone; private set => SetProperty(ref _scanDone, value); }

    private int _scanTotal;
    public int ScanTotal { get => _scanTotal; private set => SetProperty(ref _scanTotal, value); }

    private string _statusText = "Ready";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private int _totalCount;
    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }

    private string _countText = "";
    public string CountText { get => _countText; private set => SetProperty(ref _countText, value); }

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }

    private string _emptyMessage = "";
    public string EmptyMessage { get => _emptyMessage; private set => SetProperty(ref _emptyMessage, value); }

    // ------------------------------------------------------------ lifecycle

    public async void Initialize()
    {
        try
        {
            await Task.Run(() => DriveRegistry.Refresh(Repo.GetDrives()));
            await ReloadAsync();
            _driveTimer.Start();

            if (Settings.AutoScanOnStart && Repo.GetFolders().Count > 0)
                await ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Initialize failed: " + ex);
            StatusText = "Could not load the library: " + ex.Message;
        }
    }

    public async Task ReloadAsync()
    {
        var list = await Task.Run(() => Repo.LoadAll());
        foreach (var b in list) b.IsAvailable = DriveRegistry.IsOnline(b.DriveId);
        _all = list;
        TotalCount = list.Count;
        ApplyFilter();
    }

    private async Task CheckDrivesAsync()
    {
        if (_driveCheckRunning || IsScanning) return;
        _driveCheckRunning = true;
        try
        {
            var changed = await Task.Run(() => DriveRegistry.Refresh(Repo.GetDrives()));
            if (!changed) return;

            foreach (var b in _all)
                b.IsAvailable = DriveRegistry.IsOnline(b.DriveId);

            // A drive that just came back may hold a newer state sidecar, and its books may have changed.
            DriveStatusChanged?.Invoke(this, EventArgs.Empty);
            StatusText = "Drive change detected";
        }
        catch (Exception ex)
        {
            Log.Write("Drive check failed: " + ex.Message);
        }
        finally
        {
            _driveCheckRunning = false;
        }
    }

    // ------------------------------------------------------------ filter / sort

    private static readonly BookFormat[][] FormatGroups =
    {
        Array.Empty<BookFormat>(),
        new[] { BookFormat.Epub },
        new[] { BookFormat.Pdf },
        new[] { BookFormat.Mobi, BookFormat.Azw3 },
        new[] { BookFormat.Cbz, BookFormat.Cbr }
    };

    public void ApplyFilter()
    {
        IEnumerable<Book> q = _all;

        if (FormatIndex > 0 && FormatIndex < FormatGroups.Length)
        {
            var formats = FormatGroups[FormatIndex];
            q = q.Where(b => formats.Contains(b.Format));
        }

        q = Filter switch
        {
            LibraryFilter.ContinueReading => q.Where(b => b.Status == ReadStatus.Reading),
            LibraryFilter.Favorites => q.Where(b => b.IsFavorite),
            LibraryFilter.Unread => q.Where(b => b.Status == ReadStatus.Unread),
            LibraryFilter.Finished => q.Where(b => b.Status == ReadStatus.Finished),
            _ => q
        };

        var tokens = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var t = token;
            q = q.Where(b => b.SearchBlob.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<Book> sorted;
        if (Filter == LibraryFilter.ContinueReading)
            sorted = q.OrderByDescending(b => b.LastOpenedUtc ?? DateTime.MinValue);
        else if (Filter == LibraryFilter.RecentlyAdded)
            sorted = q.OrderByDescending(b => b.AddedUtc).Take(200);
        else
            sorted = Sort(q);

        var result = sorted.ToList();
        Books = result;

        IsEmpty = result.Count == 0;
        EmptyMessage = _all.Count == 0
            ? "Your library is empty. Open Drives & Folders and add a folder with books."
            : "No books match the current filter.";
        CountText = result.Count == _all.Count || Filter == LibraryFilter.RecentlyAdded
            ? $"{result.Count:N0} books"
            : $"{result.Count:N0} of {_all.Count:N0} books";

        if (!IsScanning) StatusText = $"{_all.Count:N0} books in catalog  ·  {_all.Count(b => b.IsAvailable):N0} available";
    }

    private IEnumerable<Book> Sort(IEnumerable<Book> q)
    {
        var mode = (SortMode)SortIndex;
        var desc = SortDescending;

        IOrderedEnumerable<Book> Order<TKey>(Func<Book, TKey> key, IComparer<TKey>? cmp = null) =>
            desc ? q.OrderByDescending(key, cmp) : q.OrderBy(key, cmp);

        var titleCmp = StringComparer.CurrentCultureIgnoreCase;

        return mode switch
        {
            SortMode.Author => Order(b => b.Author, titleCmp).ThenBy(b => b.SortTitle, titleCmp),
            SortMode.Series => Order(b => b.Series, titleCmp).ThenBy(b => b.SeriesIndex ?? 0).ThenBy(b => b.SortTitle, titleCmp),
            SortMode.DateAdded => Order(b => b.AddedUtc),
            SortMode.LastOpened => Order(b => b.LastOpenedUtc ?? DateTime.MinValue),
            SortMode.Rating => Order(b => b.Rating).ThenBy(b => b.SortTitle, titleCmp),
            _ => Order(b => b.SortTitle, titleCmp)
        };
    }

    // ------------------------------------------------------------ folders + scanning

    public async Task<string> AddFolderAsync(string path)
    {
        var identity = DriveRegistry.Identify(path);
        if (identity is null) return "Could not identify the drive for that folder.";

        var (id, root, label) = identity.Value;
        Repo.UpsertDrive(id, label, root);
        await Task.Run(() => DriveRegistry.Refresh(Repo.GetDrives()));

        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(root, full);
        if (rel == ".") rel = "";

        var folder = Repo.AddFolder(id, rel);
        await ScanAsync(new[] { folder });
        DriveStatusChanged?.Invoke(this, EventArgs.Empty);
        return "";
    }

    public void RemoveFolder(long folderId)
    {
        Repo.RemoveFolder(folderId);
        _ = ReloadAsync();
        DriveStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenameDrive(string driveId, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        Repo.RenameDrive(driveId, label);
        foreach (var b in _all.Where(b => b.DriveId == driveId)) b.DriveLabel = label.Trim();
    }

    public async Task ScanAsync(IReadOnlyList<LibraryFolder>? folders = null)
    {
        if (IsScanning) return;

        folders ??= Repo.GetFolders();
        if (folders.Count == 0)
        {
            StatusText = "No folders to scan. Add one from Drives & Folders.";
            return;
        }

        IsScanning = true;
        ScanDone = 0;
        ScanTotal = 0;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanTotal = p.Total;   // total first so Done never exceeds the progress bar maximum
            ScanDone = p.Done;
            StatusText = p.Message;
        });

        var total = new ScanResult();
        try
        {
            foreach (var folder in folders)
            {
                // Pick up favorites/notes stored on the drive itself before indexing.
                await Task.Run(() => AppServices.Sync.Import(folder), ct);
                total.Add(await AppServices.Scanner.ScanAsync(new[] { folder }, progress, ct));
                await ReloadAsync();
            }

            StatusText = $"Scan complete: {total.Added} new, {total.Updated} updated, {total.Removed} removed" +
                         (total.Failed > 0 ? $", {total.Failed} failed" : "") +
                         (total.OfflineFolders > 0 ? $"  ·  {total.OfflineFolders} folder(s) offline, kept in catalog" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Scan failed: " + ex);
            StatusText = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            ScanDone = 0;
            ScanTotal = 0;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    public void CancelScan() => _scanCts?.Cancel();

    // ------------------------------------------------------------ personal state

    public void SaveState(Book book)
    {
        book.StateUpdatedUtc = DateTime.UtcNow;
        book.ResetSearchBlob();
        Repo.UpsertState(book.DriveId, book.RelPath, book.StateKey, book.ToState());
        AppServices.Sync.Schedule(book.FolderId);
    }

    public void ToggleFavorite(Book book)
    {
        book.IsFavorite = !book.IsFavorite;
        SaveState(book);
        if (Filter == LibraryFilter.Favorites) ApplyFilter();
    }

    public void SetStatus(Book book, ReadStatus status)
    {
        book.Status = status;
        if (status == ReadStatus.Finished)
        {
            book.FinishedUtc = DateTime.UtcNow;
            book.Progress = 100;
        }
        else if (status == ReadStatus.Unread)
        {
            book.Progress = 0;
            book.FinishedUtc = null;
        }
        SaveState(book);
        if (Filter is LibraryFilter.ContinueReading or LibraryFilter.Unread or LibraryFilter.Finished) ApplyFilter();
    }

    /// <summary>Opens the book in the default reader and marks it as being read. Returns an error message or null.</summary>
    public string? OpenBook(Book book)
    {
        var error = BookLauncher.Open(book);
        if (error is not null) return error;

        book.LastOpenedUtc = DateTime.UtcNow;
        if (book.Status == ReadStatus.Unread) book.Status = ReadStatus.Reading;
        SaveState(book);
        return null;
    }

    /// <summary>Filter-aware "Surprise Me": a random available book from what is currently shown.</summary>
    public Book? PickRandom()
    {
        var pool = Books.Where(b => b.IsAvailable && b.Status != ReadStatus.Finished).ToList();
        if (pool.Count == 0) pool = Books.Where(b => b.IsAvailable).ToList();
        return pool.Count == 0 ? null : pool[_random.Next(pool.Count)];
    }

    public async Task RestoreFromBackupAsync(string file)
    {
        var applied = await Task.Run(() => BackupService.Import(file, Repo));
        await ReloadAsync();
        StatusText = $"Backup restored: {applied} entries applied";
        foreach (var f in Repo.GetFolders()) AppServices.Sync.Schedule(f.Id);
    }
}
