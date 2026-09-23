using System.ComponentModel;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

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

        foreach (var choice in SortChoices) SortCombo.Items.Add(choice.Label);
        SyncSortCombo();
        ApplyDensity(AppServices.Settings.GridDensity);
        SyncViewToggles();
        SyncFilterBar();

        // One cached page instance lives for the whole session, so these are subscribed once.
        BookCardControl.DetailsRequested += b => _ = ShowDetailsAsync(b);
        BookCardControl.OpenRequested += b => _ = OpenAsync(b);
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
                EmptyTitle.Text = ViewModel.TotalCount == 0 ? "Your library is empty" : "No books match";
                break;
            case nameof(LibraryViewModel.ActiveShelf):
                SyncFilterBar();
                break;
            case nameof(LibraryViewModel.HeaderText):
                LayoutTopBar();   // a longer title may need the tools on a row of their own
                break;
        }
    }

    // ---- open / details ----

    private async Task ShowDetailsAsync(Book book)
    {
        if (_dialogOpen || XamlRoot is null) return;   // only one ContentDialog can be open at a time
        _dialogOpen = true;
        ContentDialogResult result;
        BookDetailsDialog dialog;
        try
        {
            dialog = new BookDetailsDialog(book, ViewModel) { XamlRoot = XamlRoot };
            result = await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }

        switch (dialog.Next)
        {
            case DetailsNext.FindOnline:   // one ContentDialog at a time: details close, the finder opens, details return
                await FindOnlineAsync(book);
                await ShowDetailsAsync(book);
                return;
            case DetailsNext.Reopen:
                await ShowDetailsAsync(book);
                return;
        }

        if (result == ContentDialogResult.Primary) await OpenAsync(book);
        else if (result == ContentDialogResult.Secondary) BookLauncher.ShowInFolder(book);
    }

    private async Task FindOnlineAsync(Book book)
    {
        if (_dialogOpen || XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            await new FindOnlineDialog(book) { XamlRoot = XamlRoot }.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task OpenAsync(Book book)
    {
        var error = ViewModel.OpenBook(book);
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
            }.ShowAsync();
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
        SelectButton.Content = ViewModel.SelectionMode ? "Done" : "Select";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => ViewModel.SelectAllShown();

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
            result = await dialog.ShowAsync();
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
            await new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---- sort ----

    private void SyncSortCombo()
    {
        var mode = (SortMode)ViewModel.SortIndex;
        var index = Array.FindIndex(SortChoices, c => c.Mode == mode && c.Descending == ViewModel.SortDescending);
        SortCombo.SelectedIndex = Math.Max(0, index);
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SortCombo.SelectedIndex < 0) return;
        var choice = SortChoices[SortCombo.SelectedIndex];
        ViewModel.SetSort(choice.Mode, choice.Descending);
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
        ClearFiltersBtn.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        SaveShelfBtn.Visibility = filtered && ViewModel.ActiveShelf.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    private void OnViewGrid(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = ViewMode.Grid;
        SyncViewToggles();
    }

    private void OnViewList(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewMode = ViewMode.List;
        SyncViewToggles();
    }

    private void SyncViewToggles()
    {
        GridViewToggle.IsChecked = ViewModel.IsGridView;
        ListViewToggle.IsChecked = ViewModel.IsListView;
        LayoutTopBar();   // the card sizes show only in grid view, and only when there is room for them
    }

    private void OnDensityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        ApplyDensity(tag);
        AppServices.Settings.GridDensity = tag;
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
        DensityS.IsChecked = tag == "S";
        DensityM.IsChecked = tag is not ("S" or "L" or "XL");
        DensityL.IsChecked = tag == "L";
        DensityXL.IsChecked = tag == "XL";
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

    // The S/M/L/XL group and the gap before it, as laid out in the XAML: four 36 px toggles, a 1 px border
    // each side, 12 px spacing.
    private const double DensityWidth = 4 * 36 + 2 + 12;

    /// <summary>
    /// Narrow page: the tools get their own row under the title instead of squeezing it away, and when
    /// even that is too tight the card sizes go (the least needed of them). The width the tools want is
    /// counted with the card sizes showing (in grid view) whether or not they show now, so the decision
    /// cannot flip back and forth.
    /// </summary>
    private void LayoutTopBar()
    {
        var measured = ToolsPanel.DesiredSize.Width;
        if (measured <= 0 || TopBar.ActualWidth <= 0)
        {
            DensityGroup.Visibility = ViewModel.IsGridView ? Visibility.Visible : Visibility.Collapsed;   // before the first layout
            return;
        }
        var wanted = measured + (ViewModel.IsGridView && DensityGroup.Visibility != Visibility.Visible ? DensityWidth : 0);
        var room = TopBar.ActualWidth - TopBar.Padding.Left - TopBar.Padding.Right;

        var stacked = room - wanted - 16 < TitleWidth();    // the title would be cut beside them
        if (stacked != _toolsStacked)
        {
            _toolsStacked = stacked;
            Grid.SetRow(ToolsPanel, stacked ? 1 : 0);
            Grid.SetColumn(ToolsPanel, stacked ? 0 : 1);
            Grid.SetColumnSpan(ToolsPanel, stacked ? 2 : 1);
            ToolsPanel.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            ToolsPanel.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        }

        var show = ViewModel.IsGridView && room >= wanted;   // not even on a row of their own: they go
        DensityGroup.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _toolsStacked;
    private readonly TextBlock _titleProbe = new() { TextWrapping = TextWrapping.NoWrap };

    /// <summary>
    /// How wide the title is when nothing cuts it, at least 160: measured on a copy outside the page, since
    /// the one on the page only ever gets the width left over.
    /// </summary>
    private double TitleWidth()
    {
        _titleProbe.Style = HeaderTitle.Style;
        _titleProbe.Text = ViewModel.HeaderText;   // not HeaderTitle.Text, which the binding may not have updated yet
        _titleProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(160, Math.Ceiling(_titleProbe.DesiredSize.Width));
    }

    // ---- surprise ----

    private async void OnSurprise(object sender, RoutedEventArgs e)
    {
        var pick = ViewModel.PickRandom();
        if (pick is not null)
        {
            await ShowDetailsAsync(pick);
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
            }.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
