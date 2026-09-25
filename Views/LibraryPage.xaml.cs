using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AryanEbookLibrary.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel => AppServices.Library;

    // The sort box lists key and direction together, as CineLibrary does ("Title ↑", "Title ↓").
    private static readonly (string Label, SortMode Mode, bool Descending)[] SortChoices =
    {
        ("Title ↑", SortMode.Title, false),
        ("Title ↓", SortMode.Title, true),
        ("Author ↑", SortMode.Author, false),
        ("Author ↓", SortMode.Author, true),
        ("Series ↑", SortMode.Series, false),
        ("Series ↓", SortMode.Series, true),
        ("Date added ↓", SortMode.DateAdded, true),
        ("Date added ↑", SortMode.DateAdded, false),
        ("Last opened ↓", SortMode.LastOpened, true),
        ("Last opened ↑", SortMode.LastOpened, false),
        ("Rating ↓", SortMode.Rating, true),
        ("Rating ↑", SortMode.Rating, false),
    };

    private bool _ready;
    private bool _dialogOpen;
    private bool _scrollToTopOnNextResult;

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        for (var i = 0; i < SortChoices.Length; i++)
        {
            var item = new RadioMenuFlyoutItem { Text = SortChoices[i].Label, GroupName = "Sort", Tag = i };
            item.Click += OnSortChoice;
            SortMenu.Items.Add(item);
        }
        SyncSortMenu();
        ApplyDensity(AppServices.Settings.GridDensity);
        SyncViewMenu();
        SyncFilterBar();
        SyncContinueRow();

        // One cached page instance lives for the whole session, so these are subscribed once.
        BookCardControl.DetailsRequested += b => BookDetailsWindow.Show(b);
        BookCardControl.OpenRequested += (b, defaultApp) => _ = OpenAsync(b, defaultApp);
        BookCardControl.FindOnlineRequested += b => _ = FindOnlineAsync(b);
        ViewModel.PropertyChanged += OnViewModelChanged;
        _ready = true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.Filter):
            case nameof(LibraryViewModel.FormatIndex):
            case nameof(LibraryViewModel.SearchText):
            case nameof(LibraryViewModel.SeriesFilter):
            case nameof(LibraryViewModel.TagFilter):
            case nameof(LibraryViewModel.ListFilter):
            case nameof(LibraryViewModel.LanguageFilter):
            case nameof(LibraryViewModel.DecadeFilter):
            case nameof(LibraryViewModel.PublisherFilter):
            case nameof(LibraryViewModel.MinRating):
            case nameof(LibraryViewModel.ConnectedOnly):
            case nameof(LibraryViewModel.SortIndex):
                // A new question starts at the top; an edit or toggle (which also refreshes the list) keeps the place.
                _scrollToTopOnNextResult = true;
                SyncFilterBar();
                break;
            case nameof(LibraryViewModel.Books):
                if (_scrollToTopOnNextResult)
                {
                    _scrollToTopOnNextResult = false;
                    MainScroller.ChangeView(null, 0, null, disableAnimation: true);
                }
                EmptyTitle.Text = ViewModel.TotalCount == 0 ? "Your library is empty"
                    : ViewModel.ListFilter.Length > 0 && !ViewModel.IsFiltered ? "Nothing on this list yet"
                    : ViewModel.Filter == LibraryFilter.Notes && !ViewModel.IsFiltered ? "No notes yet"
                    : "No books match";
                AddFolderButton.Visibility = ViewModel.TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(LibraryViewModel.ActiveShelf):
                SyncFilterBar();
                break;
            case nameof(LibraryViewModel.HeaderText):
                LayoutTopBar();   // a longer title may need the tools on a row of their own
                break;
            case nameof(LibraryViewModel.ContinueBooks):
                SyncContinueRow();
                break;
        }
    }

    // ---- continue reading ----

    private void SyncContinueRow() =>
        ContinueRow.Visibility = ViewModel.ContinueBooks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Book book }) _ = OpenAsync(book);
    }

    /// <summary>
    /// Each chip's cover, read from the file into memory first: the way that proved safe when thousands of covers
    /// load at start-up, where handing the image its file path now and then brought the app down.
    /// </summary>
    private async void OnContinueChipPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Button { Content: StackPanel { Children: [Border { Child: Image image }, ..] } }
            || sender.ItemsSourceView.GetAt(args.Index) is not Book book) return;
        image.Tag = book;
        image.Source = null;
        if (book.CoverPath is not { } path) return;
        try
        {
            var bytes = await Task.Run(() => File.Exists(path) ? File.ReadAllBytes(path) : null);
            if (bytes is null || !ReferenceEquals(image.Tag, book)) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var cover = new BitmapImage { DecodePixelWidth = 60 };
            await cover.SetSourceAsync(stream);
            if (ReferenceEquals(image.Tag, book)) image.Source = cover;
        }
        catch (Exception)
        {
            // an unreadable cover: the placeholder colour stays
        }
    }

    // ---- open / details ----

    private async Task FindOnlineAsync(Book book)
    {
        if (_dialogOpen || XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            await new FindOnlineDialog(book) { XamlRoot = XamlRoot }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task OpenAsync(Book book, bool withDefaultApp = false)
    {
        var error = ViewModel.OpenBook(book, withDefaultApp);
        if (error is null || _dialogOpen || XamlRoot is null) return;

        _dialogOpen = true;
        try
        {
            await new ContentDialog
            {
                Title = "Cannot open this book",
                Content = error,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---- working on several books at once ----

    private void OnSelectMode(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectionMode = !ViewModel.SelectionMode;
        SelectionBar.Visibility = ViewModel.SelectionMode ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.IsChecked = ViewModel.SelectionMode;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => ViewModel.SelectAllShown();

    private void OnAddFolderWithBooks(object sender, RoutedEventArgs e) => App.MainWindow?.AddFolderWithBooks();

    private void OnClearSelection(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    private async void OnBulkEdit(object sender, RoutedEventArgs e)
    {
        if (!await HaveSelection()) return;
        _dialogOpen = true;
        BulkEditDialog dialog;
        ContentDialogResult result;
        try
        {
            dialog = new BulkEditDialog(ViewModel.SelectedCount) { XamlRoot = XamlRoot };
            result = await dialog.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        if (result != ContentDialogResult.Primary) return;

        var changed = ViewModel.EditSelected(dialog.Author, dialog.Series, dialog.AddTags, dialog.RemoveTags);
        await Say("Books changed", changed == 0
            ? "Nothing to change."
            : $"{changed} book(s) updated. Every change can be undone in that book's own details.");
    }

    private async void OnBulkFinished(object sender, RoutedEventArgs e) => await Mark(ReadStatus.Finished, "finished today");

    private async void OnBulkFinishedEarlier(object sender, RoutedEventArgs e) =>
        await Mark(ReadStatus.Finished, "finished, with no date. They count as read, but not in any month of the reading log; " +
                                        "a book's details can give it its date", stampDate: false);

    private async void OnBulkReading(object sender, RoutedEventArgs e) => await Mark(ReadStatus.Reading, "being read");

    private async void OnBulkUnread(object sender, RoutedEventArgs e) => await Mark(ReadStatus.Unread, "unread");

    private async Task Mark(ReadStatus status, string what, bool stampDate = true)
    {
        if (!await HaveSelection()) return;
        var count = ViewModel.SetStatusForSelected(status, stampDate);
        await Say("Books marked", $"{count} book(s) marked {what}.");
    }

    private async void OnBulkFavorite(object sender, RoutedEventArgs e) => await Favorite(true);

    private async void OnBulkUnfavorite(object sender, RoutedEventArgs e) => await Favorite(false);

    private async Task Favorite(bool favorite)
    {
        if (!await HaveSelection()) return;
        var count = ViewModel.SetFavoriteForSelected(favorite);
        await Say("Favorites", count == 0 ? "Nothing to change." : $"{count} book(s) {(favorite ? "added to" : "removed from")} favorites.");
    }

    private async void OnBulkOnline(object sender, RoutedEventArgs e)
    {
        if (!await HaveSelection()) return;
        var count = ViewModel.LookUpSelectedOnline();
        await Say("Looking online", $"{count} book(s) are being looked up on Open Library, Wikidata and Wikipedia. " +
                                    "The status bar shows how it goes, and details appear as answers arrive.");
    }

    private async Task<bool> HaveSelection()
    {
        if (ViewModel.SelectedCount > 0) return true;
        await Say("Nothing selected", "Tick a few books first.");
        return false;
    }

    private async Task Say(string title, string message)
    {
        if (_dialogOpen || XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            await new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---- sort ----

    /// <summary>The Sort button names the order in use, and its menu ticks it.</summary>
    private void SyncSortMenu()
    {
        var mode = (SortMode)ViewModel.SortIndex;
        var index = Math.Max(0, Array.FindIndex(SortChoices, c => c.Mode == mode && c.Descending == ViewModel.SortDescending));
        ((RadioMenuFlyoutItem)SortMenu.Items[index]).IsChecked = true;
        SortText.Text = SortChoices[index].Label;
    }

    private void OnSortChoice(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioMenuFlyoutItem { Tag: int index }) return;
        var choice = SortChoices[index];
        ViewModel.SetSort(choice.Mode, choice.Descending);
        SyncSortMenu();
    }

    // ---- format pills ----

    private void OnFormatPill(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index)) ViewModel.FormatIndex = index;
    }

    // The title bar's search box follows the view model, so clearing the search here clears it there too.
    private void OnClearFilters(object sender, RoutedEventArgs e) => ViewModel.ClearFilters();

    private void SyncFilterBar()
    {
        Button[] pills = { PillAll, PillEpub, PillPdf, PillKindle, PillComics };
        for (var i = 0; i < pills.Length; i++)
            pills[i].Style = Pill(i == ViewModel.FormatIndex);

        var menu = ViewModel.MenuFilterCount;
        FiltersBtn.Style = Pill(menu > 0);
        FiltersText.Text = menu > 0 ? $"Filters · {menu}" : "Filters";

        var filtered = ViewModel.IsFiltered;
        FilterActions.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersBtn.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        // A shelf is a view of the whole library, so one of My lists cannot be saved as one.
        SaveShelfBtn.Visibility = filtered && ViewModel.ActiveShelf.Length == 0 && ViewModel.ListFilter.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        // "Filters · 2" is wider than "Filters", so the pills may no longer fit beside the title; asked once measured.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, LayoutTopBar);

        static Style Pill(bool on) => (Style)Application.Current.Resources[on ? "PillButtonActiveStyle" : "PillButtonStyle"];
    }

    // ---- the Filters menu ----

    private bool _fillingFacets;

    private void OnFiltersOpening(object? sender, object e) => FillFacets(null);

    /// <summary>
    /// Fills the menu's lists with what the view can show, each choice counted with the other filters on.
    /// The list the user just used is left alone: refilling it would snap its selection back mid-change.
    /// </summary>
    private void FillFacets(ComboBox? skip)
    {
        _fillingFacets = true;
        try
        {
            Fill(LanguageBox, ViewModel.LanguageOptions(), ViewModel.LanguageFilter);
            Fill(DecadeBox, ViewModel.DecadeOptions(), ViewModel.DecadeFilter);
            Fill(PublisherBox, ViewModel.PublisherOptions(), ViewModel.PublisherFilter);
            Fill(RatingBox, ViewModel.RatingOptions(), ViewModel.MinRating.ToString());
            ConnectedSwitch.IsOn = ViewModel.ConnectedOnly;
        }
        finally
        {
            _fillingFacets = false;
        }

        void Fill(ComboBox box, List<FacetOption> options, string chosen)
        {
            if (box == skip) return;
            box.ItemsSource = options;
            var at = options.FindIndex(o => string.Equals(o.Key, chosen, StringComparison.CurrentCultureIgnoreCase));
            box.SelectedIndex = Math.Max(0, at);
        }
    }

    private void OnFacetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFacets || sender is not ComboBox { SelectedItem: FacetOption option } box) return;
        if (box == LanguageBox) ViewModel.LanguageFilter = option.Key;
        else if (box == DecadeBox) ViewModel.DecadeFilter = option.Key;
        else if (box == PublisherBox) ViewModel.PublisherFilter = option.Key;
        else if (box == RatingBox) ViewModel.MinRating = int.TryParse(option.Key, out var stars) ? stars : 0;
        FillFacets(box);
    }

    private void OnConnectedToggled(object sender, RoutedEventArgs e)
    {
        if (_fillingFacets) return;
        ViewModel.ConnectedOnly = ConnectedSwitch.IsOn;
        FillFacets(null);
    }

    private void OnClearMenuFilters(object sender, RoutedEventArgs e)
    {
        ViewModel.LanguageFilter = "";
        ViewModel.DecadeFilter = "";
        ViewModel.PublisherFilter = "";
        ViewModel.MinRating = 0;
        ViewModel.ConnectedOnly = false;
        FillFacets(null);
    }

    // ---- shelves ----

    private async void OnSaveShelf(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || XamlRoot is null) return;
        _dialogOpen = true;
        string? name;
        try
        {
            name = await ShelfDialogs.AskNameAsync(XamlRoot, "Save as shelf", "Save",
                ShelfFilter.SuggestName(ViewModel.CurrentView()));
        }
        finally
        {
            _dialogOpen = false;
        }
        if (name is not null) ViewModel.SaveShelf(name);
    }

    // ---- view mode + card size ----

    /// <summary>The View menu: a cover size shows the grid at that size, or the list.</summary>
    private void OnViewChoice(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag }) return;
        if (tag == "List")
        {
            ViewModel.ViewMode = ViewMode.List;
        }
        else
        {
            ApplyDensity(tag);
            AppServices.Settings.GridDensity = tag;
            ViewModel.ViewMode = ViewMode.Grid;
        }
        SyncViewMenu();
    }

    /// <summary>The View button shows the grid or list icon, and its menu ticks the view in use.</summary>
    private void SyncViewMenu()
    {
        var density = AppServices.Settings.GridDensity;
        ViewIcon.Glyph = ((char)(ViewModel.IsListView ? 0xE8FD : 0xE8A9)).ToString();
        var item = ViewModel.IsListView ? ViewList
            : density switch { "S" => ViewS, "L" => ViewL, "XL" => ViewXL, _ => ViewM };
        item.IsChecked = true;
    }

    /// <summary>Base card sizes. Close to a book cover's 2:3, a little taller so the title strip hides less of it.</summary>
    private void ApplyDensity(string tag)
    {
        (_baseWidth, _baseHeight) = tag switch
        {
            "S" => (120.0, 196.0),
            "L" => (190.0, 300.0),
            "XL" => (240.0, 376.0),
            _ => (150.0, 240.0)
        };
        FitCards();
    }

    private double _baseWidth = 150, _baseHeight = 240;

    private void OnGridAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width) FitCards();
    }

    /// <summary>
    /// The column count nearest to the chosen card size, then the cards are resized (keeping their shape) so
    /// the row fills the width exactly with 12 px gaps. Fixed-size cards with spread-out gaps looked loose at
    /// many widths; rounding (not flooring) keeps cards within about 15% of the chosen S/M/L/XL size.
    /// </summary>
    private void FitCards()
    {
        const double gap = 12;   // GridLayout.MinColumnSpacing
        var available = GridArea.ActualWidth - GridArea.Padding.Left - GridArea.Padding.Right;
        double width = _baseWidth, height = _baseHeight;
        if (available > 0)
        {
            var columns = Math.Max(1, Math.Round((available + gap) / (_baseWidth + gap), MidpointRounding.AwayFromZero));
            width = Math.Floor((available - (columns - 1) * gap) / columns);
            height = Math.Round(width * _baseHeight / _baseWidth);
        }
        BookCardControl.SetGlobalSize(width, height, _baseWidth);
        GridLayout.MinItemWidth = width;
        GridLayout.MinItemHeight = height;
    }

    private void OnTopBarSizeChanged(object sender, SizeChangedEventArgs e) => LayoutTopBar();

    private const double PillsGap = 24, ToolsGap = 16;

    /// <summary>
    /// The title, the pills and the tools on one line when they fit. Otherwise the pills get a line of their own
    /// under the title, and on a narrow page the tools do too, instead of squeezing the title away. Decided from
    /// natural widths only, never from what the last layout left over, so it cannot flip back and forth.
    /// </summary>
    private void LayoutTopBar()
    {
        var tools = ToolsPanel.DesiredSize.Width;
        var pills = FilterPanel.DesiredSize.Width - FilterPanel.Padding.Left - FilterPanel.Padding.Right;
        if (tools <= 0 || pills <= 0 || TopBar.ActualWidth <= 0) return;
        var room = TopBar.ActualWidth - TopBar.Padding.Left - TopBar.Padding.Right;

        var besidePills = room - PillsGap - pills - ToolsGap - tools;
        var besideTools = room - ToolsGap - tools;
        var title = TitleWidth(withCount: true);
        // 0: one line; 1: the pills under the title and tools; 2: the tools and then the pills under the title
        var mode = besidePills >= title ? 0 : besideTools >= TitleWidth(withCount: false) ? 1 : 2;
        // The title's column is as wide as the title, so it is held to the room left, where its count is trimmed.
        // On one line it is also kept as wide as its longest count, so a filter's "1 of 7 books" moves no pill.
        HeaderTitle.MaxWidth = Math.Max(0, mode == 0 ? besidePills : mode == 1 ? besideTools : room);
        HeaderTitle.MinWidth = mode == 0 ? title : 0;
        if (mode == _topBarMode) return;
        _topBarMode = mode;

        Grid.SetRow(FilterBar, mode == 0 ? 0 : mode);
        Grid.SetColumn(FilterBar, mode == 0 ? 1 : 0);
        Grid.SetColumnSpan(FilterBar, mode == 0 ? 1 : 3);
        // On its own line it reaches the page's edges, so the pills scroll under them rather than inside the margin.
        FilterBar.Margin = mode == 0 ? new Thickness(PillsGap, 0, ToolsGap, 0) : new Thickness(-28, 8, -28, 0);
        FilterPanel.Padding = mode == 0 ? new Thickness(0) : new Thickness(28, 0, 28, 0);
        Grid.SetRow(FilterActions, mode + 1);
        Grid.SetColumn(FilterActions, mode == 0 ? 1 : 0);
        Grid.SetColumnSpan(FilterActions, mode == 0 ? 1 : 3);
        FilterActions.Margin = new Thickness(mode == 0 ? PillsGap : 0, 8, 0, 0);

        Grid.SetRow(ToolsPanel, mode == 2 ? 1 : 0);
        Grid.SetColumn(ToolsPanel, mode == 2 ? 0 : 2);
        Grid.SetColumnSpan(ToolsPanel, mode == 2 ? 3 : 1);
        ToolsPanel.HorizontalAlignment = mode == 2 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ToolsPanel.Margin = mode == 2 ? new Thickness(-8, 8, 0, 0) : new Thickness(0);
    }

    private int _topBarMode = -1;
    private readonly TextBlock _titleProbe = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _countProbe = new() { TextWrapping = TextWrapping.NoWrap };

    /// <summary>
    /// How wide the title is when nothing cuts it, at least 160: measured on a copy outside the page, since
    /// the one on the page only ever gets the width left over. With its count, room is kept for the longest
    /// count ("2,812 of 2,812 books") rather than the one shown, so choosing a filter does not rearrange the bar;
    /// the filters named after it are what gets trimmed.
    /// </summary>
    private double TitleWidth(bool withCount)
    {
        _titleProbe.Style = HeaderTitle.Style;
        _titleProbe.Text = ViewModel.HeaderText;   // not HeaderTitle.Text, which the binding may not have updated yet
        _titleProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(160, Math.Ceiling(_titleProbe.DesiredSize.Width));
        if (!withCount) return width;

        _countProbe.Style = HeaderTitle.Style;
        _countProbe.FontSize = 14;
        _countProbe.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        _countProbe.Text = $"{ViewModel.TotalCount:N0} of {ViewModel.TotalCount:N0} books";
        _countProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(_titleProbe.DesiredSize.Width + 8 + _countProbe.DesiredSize.Width);
    }

    // ---- surprise ----

    private async void OnSurprise(object sender, RoutedEventArgs e)
    {
        var pick = ViewModel.PickRandom();
        if (pick is not null)
        {
            BookDetailsWindow.Show(pick);
            return;
        }
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            await new ContentDialog
            {
                Title = "Nothing to pick",
                Content = "No connected books match the current filter.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
