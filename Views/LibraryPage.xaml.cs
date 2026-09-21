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

    // ---- search ----

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ViewModel.SearchText = sender.Text;
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Text = "";
        ViewModel.SearchText = "";
        e.Handled = true;
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

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ViewModel.SearchText = "";
        ViewModel.FormatIndex = 0;
    }

    private void SyncFilterBar()
    {
        Button[] pills = { PillAll, PillEpub, PillPdf, PillKindle, PillComics };
        for (var i = 0; i < pills.Length; i++)
            pills[i].Style = (Style)Application.Current.Resources[i == ViewModel.FormatIndex ? "PillButtonActiveStyle" : "PillButtonStyle"];

        ClearFiltersBtn.Visibility = ViewModel.FormatIndex > 0 || ViewModel.SearchText.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        DensityGroup.Visibility = ViewModel.IsGridView ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Narrow page: the search box gets its own row under the title instead of being squeezed.</summary>
    private void OnTopBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 1000;
        Grid.SetRow(SearchBox, narrow ? 1 : 0);
        Grid.SetColumn(SearchBox, narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, narrow ? 3 : 1);
        SearchBox.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(20, 0, 20, 0);
        SearchBox.MaxWidth = narrow ? double.PositiveInfinity : 520;
        TitleColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SearchColumn.Width = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
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
