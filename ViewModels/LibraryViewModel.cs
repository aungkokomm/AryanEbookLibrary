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
    private readonly Random _random = new();
    private CancellationTokenSource? _scanCts;
    private List<Book> _all = new();
    private int _deviceChangeCallId;

    private static LibraryRepository Repo => AppServices.Repo;
    private static AppSettings Settings => AppServices.Settings;

    public LibraryViewModel()
    {
        _searchTimer = _dispatcher.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ApplyFilter();

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
                ApplyFilter();
            }
        }
    }

    /// <summary>Sort key and direction in one step (the sort box lists "Title ↑", "Title ↓", ... as CineLibrary does).</summary>
    public void SetSort(SortMode mode, bool descending)
    {
        if ((SortMode)_sortIndex == mode && _sortDescending == descending) return;
        _sortIndex = (int)mode;
        _sortDescending = descending;
        Settings.SortMode = mode;
        Settings.SortDescending = descending;
        OnPropertyChanged(nameof(SortIndex));
        OnPropertyChanged(nameof(SortDescending));
        ApplyFilter();
    }

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
            // Drive changes arrive from DeviceChangeWatcher (WM_DEVICECHANGE); nothing polls.
        }
        catch (Exception ex)
        {
            Log.Write("Initialize failed: " + ex);
            StatusText = "Could not load the library: " + ex.Message;
            return;
        }

        if (!Settings.AutoScanOnStart || Repo.GetFolders().Count == 0) return;
        try
        {
            await ScanAsync();
        }
        catch
        {
            // cancelled or failed: ScanAsync already logged it and put it in the status bar
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

    /// <summary>
    /// Called by DeviceChangeWatcher on the UI thread when Windows reports a volume arriving or leaving.
    /// USB hubs send several messages per plug-in, so calls are coalesced (CineLibrary's 150 ms debounce).
    /// </summary>
    public async Task OnDeviceChangeAsync()
    {
        var myCall = ++_deviceChangeCallId;
        await Task.Delay(150);
        if (myCall != _deviceChangeCallId) return;

        try
        {
            var drives = Repo.GetDrives();
            var before = drives.ToDictionary(d => d.Id, d => DriveRegistry.IsOnline(d.Id));
            var changed = await Task.Run(() => DriveRegistry.Refresh(drives));
            if (!changed) return;

            foreach (var b in _all)
                b.IsAvailable = DriveRegistry.IsOnline(b.DriveId);

            var arrived = drives.FirstOrDefault(d => !before[d.Id] && DriveRegistry.IsOnline(d.Id));
            var left = drives.FirstOrDefault(d => before[d.Id] && !DriveRegistry.IsOnline(d.Id));
            if (!IsScanning)
                StatusText = arrived is not null ? $"Drive \"{arrived.Label}\" connected"
                    : left is not null ? $"Drive \"{left.Label}\" disconnected, its books stay in the catalog as OFFLINE"
                    : "Drive change detected";

            DriveStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Write("Drive change handling failed: " + ex);
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

    // ------------------------------------------------------------ drives + folders (CineLibrary's drive-first flow)

    /// <summary>Cards for the Drives page: every registered drive, its folders and counts, and whether it is connected.</summary>
    public List<DriveItem> GetDriveCards()
    {
        var counts = Repo.CountBooksByDrive();
        var folderCounts = Repo.CountBooksByFolder();
        var folders = Repo.GetFolders();

        return Repo.GetDrives().Select(d =>
        {
            var online = DriveRegistry.IsOnline(d.Id);
            counts.TryGetValue(d.Id, out var c);
            return new DriveItem
            {
                Id = d.Id,
                Label = d.Label,
                IsConnected = online,
                Root = (online ? DriveRegistry.GetRoot(d.Id) : null) ?? d.LastRoot,
                BookCount = c.Books,
                MissingCount = c.Missing,
                Folders = folders.Where(f => f.DriveId == d.Id)
                    .Select(f => new FolderItem { Folder = f, BookCount = folderCounts.GetValueOrDefault(f.Id) })
                    .ToList()
            };
        }).ToList();
    }

    /// <summary>Connected drives that are not in the library yet. Touches every volume, so call it off the UI thread.</summary>
    public List<(string Id, string Root, string Label)> GetAddableDrives()
    {
        var known = Repo.GetDrives().Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Id, string Root, string Label)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.NoRootDirectory or DriveType.CDRom or DriveType.Unknown || !d.IsReady) continue;
                var identity = DriveRegistry.Identify(d.RootDirectory.FullName);
                if (identity is { } i && !known.Contains(i.Id)) list.Add(i);
            }
            catch
            {
                // drive vanished while we looked at it
            }
        }
        return list;
    }

    /// <summary>Registers a drive under the user's name for it. No scan: folders are added on the drive card.</summary>
    public async Task AddDriveAsync(string id, string label, string root)
    {
        Repo.AddDrive(id, label, root);
        await Task.Run(() => DriveRegistry.Refresh(Repo.GetDrives()));
    }

    /// <summary>
    /// Null when <paramref name="path"/> can be added to the drive, otherwise the reason it cannot. Folders are
    /// scanned with all their subfolders, so a folder inside (or around) a tracked one would index books twice.
    /// </summary>
    public string? CheckNewFolder(string driveId, string path, out string relPath)
    {
        relPath = "";
        var root = DriveRegistry.GetRoot(driveId);
        if (root is null) return "Connect this drive to add or scan folders on it.";

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return $"Please choose a folder on drive {root.TrimEnd('\\')} ({root}).";

        relPath = Path.GetRelativePath(root, full);
        if (relPath == ".") relPath = "";
        var shown = relPath.Length == 0 ? "(entire drive)" : relPath;

        foreach (var f in Repo.GetFolders().Where(f => f.DriveId == driveId))
        {
            var tracked = f.RelPath.Length == 0 ? "(entire drive)" : f.RelPath;
            if (string.Equals(f.RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                return $"'{shown}' is already tracked on this drive.";
            if (IsInside(relPath, f.RelPath))
                return $"'{shown}' is already included: '{tracked}' is scanned together with all of its subfolders.";
            if (IsInside(f.RelPath, relPath))
                return $"'{shown}' contains '{tracked}', which is already tracked. Remove '{tracked}' first, then add this folder.";
        }
        return null;

        static bool IsInside(string child, string parent) =>
            parent.Length == 0 || child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the folder (checked with <see cref="CheckNewFolder"/>) and scans it right away.</summary>
    public async Task<ScanResult> AddFolderAsync(string driveId, string relPath, IProgress<ScanProgress>? observer)
    {
        var folder = Repo.AddFolder(driveId, relPath);
        return await ScanAsync(new[] { folder }, observer);
    }

    /// <summary>"Update" on a drive card: rescans that drive's folders for new or changed books.</summary>
    public Task<ScanResult> UpdateDriveAsync(string driveId, IProgress<ScanProgress>? observer) =>
        ScanAsync(Repo.GetFolders().Where(f => f.DriveId == driveId).ToList(), observer);

    /// <summary>"Refresh changes": rescans the folders of every connected drive.</summary>
    public Task<ScanResult> RefreshChangesAsync(IProgress<ScanProgress>? observer) =>
        ScanAsync(Repo.GetFolders().Where(f => DriveRegistry.IsOnline(f.DriveId)).ToList(), observer);

    public void RemoveDrive(string driveId)
    {
        Repo.RemoveDrive(driveId);
        _ = ReloadAsync();
    }

    /// <summary>Drops books the last scan could not find. Their favorites/notes rows are kept, like everywhere else.</summary>
    public void RemoveMissing(IEnumerable<long> bookIds) => Repo.DeleteBooks(bookIds);

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

    /// <summary>
    /// Scans the folders (all of them when null) one after another. The status bar follows along, and
    /// <paramref name="observer"/> gets the same progress (the Drives page overlay). Throws
    /// OperationCanceledException when cancelled and rethrows real failures after logging them, so the
    /// caller can tell the user instead of the scan silently doing nothing.
    /// </summary>
    public async Task<ScanResult> ScanAsync(IReadOnlyList<LibraryFolder>? folders = null, IProgress<ScanProgress>? observer = null)
    {
        if (IsScanning) throw new InvalidOperationException("A scan is already running.");

        folders ??= Repo.GetFolders();
        var total = new ScanResult();
        if (folders.Count == 0)
        {
            StatusText = "No folders to scan. Add one from Drives & Folders.";
            return total;
        }

        IsScanning = true;
        ScanDone = 0;
        ScanTotal = 0;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanTotal = p.Found;   // total first so Done never exceeds the progress bar maximum
            ScanDone = p.Checked;
            if (!p.Done)
                StatusText = p.Checked == 0 ? $"Looking for books... {p.Found:N0} found" : $"Indexing {p.Checked:N0} / {p.Found:N0}";
            observer?.Report(p);
        });

        try
        {
            foreach (var folder in folders)
            {
                // Pick up favorites/notes stored on the drive itself before indexing.
                await Task.Run(() => AppServices.Sync.Import(folder), ct);
                total.Add(await AppServices.Scanner.ScanAsync(new[] { folder }, progress, ct));
                await ReloadAsync();
            }

            StatusText = "Scan complete: " + total.Summary +
                         (total.OfflineFolders > 0 ? $"  ·  {total.OfflineFolders} folder(s) offline, kept in catalog" : "");
            return total;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
            await ReloadAsync();
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("Scan failed: " + ex);
            StatusText = "Scan failed: " + ex.Message;
            throw;
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
