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
        InitializeComponent();

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
        AppWindow.Changed += (_, e) => { if (e.DidSizeChange) SyncTitleBar(); };
        AppTitleBar.Loaded += (_, _) => SyncTitleBar();
        Library.PropertyChanged += OnLibraryChanged;
        Library.ShelvesChanged += (_, _) => BuildShelves();
        BuildShelves();
        ShelvesItem.IsExpanded = AppServices.Settings.ShelvesExpanded;
        NavView.Expanding += (_, e) => { if (e.ExpandingItemContainer == ShelvesItem) RememberShelvesOpen(true); };
        NavView.Collapsed += (_, e) => { if (e.CollapsedItemContainer == ShelvesItem) RememberShelvesOpen(false); };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));

        // Event-driven drive detection (WM_DEVICECHANGE) instead of a poll timer, as in CineLibrary.
        // Don't await inside the WndProc: it must return promptly.
        _deviceWatcher = new DeviceChangeWatcher(WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => _ = Library.OnDeviceChangeAsync());

        Closed += (_, _) =>
        {
            Log.Write("app: closing");
            _deviceWatcher.Dispose();
            // A scan still running would go on writing to the database after it closes.
            Library.CancelScan();
            // The readers first: each saves its place and reading time, and the database closes after.
            Reader.ReaderWindow.CloseAll();
            AppServices.Shutdown();
        };

        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(LibraryPage));
        // The search box must not be what the app opens with a caret in.
        // Pointer focus, not Programmatic: it keeps the search box from taking focus at startup without
        // painting a focus rectangle on the pane button.
        RootGrid.Loaded += (_, _) => NavView.Focus(FocusState.Pointer);
        Library.Initialize();
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

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        var invoked = args.InvokedItemContainer?.Tag as string ?? "";
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

    private void ShowLibrary(LibraryFilter filter)
    {
        LeaveShelfView();
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.Filter = filter;
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Going anywhere else from a shelf drops what the shelf set, or its filters would follow along.</summary>
    private void LeaveShelfView()
    {
        if (Library.ActiveShelf.Length > 0) Library.ClearFilters();
    }

    /// <summary>Shows every book naming this person (from the Authors page).</summary>
    public void ShowBooksBy(string author)
    {
        LeaveShelfView();
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.Filter = LibraryFilter.All;
        Library.SearchText = author;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Shows one series, its numbered books in order (from the Series page).</summary>
    public void ShowSeries(string series)
    {
        LeaveShelfView();
        Library.Filter = LibraryFilter.All;
        Library.SearchText = "";
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
        Library.TagFilter = tag;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    private void SelectNav(string tag) =>
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
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
                Icon = new FontIcon { Glyph = ((char)0xE71C).ToString() }   // the Filters button's funnel
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

    private void Navigate(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => Library.CancelScan();
}
