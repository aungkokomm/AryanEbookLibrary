using AryanEbookLibrary.Helpers;
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
    private Task _sidecarSync = Task.CompletedTask;   // the sidecars being brought level, which Close lets finish
    private bool _closing;
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
        AppServices.Online.StatusChanged += text => { OnlineStatusText = text; IsLookingUp = true; };
        AppServices.Online.Finished += () => IsLookingUp = false;
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
                ViewChanged();
            }
        }
    }

    private string _seriesFilter = "";
    /// <summary>One series only, chosen on the Series page. Empty means every book.</summary>
    public string SeriesFilter
    {
        get => _seriesFilter;
        set
        {
            if (!SetProperty(ref _seriesFilter, value ?? "")) return;
            if (_seriesFilter.Length > 0) _tagFilter = "";
            OnPropertyChanged(nameof(HeaderText));
            ViewChanged();
        }
    }

    private string _tagFilter = "";
    /// <summary>One of the user's tags, chosen on the Tags page. Empty means every book.</summary>
    public string TagFilter
    {
        get => _tagFilter;
        set
        {
            if (!SetProperty(ref _tagFilter, value ?? "")) return;
            if (_tagFilter.Length > 0) _seriesFilter = "";
            OnPropertyChanged(nameof(HeaderText));
            ViewChanged();
        }
    }

    private string _listFilter = "";
    /// <summary>One of the user's own lists (My lists in the pane). Empty means no list.</summary>
    public string ListFilter
    {
        get => _listFilter;
        set
        {
            if (!SetProperty(ref _listFilter, value ?? "")) return;
            OnPropertyChanged(nameof(HeaderText));
            ViewChanged();
        }
    }

    public string HeaderText => ActiveShelf.Length > 0 ? ActiveShelf
        : ListFilter.Length > 0 ? ListFilter
        : SeriesFilter.Length > 0 ? "Series: " + SeriesFilter
        : TagFilter.Length > 0 ? "Tag: " + TagFilter
        : ShelfFilter.ListName(Filter);

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                if (!_applyingView) LeaveShelf();
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
            if (SetProperty(ref _formatIndex, value)) ViewChanged();
        }
    }

    // ---- the Filters menu: language, decade, publisher, rating, connected drives ----

    private string _languageFilter = "";
    /// <summary>A language key ("my", "en"), <see cref="ShelfFilter.NotStated"/>, or empty for any.</summary>
    public string LanguageFilter
    {
        get => _languageFilter;
        set { if (SetProperty(ref _languageFilter, value ?? "")) ViewChanged(); }
    }

    private string _decadeFilter = "";
    public string DecadeFilter
    {
        get => _decadeFilter;
        set { if (SetProperty(ref _decadeFilter, value ?? "")) ViewChanged(); }
    }

    private string _publisherFilter = "";
    public string PublisherFilter
    {
        get => _publisherFilter;
        set { if (SetProperty(ref _publisherFilter, value ?? "")) ViewChanged(); }
    }

    private int _minRating;
    public int MinRating
    {
        get => _minRating;
        set { if (SetProperty(ref _minRating, Math.Clamp(value, 0, 5))) ViewChanged(); }
    }

    private bool _connectedOnly;
    /// <summary>Only books on a drive that is plugged in now, so every one of them opens.</summary>
    public bool ConnectedOnly
    {
        get => _connectedOnly;
        set { if (SetProperty(ref _connectedOnly, value)) ViewChanged(); }
    }

    public List<FacetOption> LanguageOptions() => ShelfFilter.Languages(_all, CurrentView());
    public List<FacetOption> DecadeOptions() => ShelfFilter.Decades(_all, CurrentView());
    public List<FacetOption> PublisherOptions() => ShelfFilter.Publishers(_all, CurrentView());
    public List<FacetOption> RatingOptions() => ShelfFilter.Ratings(_all, CurrentView());

    /// <summary>How many of the Filters menu's filters are on (the button says so).</summary>
    public int MenuFilterCount => ShelfFilter.MenuFilters(CurrentView()).Count;

    /// <summary>Anything narrower than the plain list is showing: format, search, tag, series or a menu filter.</summary>
    public bool IsFiltered => ShelfFilter.IsFiltered(CurrentView());

    // ---- shelves: saved views ----

    private bool _applyingView;

    private string _activeShelf = "";
    /// <summary>The shelf being shown, until any filter changes. Empty when the view is not a saved one.</summary>
    public string ActiveShelf => _activeShelf;

    /// <summary>The shelves changed (saved, renamed, deleted): the navigation pane rebuilds its list.</summary>
    public event EventHandler? ShelvesChanged;

    public IReadOnlyList<Shelf> Shelves => Settings.Shelves;

    public Shelf? FindShelf(string name) =>
        Settings.Shelves.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));

    public Shelf CurrentView() => new()
    {
        List = Filter,
        Format = FormatIndex,
        Language = LanguageFilter,
        Decade = DecadeFilter,
        Publisher = PublisherFilter,
        MinRating = MinRating,
        ConnectedOnly = ConnectedOnly,
        Tag = TagFilter,
        Series = SeriesFilter,
        Search = SearchText
    };

    /// <summary>A filter changed: refresh, and the view is no longer the shelf it came from.</summary>
    private void ViewChanged()
    {
        if (_applyingView) return;
        LeaveShelf();
        ApplyFilter();
    }

    private void LeaveShelf()
    {
        if (_activeShelf.Length == 0) return;
        _activeShelf = "";
        OnPropertyChanged(nameof(ActiveShelf));
        OnPropertyChanged(nameof(HeaderText));
    }

    /// <summary>Sets every part of the view at once, with one refresh instead of one per filter.</summary>
    private void SetView(Shelf v, string activeShelf)
    {
        _applyingView = true;
        try
        {
            Filter = v.List;
            FormatIndex = v.Format;
            LanguageFilter = v.Language;
            DecadeFilter = v.Decade;
            PublisherFilter = v.Publisher;
            MinRating = v.MinRating;
            ConnectedOnly = v.ConnectedOnly;
            SeriesFilter = v.Series;
            TagFilter = v.Tag;
            SearchText = v.Search;
        }
        finally
        {
            _applyingView = false;
        }
        _searchTimer.Stop();
        _activeShelf = activeShelf;
        OnPropertyChanged(nameof(ActiveShelf));
        OnPropertyChanged(nameof(HeaderText));
        ApplyFilter();
    }

    public void ShowShelf(Shelf shelf)
    {
        _listFilter = "";   // a shelf is a view of the whole library, never of one list
        OnPropertyChanged(nameof(ListFilter));
        SetView(shelf, shelf.Name);
    }

    /// <summary>Back to the plain list: every filter off, the list itself (Unread, Favorites...) kept.</summary>
    public void ClearFilters() => SetView(new Shelf { List = Filter }, "");

    /// <summary>Saves what is showing as a shelf, replacing one of the same name, and shows it as that shelf.</summary>
    public void SaveShelf(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var shelf = CurrentView().Copy(name);
        var at = Settings.Shelves.FindIndex(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (at >= 0) Settings.Shelves[at] = shelf;
        else Settings.Shelves.Add(shelf);
        Settings.Save();
        _activeShelf = name;
        OnPropertyChanged(nameof(ActiveShelf));
        OnPropertyChanged(nameof(HeaderText));
        ShelvesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenameShelf(string from, string to)
    {
        to = to.Trim();
        var shelf = FindShelf(from);
        if (shelf is null || to.Length == 0 || FindShelf(to) is { } other && other != shelf) return;
        shelf.Name = to;
        Settings.Save();
        if (string.Equals(_activeShelf, from, StringComparison.CurrentCultureIgnoreCase))
        {
            _activeShelf = to;
            OnPropertyChanged(nameof(ActiveShelf));
            OnPropertyChanged(nameof(HeaderText));
        }
        ShelvesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the shelf. Its books are untouched: a shelf is only a saved way of looking.</summary>
    public void DeleteShelf(string name)
    {
        if (Settings.Shelves.RemoveAll(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase)) == 0) return;
        Settings.Save();
        if (string.Equals(_activeShelf, name, StringComparison.CurrentCultureIgnoreCase)) LeaveShelf();
        ShelvesChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>A line in the status bar, which shows for a few seconds (books added to a list, a copy finished).</summary>
    public void Tell(string text)
    {
        _statusText = text;
        OnPropertyChanged(nameof(StatusText));   // even when it says the same again, so the bar shows again
    }

    private int _totalCount;
    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }

    private string _countText = "";
    public string CountText { get => _countText; private set => SetProperty(ref _countText, value); }

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }

    private string _emptyMessage = "";
    public string EmptyMessage { get => _emptyMessage; private set => SetProperty(ref _emptyMessage, value); }

    private string _onlineStatusText = "";
    /// <summary>Open Library's background progress, shown at the right of the status bar.</summary>
    public string OnlineStatusText { get => _onlineStatusText; private set => SetProperty(ref _onlineStatusText, value); }

    private bool _isLookingUp;
    /// <summary>True while the online lookups run. Their last message stays in OnlineStatusText after they end.</summary>
    public bool IsLookingUp { get => _isLookingUp; private set => SetProperty(ref _isLookingUp, value); }

    /// <summary>Drops a finished run's last message, so it does not come back the next time the status bar shows.</summary>
    public void ForgetOnlineStatus()
    {
        if (!IsLookingUp) OnlineStatusText = "";
    }

    private List<Book> _continueBooks = new();
    /// <summary>
    /// The slim row over All Books: what is being read on a connected drive, most recently opened first. Empty in
    /// any other view, or with a search or filter on.
    /// </summary>
    public List<Book> ContinueBooks { get => _continueBooks; private set => SetProperty(ref _continueBooks, value); }

    private void RefreshContinueBooks()
    {
        var show = Filter == LibraryFilter.All && ActiveShelf.Length == 0 && ListFilter.Length == 0 && !IsFiltered;
        var books = show
            ? _all.Where(b => b.Status == ReadStatus.Reading && b.IsAvailable)
                .OrderByDescending(b => b.LastOpenedUtc ?? DateTime.MinValue).Take(12).ToList()
            : new List<Book>();
        if (!books.SequenceEqual(_continueBooks)) ContinueBooks = books;   // not rebuilt on every keystroke of a search
    }

    /// <summary>Every book in the catalogue, whatever the current filter shows.</summary>
    public IReadOnlyList<Book> AllBooks => _all;

    public Book? FindByKey(string key) => _all.FirstOrDefault(b => b.StateKey == key);

    /// <summary>Fills missing details from Open Library in the background, when switched on in Settings.</summary>
    public void StartOnlineLookups() => AppServices.Online.Start(_all);

    // ------------------------------------------------------------ lifecycle

    public async void Initialize()
    {
        try
        {
            // Where a slow start goes, in the log: the app and its window, then each step before the books show.
            var before = (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => DriveRegistry.Refresh(Repo.GetDrives()));
            var drives = clock.ElapsedMilliseconds;
            await ReloadAsync();
            Log.Write($"app: {TotalCount:N0} books shown {before + clock.ElapsedMilliseconds:N0} ms after start (app and window " +
                      $"{before:N0}, drives {drives:N0}, index {clock.ElapsedMilliseconds - drives:N0} ms)");
            Log.Write($"app: start steps, ms after the process began: {StartupTimes.Summary()}");

            // Edits made while a drive was away reach its sidecars now that it is here, and edits made on another
            // computer come in. After the books show, not before: it reads (and may write) files in every folder, half
            // a second to a second at start-up, and what comes in is rare, so the books are read again only then.
            var synced = System.Diagnostics.Stopwatch.StartNew();
            var came = await SyncSidecarsAsync(Repo.GetFolders().Where(f => DriveRegistry.IsOnline(f.DriveId)));
            Log.Write($"app: sidecars synced in {synced.ElapsedMilliseconds:N0} ms" + (came > 0 ? $", {came:N0} changes came in" : ""));
            if (_closing) return;   // the window closed while they synced: the database is closed now
            if (came > 0)
            {
                await ReloadAsync();
                AppServices.Annotations.RaiseChanged();
            }
            // Drive changes arrive from DeviceChangeWatcher (WM_DEVICECHANGE); nothing polls.
        }
        catch (Exception ex)
        {
            Log.Write("Initialize failed: " + ex);
            StatusText = "Could not load the library: " + ex.Message;
            return;
        }

        if (_closing) return;
        if (!Settings.AutoScanOnStart || Repo.GetFolders().Count == 0)
        {
            StartOnlineLookups();
            return;
        }
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
        var lists = Repo.GetLists();   // a sidecar or a backup may have brought new ones
        var listsChanged = !lists.SequenceEqual(_lists);
        _lists = lists;
        TotalCount = list.Count;
        ApplyFilter();
        if (listsChanged) ListsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings each folder's sidecar and the index level: newer edits from the drive come in, and edits made here
    /// while the drive was away are written to it. Touches the drives, so call it off the UI thread.
    /// </summary>
    private static int SyncSidecars(IEnumerable<LibraryFolder> folders) => folders.Sum(f => AppServices.Sync.SyncFolder(f));

    /// <summary>The sync off the UI thread, kept so that closing waits for it.</summary>
    private Task<int> SyncSidecarsAsync(IEnumerable<LibraryFolder> folders)
    {
        var sync = Task.Run(() => SyncSidecars(folders));
        _sidecarSync = sync;
        return sync;
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
            if (ConnectedOnly) ApplyFilter();   // a drive coming or going changes which books this view shows

            var arrived = drives.FirstOrDefault(d => !before[d.Id] && DriveRegistry.IsOnline(d.Id));
            var left = drives.FirstOrDefault(d => before[d.Id] && !DriveRegistry.IsOnline(d.Id));

            // A drive plugged in again gets the edits made while it was away (notes, lists, favorites...), and
            // brings in the ones made on another computer since.
            var back = drives.Where(d => !before[d.Id] && DriveRegistry.IsOnline(d.Id)).Select(d => d.Id).ToHashSet();
            if (back.Count > 0 && !IsScanning)
            {
                var folders = Repo.GetFolders().Where(f => back.Contains(f.DriveId)).ToList();
                if (await SyncSidecarsAsync(folders) > 0 && !_closing)
                {
                    await ReloadAsync();
                    AppServices.Annotations.RaiseChanged();
                }
            }
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

    public void ApplyFilter()
    {
        var view = CurrentView();
        var q = ShelfFilter.Apply(_all, view);
        if (ListFilter.Length > 0) q = q.Where(b => b.InList(ListFilter));

        IEnumerable<Book> sorted;
        if (SeriesFilter.Length > 0)
            sorted = q.OrderBy(b => b.SeriesIndex ?? double.MaxValue).ThenBy(b => b.SortTitle, StringComparer.CurrentCultureIgnoreCase);
        else if (Filter == LibraryFilter.ContinueReading)
            sorted = q.OrderByDescending(b => b.LastOpenedUtc ?? DateTime.MinValue);
        else if (Filter == LibraryFilter.RecentlyAdded)
            sorted = q.OrderByDescending(b => b.AddedUtc).Take(200);
        else
            sorted = Sort(q);

        var result = sorted.ToList();
        Books = result;

        IsEmpty = result.Count == 0;
        EmptyMessage = _all.Count == 0
            ? "Pick the folder where your eBooks are: EPUB, PDF, MOBI, AZW3, KFX, CBZ or CBR. Aryan only reads it, and never changes your files."
            : ListFilter.Length > 0 && !IsFiltered
                ? "Add books with “Add to list” in a book's details or its right-click menu, or drag books onto the list in the pane."
                : Filter == LibraryFilter.Notes && !IsFiltered
                    ? "Open a book's details and use “Add note” to write what you thought of it."
                    : "No books match the current filter.";
        var count = result.Count == _all.Count || Filter == LibraryFilter.RecentlyAdded
            ? Fn.Count(result.Count, "book")
            : $"{result.Count:N0} of {_all.Count:N0} books";
        // The Filters menu hides its choices, so the line under the title says which are on.
        var menu = ShelfFilter.MenuFilters(view);
        CountText = menu.Count == 0 ? count : count + "  ·  " + string.Join("  ·  ", menu);
        OnPropertyChanged(nameof(MenuFilterCount));
        OnPropertyChanged(nameof(IsFiltered));
        RefreshContinueBooks();
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
            var lastId = Repo.LastBookId();
            foreach (var folder in folders)
            {
                // Pick up favorites/notes stored on the drive itself before indexing.
                var imported = await Task.Run(() => AppServices.Sync.Import(folder), ct);
                total.Add(await AppServices.Scanner.ScanAsync(new[] { folder }, progress, ct));
                await ReloadAsync();
                if (imported > 0) AppServices.Annotations.RaiseChanged();
            }

            // Files moved or renamed in Explorer since the last scan take their old book's place, with everything the
            // user gave it. Only now: a file moved to another folder is missing from one and new in the other.
            var moved = await Task.Run(() => MissingBooks.RelinkMoved(Repo), ct);
            if (moved.Count > 0)
            {
                total.Moved = moved.Count;
                total.Inserted -= moved.Count(m => m.NewRowId > lastId);   // counted as new by the folder's scan
                var scanned = folders.Select(f => f.Id).ToHashSet();
                total.Missing = Repo.GetMissing().Count(m => scanned.Contains(m.FolderId));
                // The drives' own copies (sidecars) under the new paths, and a renamed file's name read again.
                foreach (var id in moved.SelectMany(m => new[] { m.Book.FolderId, m.FolderId }).Distinct())
                    AppServices.Sync.Schedule(id);
                await Task.Run(AppServices.Scanner.RefreshNamesFromFiles, ct);
                await ReloadAsync();
                AppServices.Annotations.RaiseChanged();
            }

            StatusText = "Scan complete: " + total.Summary +
                         (total.OfflineFolders > 0 ? $"  ·  {total.OfflineFolders} folder(s) offline, kept in catalog" : "");
            StartOnlineLookups();
            return total;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
            await ReloadAsync();
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Cancelled while a step was using the database, which the app closing has just shut: a cancelled scan.
            throw new OperationCanceledException(ct);
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

    /// <summary>
    /// The window is closing, before the database does: a scan stops, and a sync of the sidecars under way (it writes to
    /// them and to the database) is let finish, a second or two.
    /// </summary>
    public void Close()
    {
        _closing = true;
        CancelScan();
        try
        {
            if (!_sidecarSync.Wait(TimeSpan.FromSeconds(15))) Log.Write("app: the sidecars were still syncing at close");
        }
        catch (Exception ex)
        {
            Log.Write("app: the sidecar sync failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------ personal state

    /// <summary>
    /// Writes one person's name the same way on every book that names them ("S. Dhammika" → "Shravasti
    /// Dhammika"). It is an edit like any other: the user's name is kept in book_state, the files are untouched,
    /// and a book with several authors keeps the others.
    /// </summary>
    public int RenamePerson(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || from == to) return 0;
        var changed = 0;
        foreach (var book in _all)
        {
            var people = AuthorIndex.Split(book.Author).ToList();
            var index = people.FindIndex(p => string.Equals(p, from, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;
            people[index] = to;
            var author = string.Join(", ", people.Distinct(StringComparer.OrdinalIgnoreCase));
            if (author == book.Author) continue;
            book.SetCustomDetails(book.CustomTitle, author, book.CustomSeries);
            SaveState(book);
            changed++;
        }
        if (changed > 0) ApplyFilter();
        return changed;
    }

    /// <summary>The user's own author on these books (an edit like any other, kept in book_state; the files are untouched).</summary>
    public int SetAuthor(IEnumerable<Book> books, string author)
    {
        var changed = 0;
        foreach (var book in books)
        {
            if (book.Author == author) continue;
            book.SetCustomDetails(book.CustomTitle, author, book.CustomSeries);
            SaveState(book);
            changed++;
        }
        if (changed > 0) ApplyFilter();
        return changed;
    }

    /// <summary>Authors worked out for books a website left without one, minus the ones the user turned down.</summary>
    public List<AuthorOffer> GetAuthorOffers() => AuthorSuggester.Build(_all, Settings.NoAuthorOffers);

    /// <summary>The library's authors, and the names that look like one person written differently.</summary>
    public List<AuthorEntry> GetAuthors() => AuthorIndex.Build(_all, Repo.GetAllAuthorsOnline());

    /// <summary>The library's series, with the volumes that are missing between the ones you have.</summary>
    public List<SeriesEntry> GetSeries() => SeriesIndex.Build(_all);

    /// <summary>The user's own tags, most used first.</summary>
    public List<TagEntry> GetTags() => Tags.Build(_all);

    /// <summary>Tags the library can work out by itself, minus the ones the user turned down.</summary>
    public List<TagSuggestion> GetTagSuggestions() => TagSuggester.Build(_all, Settings.NoTags);

    /// <summary>Writes a suggested tag onto its books. Returns how many books took it.</summary>
    public int ApplyTagSuggestion(TagSuggestion suggestion)
    {
        var changed = 0;
        foreach (var book in suggestion.Books)
        {
            var now = Tags.Merge(book.UserTags, suggestion.Tag);
            if (now == book.UserTags) continue;
            book.UserTags = now;
            SaveState(book);
            changed++;
        }
        if (changed > 0) ApplyFilter();
        return changed;
    }

    /// <summary>Not that one: it is not offered again until the user forgets the refusals in Settings.</summary>
    public void RefuseTagSuggestion(TagSuggestion suggestion)
    {
        if (Settings.NoTags.Contains(suggestion.Key, StringComparer.OrdinalIgnoreCase)) return;
        Settings.NoTags.Add(suggestion.Key);
        Settings.Save();
    }

    /// <summary>
    /// Renames a tag on every book wearing it; a name already in use joins the two. Shelves and the library's
    /// tag filter follow the new name. Returns how many books changed.
    /// </summary>
    public int RenameTag(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || to.Contains(',') || from == to) return 0;
        var changed = 0;
        foreach (var book in _all.Where(b => Tags.Has(b, from)))
        {
            var now = Tags.Rename(book.UserTags, from, to);
            if (now == book.UserTags) continue;
            book.UserTags = now;
            SaveState(book);
            changed++;
        }
        var shelves = Settings.Shelves.Where(s => string.Equals(s.Tag, from, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var shelf in shelves) shelf.Tag = to;
        if (shelves.Count > 0) Settings.Save();
        if (string.Equals(TagFilter, from, StringComparison.CurrentCultureIgnoreCase)) TagFilter = to;
        else if (changed > 0) ApplyFilter();
        return changed;
    }

    /// <summary>Takes a tag off every book wearing it. The books stay. Returns how many books changed.</summary>
    public int RemoveTag(string name)
    {
        var changed = 0;
        foreach (var book in _all.Where(b => Tags.Has(b, name)))
        {
            book.UserTags = Tags.Remove(book.UserTags, name);
            SaveState(book);
            changed++;
        }
        if (string.Equals(TagFilter, name, StringComparison.CurrentCultureIgnoreCase)) TagFilter = "";
        else if (changed > 0) ApplyFilter();
        return changed;
    }

    /// <summary>Adds or removes one tag on one book (the Tags page, the book's own menu).</summary>
    public void SetTag(Book book, string tag, bool wanted)
    {
        var now = wanted ? Tags.Merge(book.UserTags, tag) : Tags.Remove(book.UserTags, tag);
        if (now == book.UserTags) return;
        book.UserTags = now;
        SaveState(book);
        if (TagFilter.Length > 0) ApplyFilter();
    }

    // ------------------------------------------------------------ working on many books at once

    private bool _selectionMode;
    /// <summary>In selection mode a click ticks a book instead of opening it, and the action bar is shown.</summary>
    public bool SelectionMode
    {
        get => _selectionMode;
        set
        {
            if (!SetProperty(ref _selectionMode, value)) return;
            if (!value) ClearSelection();
            SelectionModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The cards and rows follow this to show or hide their tick.</summary>
    public event EventHandler? SelectionModeChanged;

    private int _selectedCount;
    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetProperty(ref _selectedCount, value)) OnPropertyChanged(nameof(SelectedText));
        }
    }

    public string SelectedText => SelectedCount switch
    {
        0 => "Nothing selected",
        1 => "1 book selected",
        _ => $"{SelectedCount:N0} books selected"
    };

    public List<Book> Selected => _all.Where(b => b.IsSelected).ToList();

    public void Select(Book book, bool selected)
    {
        if (book.IsSelected == selected) return;
        book.IsSelected = selected;
        SelectedCount += selected ? 1 : -1;
    }

    public void ToggleSelect(Book book) => Select(book, !book.IsSelected);

    /// <summary>Ticks every book the current filter and search show, not the whole catalogue.</summary>
    public void SelectAllShown()
    {
        foreach (var b in Books) b.IsSelected = true;
        SelectedCount = _all.Count(b => b.IsSelected);
    }

    public void ClearSelection()
    {
        foreach (var b in _all.Where(b => b.IsSelected)) b.IsSelected = false;
        SelectedCount = 0;
    }

    /// <summary>
    /// The same edit on every selected book: an author, a series, tags to add. Empty fields are left alone,
    /// and the files are never touched (these are the user's own details, as in the book's own dialog).
    /// </summary>
    public int EditSelected(string? author, string? series, string? addTags, string? removeTags = null)
    {
        var changed = 0;
        foreach (var book in Selected)
        {
            var before = (book.CustomAuthor, book.CustomSeries, book.UserTags);

            if (!string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(series))
                book.SetCustomDetails(book.CustomTitle,
                    string.IsNullOrWhiteSpace(author) ? book.CustomAuthor : author.Trim(),
                    string.IsNullOrWhiteSpace(series) ? book.CustomSeries : series.Trim());

            if (!string.IsNullOrWhiteSpace(addTags)) book.UserTags = Tags.Merge(book.UserTags, addTags);
            if (!string.IsNullOrWhiteSpace(removeTags))
                foreach (var tag in Tags.Split(removeTags))
                    book.UserTags = Tags.Remove(book.UserTags, tag);

            if (before == (book.CustomAuthor, book.CustomSeries, book.UserTags)) continue;
            SaveState(book);
            changed++;
        }
        if (changed > 0) ApplyFilter();
        return changed;
    }

    public int SetStatusForSelected(ReadStatus status, bool stampDate = true)
    {
        var books = Selected;
        foreach (var book in books) SetStatus(book, status, stampDate);
        return books.Count;
    }

    public int SetFavoriteForSelected(bool favorite)
    {
        var books = Selected.Where(b => b.IsFavorite != favorite).ToList();
        foreach (var book in books)
        {
            book.IsFavorite = favorite;
            SaveState(book);
        }
        if (Filter == LibraryFilter.Favorites) ApplyFilter();
        return books.Count;
    }

    /// <summary>Looks the selected books up online now, whether or not the background fill is switched on.</summary>
    public int LookUpSelectedOnline()
    {
        var books = Selected;
        AppServices.Online.Start(books, force: true);
        return books.Count;
    }

    public void SaveState(Book book)
    {
        book.StateUpdatedUtc = DateTime.UtcNow;
        book.ResetSearchBlob();
        Repo.UpsertState(book.DriveId, book.RelPath, book.StateKey, book.ToState());
        AppServices.Sync.Schedule(book.FolderId);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A book's personal state was saved: the pane's counts (Notes, My lists) may have changed.</summary>
    public event EventHandler? StateChanged;

    // ------------------------------------------------------------ my lists

    private List<string> _lists = new();

    /// <summary>The user's own lists, in the order they were made.</summary>
    public IReadOnlyList<string> UserLists => _lists;

    /// <summary>A list was made, renamed or deleted: the navigation pane rebuilds My lists.</summary>
    public event EventHandler? ListsChanged;

    public string? FindList(string name) =>
        _lists.FirstOrDefault(n => string.Equals(n, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public int ListCount(string name) => _all.Count(b => b.InList(name));

    /// <summary>How many books are on each list, in one pass over the library.</summary>
    public Dictionary<string, int> ListCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var book in _all)
            foreach (var name in book.Lists)
                counts[name] = counts.GetValueOrDefault(name) + 1;
        return counts;
    }

    public List<Book> BooksInList(string name) =>
        _all.Where(b => b.InList(name)).OrderBy(b => b.SortTitle, StringComparer.CurrentCultureIgnoreCase).ToList();

    public int NotesCount => _all.Count(b => b.HasNote);

    /// <summary>Makes a new, empty list. False when the name is empty or taken.</summary>
    public bool CreateList(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || FindList(name) is not null || !Repo.AddList(name)) return false;
        _lists = Repo.GetLists();
        ListsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Renames a list on every book on it. False when the new name is empty or another list has it.</summary>
    public bool RenameList(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0 || FindList(from) is null) return false;
        if (FindList(to) is { } other && !string.Equals(other, from, StringComparison.CurrentCultureIgnoreCase)) return false;
        var shown = string.Equals(_listFilter, from, StringComparison.CurrentCultureIgnoreCase);
        ApplyListChange(Repo.RenameList(from, to));
        if (shown)
        {
            _listFilter = to;
            OnPropertyChanged(nameof(ListFilter));
            OnPropertyChanged(nameof(HeaderText));
        }
        return true;
    }

    /// <summary>Deletes a list. Its books stay in the library: only the list and who was on it go.</summary>
    public void DeleteList(string name)
    {
        if (FindList(name) is null) return;
        var shown = string.Equals(_listFilter, name, StringComparison.CurrentCultureIgnoreCase);
        ApplyListChange(Repo.RenameList(name, null));
        if (shown) ListFilter = "";
    }

    /// <summary>The books the repository changed get the new names, and their folders' sidecars are written.</summary>
    private void ApplyListChange(List<LibraryRepository.StateRow> rows)
    {
        var byKey = rows.ToDictionary(r => r.Key, r => r.State);
        foreach (var b in _all)
        {
            if (!byKey.TryGetValue(b.StateKey, out var s)) continue;
            b.Lists = s.Lists.ToList();
            b.StateUpdatedUtc = s.UpdatedUtc;
        }
        // Every folder holding a changed book, loaded or not (one on an unplugged drive is written when it is back).
        var folders = Repo.GetFolders();
        foreach (var row in rows)
            if (folders.Where(f => f.DriveId == row.DriveId && IsIn(row.RelPath, f.RelPath))
                    .OrderByDescending(f => f.RelPath.Length).FirstOrDefault() is { } folder)
                AppServices.Sync.Schedule(folder.Id);

        _lists = Repo.GetLists();
        ListsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        ApplyFilter();

        static bool IsIn(string relPath, string folder) =>
            folder.Length == 0 || relPath.StartsWith(folder.TrimEnd(Slash) + Slash, StringComparison.OrdinalIgnoreCase);
    }

    private const char Slash = (char)92;

    /// <summary>Puts the book on the list or takes it off (the details window, the book's menu).</summary>
    public void SetInList(Book book, string list, bool wanted)
    {
        list = FindList(list) ?? list.Trim();
        if (book.InList(list) == wanted) return;
        book.Lists = wanted
            ? book.Lists.Append(list).ToList()
            : book.Lists.Where(n => !string.Equals(n, list, StringComparison.CurrentCultureIgnoreCase)).ToList();
        SaveState(book);
        if (ListFilter.Length > 0) ApplyFilter();
    }

    /// <summary>Puts several books on a list at once (books dropped on it). Returns how many were not on it yet.</summary>
    public int AddToList(IEnumerable<Book> books, string list)
    {
        list = FindList(list) ?? list.Trim();
        var added = 0;
        foreach (var book in books.Where(b => !b.InList(list)).ToList())
        {
            book.Lists = book.Lists.Append(list).ToList();
            SaveState(book);
            added++;
        }
        if (added > 0 && ListFilter.Length > 0) ApplyFilter();
        return added;
    }

    public void ToggleFavorite(Book book)
    {
        book.IsFavorite = !book.IsFavorite;
        SaveState(book);
        if (Filter == LibraryFilter.Favorites) ApplyFilter();
    }

    /// <summary>
    /// A book finished now is finished today; with <paramref name="stampDate"/> false it was finished some
    /// time ago and gets no date (it counts for all time, not in any month of the reading log). A book that
    /// was already finished keeps its date.
    /// </summary>
    public void SetStatus(Book book, ReadStatus status, bool stampDate = true)
    {
        ReadingLog.SetStatus(book, status, stampDate ? DateTime.UtcNow : null);
        SaveState(book);
        if (Filter is LibraryFilter.ContinueReading or LibraryFilter.Unread or LibraryFilter.Finished) ApplyFilter();
        else RefreshContinueBooks();
    }

    /// <summary>Opens the book in the default reader and marks it as being read. Returns an error message or null.</summary>
    public string? OpenBook(Book book, bool withDefaultApp = false)
    {
        var error = BookLauncher.Open(book, withDefaultApp);
        if (error is not null) return error;

        book.LastOpenedUtc = DateTime.UtcNow;
        if (book.Status == ReadStatus.Unread) book.Status = ReadStatus.Reading;
        SaveState(book);
        RefreshContinueBooks();   // it comes first in the row now
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
        var applied = await Task.Run(() => BackupService.Import(file, Repo, AppServices.AnnotationStore));
        await ReloadAsync();
        AppServices.Annotations.RaiseChanged();
        StatusText = $"Backup restored: {applied} entries applied";
        foreach (var f in Repo.GetFolders()) AppServices.Sync.Schedule(f.Id);
    }
}
