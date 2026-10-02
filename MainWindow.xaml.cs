using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using AryanEbookLibrary.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace AryanEbookLibrary;

public sealed partial class MainWindow : Window
{
    public LibraryViewModel Library => AppServices.Library;
    private readonly DeviceChangeWatcher _deviceWatcher;

    public MainWindow()
    {
        StartupTimes.Mark("theme");
        InitializeComponent();
        StartupTimes.Mark("window");

        Title = "Aryan eBook Library";
        // Mica, so the title bar and the sidebar sit on the desktop's own material, as Windows 11 apps do.
        // It follows the app's theme, not Windows', which matters when the user picks Dark on a light system.
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        else RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);
        PaintCaptionButtons();
        RootGrid.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        // A colour theme tints the title bar and the sidebar (the root, otherwise Mica) most, the page a touch.
        ColorTheme.Follow(this, RootGrid, PageTint);
        AppWindow.Changed += (_, e) => { if (e.DidSizeChange) SyncTitleBar(); };
        AppTitleBar.Loaded += (_, _) => SyncTitleBar();
        Library.PropertyChanged += OnLibraryChanged;
        Library.ShelvesChanged += (_, _) => BuildShelves();
        BuildShelves();
        ShelvesItem.IsExpanded = AppServices.Settings.ShelvesExpanded;
        // Only the user's own folding is remembered. A window narrow enough for the pane to shrink to icons folds
        // every group by itself, and that used to be saved as if the user had folded them.
        NavView.Expanding += (_, e) =>
        {
            if (NavView.DisplayMode != NavigationViewDisplayMode.Expanded) return;
            if (e.ExpandingItemContainer == ShelvesItem) RememberShelvesOpen(true);
            else if (e.ExpandingItemContainer == ListsItem) RememberListsOpen(true);
        };
        NavView.Collapsed += (_, e) =>
        {
            if (NavView.DisplayMode != NavigationViewDisplayMode.Expanded) return;
            if (e.CollapsedItemContainer == ShelvesItem) RememberShelvesOpen(false);
            else if (e.CollapsedItemContainer == ListsItem) RememberListsOpen(false);
        };
        // Back to the full pane: the groups open again as the user left them.
        NavView.DisplayModeChanged += (_, e) =>
        {
            if (e.DisplayMode != NavigationViewDisplayMode.Expanded) return;
            ShelvesItem.IsExpanded = AppServices.Settings.ShelvesExpanded;
            ListsItem.IsExpanded = AppServices.Settings.ListsExpanded;
        };
        Library.ListsChanged += (_, _) => BuildLists();
        Library.StateChanged += (_, _) => CountsSoon();
        AppServices.Annotations.Changed += _ => CountsSoon();
        foreach (var tag in CountedTags) AddCount(FindNav(tag)!);
        // The card only fits the full pane; on a narrow window the pane is a strip of icons until it is opened.
        NavView.PaneOpening += (_, _) => NowReadingCard.Visibility = _nowReading is null ? Visibility.Collapsed : Visibility.Visible;
        NavView.PaneClosing += (_, _) => NowReadingCard.Visibility = Visibility.Collapsed;
        BuildLists();
        ListsItem.IsExpanded = AppServices.Settings.ListsExpanded;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));

        // Event-driven drive detection (WM_DEVICECHANGE) instead of a poll timer, as in CineLibrary.
        // Don't await inside the WndProc: it must return promptly.
        _deviceWatcher = new DeviceChangeWatcher(WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => _ = Library.OnDeviceChangeAsync(), () => CloseDown("Windows is ending the session"));

        Closed += (_, _) => CloseDown("closing");

        SelectNav("all");
        ContentFrame.Navigate(typeof(LibraryPage));
        StartupTimes.Mark("page");
        // The search box must not be what the app opens with a caret in.
        // Pointer focus, not Programmatic: it keeps the search box from taking focus at startup without
        // painting a focus rectangle on the pane button.
        RootGrid.Loaded += (_, _) => NavView.Focus(FocusState.Pointer);
        if (Session.LastEndedBadly) RootGrid.Loaded += async (_, _) => await TellLastSessionEndedBadlyAsync();
        Library.Initialize();
        // Once per start, and never in the way: see UpdateChecker.
        _ = CheckForUpdateAsync();
    }

    // ---- update bubble ----

    /// <summary>The newer release the bubble is offering, until it is answered.</summary>
    private UpdateInfo? _update;

    private async Task CheckForUpdateAsync()
    {
        if (await UpdateChecker.CheckAsync(AppServices.Settings.SkippedUpdateVersion) is not { } update) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _update = update;
            UpdateText.Text = $"Aryan eBook Library {update.Version} is available";
            UpdateBubble.Visibility = Visibility.Visible;
        });
    }

    private async void OnUpdateDownload(object sender, RoutedEventArgs e)
    {
        if (_update is { } update)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(update.ReleaseUrl));
            }
            catch (Exception ex)
            {
                Log.Write($"update: could not open the release page ({ex.GetType().Name})");
            }
        }
        // Not remembered as skipped: until it is installed, the next start offers it again.
        _update = null;
        UpdateBubble.Visibility = Visibility.Collapsed;
    }

    /// <summary>The cross: not this version. A later release is offered as usual.</summary>
    private void OnUpdateSkip(object sender, RoutedEventArgs e)
    {
        if (_update is { } update)
        {
            AppServices.Settings.SkippedUpdateVersion = update.Version;
            AppServices.Settings.Save();
            Log.Write($"update: the user skipped {update.Version}");
        }
        _update = null;
        UpdateBubble.Visibility = Visibility.Collapsed;
    }

    private bool _closedDown;

    /// <summary>Saves and closes everything, once: when the window closes, or when Windows ends the session first.</summary>
    private void CloseDown(string why)
    {
        if (_closedDown) return;
        _closedDown = true;
        Log.Write("app: " + why);
        _deviceWatcher.Dispose();
        // A scan still running would go on writing to the database after it closes; a sidecar sync is let finish.
        Library.Close();
        // The readers first: each saves its place and reading time, and the database closes after.
        Reader.ReaderWindow.CloseAll();
        BookDetailsWindow.CloseAll();   // a note being typed is saved
        AppServices.Shutdown();
    }

    private async Task TellLastSessionEndedBadlyAsync()
    {
        if (Content.XamlRoot is null) return;
        var answer = await new ContentDialog
        {
            Title = "Aryan did not close normally last time",
            Content = "Your library is safe. If this keeps happening, the file aryan.log in the data folder says what went wrong.",
            PrimaryButtonText = "Open data folder",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        }.ShowThemedAsync();
        if (answer != ContentDialogResult.Primary) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Open data folder failed: " + ex.Message);
        }
    }

    // ---- title bar ----

    /// <summary>
    /// Windows draws the minimise, maximise and close buttons itself, over the right of the title bar.
    /// Their colours have to be given to it, or a dark app shows nearly invisible glyphs on light Windows.
    /// </summary>
    private void PaintCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var text = dark ? Colors.White : Colors.Black;

        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = text;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E);
        bar.ButtonHoverForegroundColor = text;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = text;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00);
    }

    /// <summary>
    /// Keeps the search box clear of the caption buttons, and cuts its rectangle out of the window's drag
    /// region. Without that second part the whole title bar drags the window and the box can never be
    /// clicked: the framework's own drag rectangle covers it (proved in the log, "TITLEBAR" lines).
    /// </summary>
    private void SyncTitleBar()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;
        CaptionColumn.Width = new GridLength(Math.Max(AppWindow.TitleBar.RightInset / scale + 8, 56));

        if (TitleSearch.ActualWidth <= 0) return;
        try
        {
            var at = TitleSearch.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
            var box = new Windows.Graphics.RectInt32(
                (int)Math.Round(at.X * scale), (int)Math.Round(at.Y * scale),
                (int)Math.Round(TitleSearch.ActualWidth * scale), (int)Math.Round(TitleSearch.ActualHeight * scale));

            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(id);
            source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, new[] { box });

        }
        catch (Exception ex)
        {
            Log.Write("TITLEBAR passthrough failed: " + ex.Message);
        }
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // On a narrow window the name gives way to the search box, as Windows' own apps do.
        AppNameText.Visibility = e.NewSize.Width < 760 ? Visibility.Collapsed : Visibility.Visible;
        SyncTitleBar();
    }

    private void OnSearchSizeChanged(object sender, SizeChangedEventArgs e) => SyncTitleBar();

    // ---- search ----

    private bool _syncingSearch;

    private void OnTitleSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_syncingSearch || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        Library.SearchText = sender.Text ?? "";
        if (Library.SearchText.Length > 0) Navigate(typeof(LibraryPage));   // searching means books
    }

    private void OnTitleSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        TitleSearch.Text = "";
        Library.SearchText = "";
        e.Handled = true;
    }

    /// <summary>
    /// The page cleared the search (its "Clear filters"), so the box follows. A shelf shown or left moves the
    /// pane's selection with it.
    /// </summary>
    private void OnLibraryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.StatusText) or nameof(LibraryViewModel.OnlineStatusText)
            or nameof(LibraryViewModel.IsScanning) or nameof(LibraryViewModel.IsLookingUp))
        {
            if (e.PropertyName != nameof(LibraryViewModel.OnlineStatusText) || Library.OnlineStatusText.Length > 0)
                ShowStatus();   // a message cleared is no news
            return;
        }
        if (e.PropertyName == nameof(LibraryViewModel.ListFilter))
        {
            if (Library.ListFilter.Length > 0) SelectList(Library.ListFilter);
            else if (NavView.SelectedItem is NavigationViewItem { Tag: string listTag } && listTag.StartsWith(UserListTag))
                SelectNav(ListTag(Library.Filter));   // the list was deleted, or a shelf was shown
            return;
        }
        if (e.PropertyName == nameof(LibraryViewModel.ActiveShelf))
        {
            if (Library.ActiveShelf.Length > 0) SelectShelf(Library.ActiveShelf);
            else if (NavView.SelectedItem is NavigationViewItem { Tag: string tag } && tag.StartsWith(ShelfTag))
                SelectNav(ListTag(Library.Filter));   // a filter changed: this is no longer the shelf
            return;
        }
        if (e.PropertyName != nameof(LibraryViewModel.SearchText)) return;
        if (TitleSearch.Text == Library.SearchText) return;
        _syncingSearch = true;
        TitleSearch.Text = Library.SearchText;
        _syncingSearch = false;
    }

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TitleSearch.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    /// <summary>
    /// Page Up and Down, Home and End move through the books wherever the keyboard is, before the scroller takes them
    /// (it scrolled only while a book had the keyboard, and did nothing with Ctrl). A text box keeps Home and End for its
    /// caret, and a drop-down all four.
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || ContentFrame.Content is not LibraryPage page) return;
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (focused is ComboBox || focused is TextBox && e.Key is Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End) return;
        if (page.ScrollByKey(e.Key)) e.Handled = true;
    }

    private bool _shortcutsOpen;

    /// <summary>F1: the library's keys and mouse actions, as a book's window lists its own.</summary>
    private async void OnShortcutsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_shortcutsOpen || RootGrid.XamlRoot is null) return;
        _shortcutsOpen = true;
        try
        {
            await Reader.ReaderShortcuts.ShowAsync(Reader.ReaderShortcuts.Library(), RootGrid.XamlRoot);
        }
        catch (Exception ex)
        {
            Log.Write("library: the shortcuts could not be shown: " + ex.Message);   // another dialog is open
        }
        finally
        {
            _shortcutsOpen = false;
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        var invoked = args.InvokedItemContainer?.Tag as string ?? "";
        if (invoked.StartsWith(UserListTag))
        {
            ShowList(invoked[UserListTag.Length..]);
            return;
        }
        if (invoked == NewListTag)
        {
            _ = NewListAsync();
            return;
        }
        if (invoked.StartsWith(ShelfTag))
        {
            if (Library.FindShelf(invoked[ShelfTag.Length..]) is { } shelf)
            {
                Library.ShowShelf(shelf);
                Navigate(typeof(LibraryPage));
            }
            return;
        }

        switch (invoked)
        {
            case "all": ShowLibrary(LibraryFilter.All); break;
            case "continue": ShowLibrary(LibraryFilter.ContinueReading); break;
            case "recent": ShowLibrary(LibraryFilter.RecentlyAdded); break;
            case "favorites": ShowLibrary(LibraryFilter.Favorites); break;
            case "unread": ShowLibrary(LibraryFilter.Unread); break;
            case "finished": ShowLibrary(LibraryFilter.Finished); break;
            case "highlights": ShowAnnotations(notes: false); break;
            case "notes": ShowAnnotations(notes: true); break;
            case "needs": ShowLibrary(LibraryFilter.NeedsDetails); break;
            case "authors": Navigate(typeof(AuthorsPage)); break;
            case "series": Navigate(typeof(SeriesPage)); break;
            case "tags": Navigate(typeof(TagsPage)); break;
            case "reading": Navigate(typeof(ReadingPage)); break;
            case "duplicates": Navigate(typeof(DuplicatesPage)); break;
            case "missing": Navigate(typeof(MissingPage)); break;
            case "drives": Navigate(typeof(DrivesPage)); break;
        }
    }

    /// <summary>The empty library's button: Drives &amp; Folders, opening at the folder picker.</summary>
    public void AddFolderWithBooks()
    {
        NavView.SelectedItem = NavView.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == "drives");
        ContentFrame.Navigate(typeof(DrivesPage), DrivesPage.PickFolderFirst);
    }

    /// <summary>All Books, unfiltered: where a new library's first books are shown.</summary>
    public void ShowAllBooks()
    {
        SelectNav("all");
        ShowLibrary(LibraryFilter.All);
    }

    private void ShowLibrary(LibraryFilter filter)
    {
        LeaveShelfView();
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.ListFilter = "";
        Library.Filter = filter;
        Navigate(typeof(LibraryPage));
    }

    /// <summary>
    /// Highlights, or My Notes; with a book's key, only that book's (a book's details "See all"). Going there again
    /// from the pane keeps the search and filters the page has.
    /// </summary>
    public void ShowAnnotations(bool notes, string bookKey = "")
    {
        SelectNav(notes ? "notes" : "highlights");
        if (ContentFrame.Content is AnnotationsPage page && page.View.Notes == notes)
        {
            if (bookKey.Length > 0) page.ShowBook(bookKey);
            return;
        }
        ContentFrame.Navigate(typeof(AnnotationsPage), new AnnotationsView(notes, bookKey));
    }

    /// <summary>Shows the books on one of My lists (from the pane, or a list chip in a book's details).</summary>
    public void ShowList(string name)
    {
        LeaveShelfView();
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.SearchText = "";
        Library.Filter = LibraryFilter.All;
        Library.ListFilter = Library.FindList(name) ?? name;
        SelectList(Library.ListFilter);
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Every book the words find, in All Books (a subject in a book's details).</summary>
    public void ShowSearch(string text)
    {
        LeaveShelfView();
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.ListFilter = "";
        Library.Filter = LibraryFilter.All;
        Library.SearchText = text;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Going anywhere else from a shelf drops what the shelf set, or its filters would follow along.</summary>
    private void LeaveShelfView()
    {
        if (Library.ActiveShelf.Length > 0) Library.ClearFilters();
    }

    /// <summary>Shows every book naming this person (from the Authors page).</summary>
    public void ShowBooksBy(string author) => ShowSearch(author);

    /// <summary>Every book from one publisher (a publisher in a book's details), with the Filters menu's publisher on.</summary>
    public void ShowPublisher(string publisher)
    {
        LeaveShelfView();
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.ListFilter = "";
        Library.SearchText = "";
        Library.Filter = LibraryFilter.All;
        Library.PublisherFilter = publisher;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Shows one series, its numbered books in order (from the Series page).</summary>
    public void ShowSeries(string series)
    {
        LeaveShelfView();
        Library.Filter = LibraryFilter.All;
        Library.SearchText = "";
        Library.ListFilter = "";
        Library.SeriesFilter = series;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Shows every book wearing one tag (from the Tags page).</summary>
    public void ShowTag(string tag)
    {
        LeaveShelfView();
        Library.Filter = LibraryFilter.All;
        Library.SearchText = "";
        Library.ListFilter = "";
        Library.TagFilter = tag;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    private void SelectNav(string tag) => NavView.SelectedItem = FindNav(tag);

    private NavigationViewItem? FindNav(string tag) =>
        NavView.MenuItems.OfType<NavigationViewItem>()
            .SelectMany(i => i.MenuItems.OfType<NavigationViewItem>().Prepend(i))
            .FirstOrDefault(i => (string?)i.Tag == tag);

    private static string ListTag(LibraryFilter list) => list switch
    {
        LibraryFilter.ContinueReading => "continue",
        LibraryFilter.RecentlyAdded => "recent",
        LibraryFilter.Favorites => "favorites",
        LibraryFilter.Unread => "unread",
        LibraryFilter.Finished => "finished",
        LibraryFilter.NeedsDetails => "needs",
        _ => "all"
    };

    // ---- shelves ----

    private const string ShelfTag = "shelf:";

    private void SelectShelf(string name) =>
        NavView.SelectedItem = ShelvesItem.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, ShelfTag + name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>One pane item per saved shelf, under "Shelves", each with Rename and Delete on right-click.</summary>
    private void BuildShelves()
    {
        ShelvesItem.MenuItems.Clear();
        foreach (var shelf in Library.Shelves)
        {
            var name = shelf.Name;
            var item = new NavigationViewItem
            {
                Content = name,
                Tag = ShelfTag + name,
                Icon = new FontIcon { Glyph = ((char)0xE71C).ToString(), Style = PaneIconStyle("PaneLibraryIconStyle") }   // the Filters button's funnel
            };
            ToolTipService.SetToolTip(item, ShelfFilter.SuggestName(shelf));   // what is on it

            var rename = new MenuFlyoutItem { Text = "Rename...", Icon = new SymbolIcon(Symbol.Rename) };
            rename.Click += async (_, _) => await RenameShelfAsync(name);
            var delete = new MenuFlyoutItem { Text = "Delete shelf", Icon = new SymbolIcon(Symbol.Delete) };
            delete.Click += async (_, _) => await DeleteShelfAsync(name);
            item.ContextFlyout = new MenuFlyout { Items = { rename, delete } };

            ShelvesItem.MenuItems.Add(item);
        }
        ShelvesItem.Visibility = Library.Shelves.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Library.ActiveShelf.Length > 0) SelectShelf(Library.ActiveShelf);
    }

    private static void RememberShelvesOpen(bool open)
    {
        if (AppServices.Settings.ShelvesExpanded == open) return;
        AppServices.Settings.ShelvesExpanded = open;
        AppServices.Settings.Save();
    }

    private bool _shelfDialogOpen;

    private async Task RenameShelfAsync(string name)
    {
        if (_shelfDialogOpen || Content.XamlRoot is null) return;
        _shelfDialogOpen = true;
        try
        {
            var to = await ShelfDialogs.AskNameAsync(Content.XamlRoot, "Rename shelf", "Rename", name, renaming: name);
            if (to is not null && to != name) Library.RenameShelf(name, to);
        }
        finally
        {
            _shelfDialogOpen = false;
        }
    }

    private async Task DeleteShelfAsync(string name)
    {
        if (_shelfDialogOpen || Content.XamlRoot is null) return;
        _shelfDialogOpen = true;
        try
        {
            var answer = await new ContentDialog
            {
                Title = "Delete this shelf?",
                Content = $"“{name}” goes from the navigation pane. Its books stay in the library: a shelf is only a saved view.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            }.ShowThemedAsync();
            if (answer != ContentDialogResult.Primary) return;
            var shown = string.Equals(Library.ActiveShelf, name, StringComparison.CurrentCultureIgnoreCase);
            Library.DeleteShelf(name);
            if (shown) Library.ClearFilters();   // the shelf is gone, so its plain list shows
        }
        finally
        {
            _shelfDialogOpen = false;
        }
    }

    // ---- notes and my lists ----

    private const string UserListTag = "userlist:";
    private const string NewListTag = "newlist";
    private readonly Dictionary<string, TextBlock> _listCounts = new(StringComparer.CurrentCultureIgnoreCase);
    private NavigationViewItem? _newListItem;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _countsTimer;

    private void SelectList(string name) =>
        NavView.SelectedItem = ListsItem.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, UserListTag + name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// One pane item per list under "My lists", with its count, a right-click menu and room to drop books on,
    /// then "New list" (CineLibrary's MY LISTS with its +).
    /// </summary>
    private void BuildLists()
    {
        // Only the lists are replaced and "New list" stays: a group left with no items is folded away by the pane,
        // and the Collapsed event would then remember it folded as if the user had done it.
        if (_newListItem is null)
        {
            _newListItem = new NavigationViewItem
            {
                Content = "New list...", Tag = NewListTag, SelectsOnInvoked = false,
                Icon = new SymbolIcon(Symbol.Add) { Style = PaneIconStyle("PaneNotesIconStyle") }
            };
            ToolTipService.SetToolTip(_newListItem, "Make a list, then put books on it from their details or menu, or by dragging them here");
            ListsItem.MenuItems.Add(_newListItem);
        }
        while (ListsItem.MenuItems.Count > 0 && !ReferenceEquals(ListsItem.MenuItems[0], _newListItem)) ListsItem.MenuItems.RemoveAt(0);
        _listCounts.Clear();
        var at = 0;
        foreach (var name in Library.UserLists) ListsItem.MenuItems.Insert(at++, MakeListItem(name));

        RefreshCounts();
        if (Library.ListFilter.Length > 0) SelectList(Library.ListFilter);
    }

    private NavigationViewItem MakeListItem(string name)
    {
        var item = new NavigationViewItem
        {
            Content = name,
            Tag = UserListTag + name,
            Icon = new FontIcon { Glyph = ((char)0xE8FD).ToString(), Style = PaneIconStyle("PaneNotesIconStyle") }
        };
        _listCounts[name] = WithCount(item);
        ToolTipService.SetToolTip(item, name);

        var copy = new MenuFlyoutItem { Text = "Copy books to folder...", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += async (_, _) => await ListActionAsync(root => ListActions.CopyToFolderAsync(this, root, name));
        var picture = new MenuFlyoutItem { Text = "Export as image...", Icon = new FontIcon { Glyph = ((char)0xE91B).ToString() } };
        picture.Click += async (_, _) => await ListActionAsync(root => ListActions.ExportImageAsync(this, root, name));
        var rename = new MenuFlyoutItem { Text = "Rename...", Icon = new SymbolIcon(Symbol.Rename) };
        rename.Click += async (_, _) => await ListActionAsync(async root =>
        {
            var to = await ListDialogs.AskNameAsync(root, "Rename list", "Rename", name, renaming: name);
            if (to is not null && to != name) Library.RenameList(name, to);
        });
        var delete = new MenuFlyoutItem { Text = "Delete list", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await ListActionAsync(async root =>
        {
            if (await ListDialogs.ConfirmDeleteAsync(root, name, Library.ListCount(name))) Library.DeleteList(name);
        });
        item.ContextFlyout = new MenuFlyout { Items = { copy, picture, new MenuFlyoutSeparator(), rename, delete } };

        // Books dragged from the grid or the list land on the list.
        item.AllowDrop = true;
        item.DragOver += (_, e) =>
        {
            if (!BookDrag.HasBooks(e.DataView)) return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
            e.DragUIOverride.Caption = $"Add to “{name}”";
        };
        item.Drop += (_, e) =>
        {
            var books = BookDrag.Read(e.DataView);
            if (books.Count == 0) return;
            var added = Library.AddToList(books, name);
            Library.Tell(added > 0 ? $"Added {(added == 1 ? "1 book" : $"{added:N0} books")} to “{name}”"
                : books.Count == 1 ? $"“{books[0].Title}” is already on “{name}”"
                : $"Those books are already on “{name}”");
        };
        return item;
    }

    /// <summary>One list dialog at a time, like the shelves' (a ContentDialog cannot open over another).</summary>
    private async Task ListActionAsync(Func<XamlRoot, Task> action)
    {
        if (_shelfDialogOpen || Content.XamlRoot is null) return;
        _shelfDialogOpen = true;
        try
        {
            await action(Content.XamlRoot);
        }
        finally
        {
            _shelfDialogOpen = false;
        }
    }

    private Task NewListAsync() => ListActionAsync(async root =>
    {
        var name = await ListDialogs.AskNameAsync(root, "New list", "Create", "");
        if (name is not null && Library.CreateList(name)) ListsItem.IsExpanded = true;
    });

    private static void RememberListsOpen(bool open)
    {
        if (AppServices.Settings.ListsExpanded == open) return;
        AppServices.Settings.ListsExpanded = open;
        AppServices.Settings.Save();
    }

    /// <summary>Many saves come at once (a list renamed on 200 books): the counts are worked out once after them.</summary>
    private void CountsSoon() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closedDown) return;
        if (_countsTimer is null)
        {
            _countsTimer = DispatcherQueue.CreateTimer();
            _countsTimer.Interval = TimeSpan.FromMilliseconds(150);
            _countsTimer.IsRepeating = false;
            _countsTimer.Tick += (_, _) => RefreshCounts();
        }
        _countsTimer.Stop();
        _countsTimer.Start();
    });

    // ---- sidebar counts and the book being read ----

    /// <summary>The shelves that show how much is on them. Recently Added is an order, not a part, so it has none.</summary>
    private static readonly string[] CountedTags =
        { "all", "continue", "favorites", "unread", "finished", "highlights", "notes", "authors", "series", "tags", "missing", "needs" };

    private readonly Dictionary<string, TextBlock> _counts = new();
    private int _countsRun;
    private Book? _nowReading;
    private string? _nowReadingCover;

    private static Style PaneIconStyle(string key) => (Style)Application.Current.Resources[key];

    private void AddCount(NavigationViewItem item) => _counts[(string)item.Tag] = WithCount(item);

    /// <summary>The item's label with a quiet count at its right, which RefreshCounts fills in. Returns the count.</summary>
    private static TextBlock WithCount(NavigationViewItem item)
    {
        var label = (string)item.Content;
        var count = new TextBlock { Style = (Style)Application.Current.Resources["PaneCountStyle"] };
        Grid.SetColumn(count, 1);
        var content = new Grid
        {
            ColumnSpacing = 8,
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, count }
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        item.Content = content;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, label);
        return count;
    }

    /// <summary>
    /// Every shelf's count and the book being read, worked out off the window's thread (the Authors, Series and Tags
    /// indexes take about 30 ms for 2,800 books). A later refresh wins over one still running.
    /// </summary>
    private async void RefreshCounts()
    {
        // Closing during the start-up scan or sync left a refresh queued: the database is closed by then.
        if (_closedDown) return;
        var run = ++_countsRun;
        var books = Library.AllBooks.ToList();
        var (highlights, notes) = AppServices.Annotations.Counts();
        var lists = Library.ListCounts();
        var repo = AppServices.Repo;
        var (counts, reading) = await Task.Run(() =>
        {
            var n = new Dictionary<string, int>
            {
                ["all"] = books.Count,
                ["continue"] = books.Count(b => b.Status == ReadStatus.Reading),
                ["favorites"] = books.Count(b => b.IsFavorite),
                ["unread"] = books.Count(b => b.Status == ReadStatus.Unread),
                ["finished"] = books.Count(b => b.Status == ReadStatus.Finished),
                ["highlights"] = highlights,
                ["notes"] = books.Count(b => b.HasNote) + notes,
                ["authors"] = AuthorIndex.Build(books, repo.GetAllAuthorsOnline()).Count,
                ["series"] = SeriesIndex.Build(books).Count,
                ["tags"] = Tags.Build(books).Count,
                ["missing"] = repo.GetMissing().Count,
                ["needs"] = books.Count(b => b.NeedsDetails)
            };
            var now = books.Where(b => b.Status == ReadStatus.Reading && b.IsAvailable).MaxBy(b => b.LastOpenedUtc ?? DateTime.MinValue);
            return (n, now);
        });
        if (run != _countsRun) return;
        foreach (var (tag, text) in _counts) text.Text = counts[tag].ToString("N0");
        foreach (var (name, text) in _listCounts) text.Text = lists.GetValueOrDefault(name).ToString("N0");
        ShowNowReading(reading);
    }

    /// <summary>The card for the book opened last of those being read, or none.</summary>
    private void ShowNowReading(Book? book)
    {
        _nowReading = book;
        NowReadingCard.Visibility = book is not null && NavView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        if (book is null) return;
        NowReadingTitle.Text = book.Title;
        NowReadingProgress.Value = book.Progress;
        NowReadingPercent.Text = book.Progress + "%";
        ToolTipService.SetToolTip(NowReadingCard, $"Continue reading “{book.Title}”");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NowReadingCard, "Now reading: " + book.Title);
        var cover = book.CoverPath;
        if (cover == _nowReadingCover) return;
        _nowReadingCover = cover;
        NowReadingCover.Source = null;
        if (cover is not null && File.Exists(cover)) _ = CoverLoader.ShowAsync(NowReadingCover, cover, 80);
    }

    private void OnNowReading(object sender, RoutedEventArgs e)
    {
        if (_nowReading is null) return;
        var error = Library.OpenBook(_nowReading);
        if (error is not null) Library.Tell(error);
    }

    private void Navigate(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => Library.CancelScan();

    // ---- status bar ----

    private static readonly TimeSpan StatusLinger = TimeSpan.FromSeconds(8);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;

    // Not "there is an online message": the lookups end with one (Google's day used up), which kept the bar up all day.
    private bool Working => Library.IsScanning || Library.IsLookingUp;

    /// <summary>The status bar shows while a scan or an online lookup runs, and for a few seconds after any news.</summary>
    private void ShowStatus()
    {
        StatusBar.Visibility = Visibility.Visible;
        if (_statusTimer is null)
        {
            _statusTimer = DispatcherQueue.CreateTimer();
            _statusTimer.Interval = StatusLinger;
            _statusTimer.IsRepeating = false;
            _statusTimer.Tick += (_, _) =>
            {
                if (Working) return;
                StatusBar.Visibility = Visibility.Collapsed;
                Library.ForgetOnlineStatus();
            };
        }
        _statusTimer.Stop();
        if (!Working) _statusTimer.Start();
    }
}
