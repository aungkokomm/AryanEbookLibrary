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
using Windows.System;

namespace AryanEbookLibrary.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel => AppServices.Library;

    /// <summary>
    /// Page Down and Page Up a screen of books; Home and End (with Ctrl too) the first or last book, with the keyboard
    /// on it so Enter reads it. False for any other key. The window sends the keys nothing else wanted.
    /// </summary>
    public bool ScrollByKey(VirtualKey key)
    {
        if (ViewModel.Books.Count == 0) return false;
        switch (key)
        {
            case VirtualKey.PageDown or VirtualKey.PageUp:
                var step = MainScroller.ViewportHeight * 0.9 * (key == VirtualKey.PageDown ? 1 : -1);
                MainScroller.ChangeView(null, Math.Clamp(MainScroller.VerticalOffset + step, 0, MainScroller.ScrollableHeight), null);
                return true;
            case VirtualKey.Home:
                ScrollThenFocus(0, 0);
                return true;
            case VirtualKey.End:
                ScrollThenFocus(MainScroller.ScrollableHeight, ViewModel.Books.Count - 1);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Keys for ticking books, as in File Explorer: Ctrl+A ticks every book shown and Escape puts the ticks away; with
    /// the keyboard on a book, Space ticks it, the arrow keys move to the next book, and Shift+arrows tick the books on
    /// the way. False for any other key. The window sends the keys nothing else wanted, never a text box's.
    /// </summary>
    public bool SelectByKey(VirtualKey key, object? focused)
    {
        var vm = ViewModel;
        var shift = BookCardControl.IsDown(VirtualKey.Shift);
        if (key == VirtualKey.A && BookCardControl.IsDown(VirtualKey.Control))
        {
            if (vm.Books.Count == 0) return false;
            vm.SelectionMode = true;
            vm.SelectAllShown();
            return true;
        }
        if (key == VirtualKey.Escape && vm.SelectionMode)
        {
            vm.SelectionMode = false;
            return true;
        }

        var book = focused switch { BookCardControl c => c.Book, BookRowControl r => r.Book, _ => null };
        if (book is null) return false;
        if (key == VirtualKey.Space)
        {
            vm.SelectionMode = true;
            if (shift) vm.SelectRange(book);
            else vm.ToggleSelect(book);
            return true;
        }

        var across = vm.IsGridView ? _columns : 1;
        var step = key switch
        {
            VirtualKey.Right when vm.IsGridView => 1,
            VirtualKey.Left when vm.IsGridView => -1,
            VirtualKey.Down => across,
            VirtualKey.Up => -across,
            _ => 0
        };
        if (step == 0) return false;
        var at = vm.IndexOfShown(book);
        var to = Math.Clamp(at + step, 0, vm.Books.Count - 1);
        if (at < 0 || to == at) return true;
        if (shift)
        {
            vm.SelectionMode = true;
            vm.SelectRange(vm.Books[to], from: book);
        }
        FocusBook(to);
        return true;
    }

    /// <summary>
    /// Sorting a big library from the keyboard, as photo apps rate pictures: with the keyboard on a book, 1 to 5 give
    /// it stars and 0 takes them away, F makes it a favorite, R marks it being read and D done (each again undoes it).
    /// On a ticked book they act on every ticked book. False for any other key.
    /// </summary>
    /// <summary>Ctrl+C: the ticked books, or else the one with the keyboard on it, copied as a list to paste anywhere.</summary>
    public bool CopyByKey(VirtualKey key, object? focused)
    {
        if (key != VirtualKey.C || !BookCardControl.IsDown(VirtualKey.Control)) return false;
        var vm = ViewModel;
        var books = vm.SelectedCount > 0
            ? vm.Selected.OrderBy(b => vm.IndexOfShown(b) is var i and >= 0 ? i : int.MaxValue).ToList()
            : focused switch { BookCardControl { Book: { } c } => [c], BookRowControl { Book: { } r } => [r], _ => new List<Book>() };
        if (books.Count == 0) return false;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(CatalogExport.Lines(books));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        vm.Tell(books.Count == 1 ? $"Copied “{books[0].Title}”" : $"{books.Count:N0} books copied as a list");
        return true;
    }

    public bool MarkByKey(VirtualKey key, object? focused)
    {
        if (BookCardControl.IsDown(VirtualKey.Control) || BookCardControl.IsDown(VirtualKey.Menu) || BookCardControl.IsDown(VirtualKey.Shift))
            return false;
        var book = focused switch { BookCardControl c => c.Book, BookRowControl r => r.Book, _ => null };
        if (book is null) return false;
        var vm = ViewModel;
        var books = book.IsSelected && vm.SelectedCount > 1 ? vm.Selected : new List<Book> { book };
        var which = books.Count == 1 ? $"“{book.Title}”" : $"{books.Count:N0} books";

        int? stars = key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number5 => key - VirtualKey.Number0,
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad5 => key - VirtualKey.NumberPad0,
            _ => null
        };
        if (stars is { } n)
        {
            vm.SetRating(books, n);
            vm.Tell(n == 0 ? $"{which}: no stars" : $"{which}: {n} star{(n == 1 ? "" : "s")}");
            return true;
        }
        switch (key)
        {
            case VirtualKey.F:
                var favorite = !book.IsFavorite;
                vm.SetFavorite(books, favorite);
                vm.Tell(favorite ? $"{which}: favorite" : $"{which}: not a favorite");
                return true;
            case VirtualKey.R or VirtualKey.D:
                var wanted = key == VirtualKey.R ? ReadStatus.Reading : ReadStatus.Finished;
                var status = book.Status == wanted ? ReadStatus.Unread : wanted;
                foreach (var b in books) vm.SetStatus(b, status);
                vm.Tell($"{which}: " + status switch { ReadStatus.Reading => "reading", ReadStatus.Finished => "finished today", _ => "unread" });
                return true;
        }
        return false;
    }

    /// <summary>
    /// The book takes the keyboard once the scroll has landed: asked for before, the repeater handed back a card it
    /// then gave to another book.
    /// </summary>
    private void ScrollThenFocus(double offset, int index)
    {
        if (Math.Abs(MainScroller.VerticalOffset - offset) < 1)
        {
            FocusBook(index);
            return;
        }
        // After a long jump the repeater settles its cards over more than one pass: the keyboard waits for the last.
        void Landed(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e.IsIntermediate) return;
            MainScroller.ViewChanged -= Landed;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusBook(index));
        }
        MainScroller.ViewChanged += Landed;
        if (!MainScroller.ChangeView(null, offset, null, disableAnimation: true))
        {
            MainScroller.ViewChanged -= Landed;
            FocusBook(index);
        }
    }

    /// <summary>A book far down the list may not be made yet: the repeater makes it, then it takes the keyboard.</summary>
    private void FocusBook(int index)
    {
        // The cards for where the view now is are made first, so the one asked for is the card that stays its own.
        MainScroller.UpdateLayout();
        var repeater = ViewModel.IsGridView ? GridRepeater : ListRepeater;
        if (repeater.GetOrCreateElement(index) is not Control book) return;
        book.UpdateLayout();
        book.StartBringIntoView();
        book.Focus(FocusState.Keyboard);
    }

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
        ("Progress ↓", SortMode.Progress, true),
        ("Progress ↑", SortMode.Progress, false),
        ("Size ↓", SortMode.FileSize, true),
        ("Size ↑", SortMode.FileSize, false),
        ("Format ↑", SortMode.Format, false),
        ("Format ↓", SortMode.Format, true),
        ("Length ↑", SortMode.Length, false),
        ("Length ↓", SortMode.Length, true),
        ("Pages left ↑", SortMode.PagesLeft, false),
        ("Pages left ↓", SortMode.PagesLeft, true),
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
        BookMenu.ManyRequested += OnMany;
        // Ctrl+click, a cover's tick or a key can start ticking as well as the Select button.
        ViewModel.SelectionModeChanged += (_, _) =>
        {
            SelectionBar.Visibility = ViewModel.SelectionMode ? Visibility.Visible : Visibility.Collapsed;
            SelectButton.IsChecked = ViewModel.SelectionMode;
        };
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
            case nameof(LibraryViewModel.TotalCount):
                LayoutTopBar();   // a longer title or count may need the pills or the tools on a row of their own
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
        if (args.Element is not Button { Content: StackPanel { Children: [Border { Child: Image image }, StackPanel { Children: [_, ProgressBar bar, TextBlock why] }] } }
            || sender.ItemsSourceView.GetAt(args.Index) is not Book book) return;
        // A book up next in its series says so where a book being read shows how far it is.
        var upNext = ViewModel.UpNextWhy(book);
        why.Text = upNext ?? "";
        why.Visibility = upNext is null ? Visibility.Collapsed : Visibility.Visible;
        bar.Visibility = upNext is null ? Visibility.Visible : Visibility.Collapsed;
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
        SelectButton.IsChecked = ViewModel.SelectionMode;   // the button's own click flipped it even if nothing changed
    }

    /// <summary>The menu of several ticked books, and the bar's Add to list.</summary>
    private void OnMany(BookMenu.Many action, string? list)
    {
        var none = new RoutedEventArgs();
        switch (action)
        {
            case BookMenu.Many.Edit: OnBulkEdit(this, none); break;
            case BookMenu.Many.FinishedToday: OnBulkFinished(this, none); break;
            case BookMenu.Many.FinishedEarlier: OnBulkFinishedEarlier(this, none); break;
            case BookMenu.Many.Reading: OnBulkReading(this, none); break;
            case BookMenu.Many.Unread: OnBulkUnread(this, none); break;
            case BookMenu.Many.Favorite: OnBulkFavorite(this, none); break;
            case BookMenu.Many.NotFavorite: OnBulkUnfavorite(this, none); break;
            case BookMenu.Many.Online: OnBulkOnline(this, none); break;
            case BookMenu.Many.AddToList when list is not null: _ = AddToListAsync(list); break;
            case BookMenu.Many.NewList: _ = AddToNewListAsync(); break;
            case BookMenu.Many.CopyTo: OnBulkCopy(this, none); break;
        }
    }

    /// <summary>The ticked books' files copied to a folder (an e-reader, a USB stick); the originals stay where they are.</summary>
    private async void OnBulkCopy(object sender, RoutedEventArgs e)
    {
        if (!await HaveSelection() || _dialogOpen || XamlRoot is null || App.MainWindow is not { } window) return;
        var books = ViewModel.Selected;
        _dialogOpen = true;
        try
        {
            await ListActions.CopyBooksAsync(window, XamlRoot, books, books.Count == 1 ? "1 ticked book" : $"{books.Count:N0} ticked books");
        }
        catch (Exception ex)
        {
            Log.Write("Copying the ticked books failed: " + ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnBulkListsOpening(object? sender, object e)
    {
        if (sender is MenuFlyout flyout) BookMenu.FillListsForMany(flyout.Items);
    }

    private async Task AddToListAsync(string list)
    {
        if (!await HaveSelection()) return;
        var added = ViewModel.AddToList(ViewModel.Selected, list);
        await Say("Added to list", added == 0 ? $"Every ticked book is on {list} already." : $"{added} book(s) added to {list}.");
    }

    private async Task AddToNewListAsync()
    {
        if (!await HaveSelection() || _dialogOpen || XamlRoot is null) return;
        string? name;
        _dialogOpen = true;
        try
        {
            name = await ListDialogs.AskNameAsync(XamlRoot, "New list", "Create", "");
        }
        finally
        {
            _dialogOpen = false;
        }
        if (name is not null && ViewModel.CreateList(name)) await AddToListAsync(name.Trim());
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
        foreach (var column in ColumnsMenu.Items.OfType<ToggleMenuFlyoutItem>())
            column.IsChecked = BookRowControl.ShowsColumn((string)column.Tag);
    }

    /// <summary>A column of the list view on or off; it switches to the list, where the columns are.</summary>
    private void OnColumnToggle(object sender, RoutedEventArgs e)
    {
        var shown = ColumnsMenu.Items.OfType<ToggleMenuFlyoutItem>().Where(c => c.IsChecked).Select(c => (string)c.Tag);
        AppServices.Settings.ListColumns = string.Join(",", shown);
        ViewModel.ViewMode = ViewMode.List;
        BookRowControl.RaiseColumnsChanged();
        SyncViewMenu();
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
    private int _columns = 1;   // books in a row of the grid, for the Up and Down keys

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
            _columns = (int)columns;
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
        // One line when the title fits with its count as shown unfiltered ("8,068 books"). Deciding by the longest count a
        // filter can show ("8,068 of 8,068 books") put a maximized window on the edge, where a pixel's difference at start
        // gave two lines one time and one the next.
        var title = TitleWidth($"{ViewModel.TotalCount:N0} books");
        // 0: one line; 1: the pills under the title and tools; 2: the tools and then the pills under the title
        var mode = besidePills >= title ? 0 : besideTools >= TitleWidth(null) ? 1 : 2;
        // The title's column is as wide as the title, so it is held to the room left, where its count is trimmed.
        // On one line it is also kept as wide as its longest count where there is room, so a filter's "1 of 7 books"
        // moves no pill; where there is not, that longer count is trimmed instead (its tooltip has it all).
        HeaderTitle.MaxWidth = Math.Max(0, mode == 0 ? besidePills : mode == 1 ? besideTools : room);
        HeaderTitle.MinWidth = mode == 0
            ? Math.Min(besidePills, TitleWidth($"{ViewModel.TotalCount:N0} of {ViewModel.TotalCount:N0} books"))
            : 0;
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
    /// How wide the title is when nothing cuts it, at least 160, and with <paramref name="count"/> beside it when one is
    /// given: measured on a copy outside the page, since the one on the page only ever gets the width left over.
    /// </summary>
    private double TitleWidth(string? count)
    {
        _titleProbe.Style = HeaderTitle.Style;
        _titleProbe.Text = ViewModel.HeaderText;   // not HeaderTitle.Text, which the binding may not have updated yet
        _titleProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(160, Math.Ceiling(_titleProbe.DesiredSize.Width));
        if (count is null) return width;

        _countProbe.Style = HeaderTitle.Style;
        _countProbe.FontSize = 14;
        _countProbe.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        _countProbe.Text = count;
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
