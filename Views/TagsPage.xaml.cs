using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// The tag explorer: every tag the user has written in a list that can be searched and sorted, and the chosen
/// tag's books beside it, with Show in library, Rename and Remove. The tags the library can work out by
/// itself wait on their own tab; a suggestion is only ever written when the user says so.
/// </summary>
public sealed partial class TagsPage : Page
{
    private List<TagEntry> _tags = new();
    private List<TagSuggestion> _suggestions = new();
    private string _selected = "";
    private bool _filling;
    private bool _firstLoad = true;
    private bool _dialogOpen;

    public TagsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _tags = AppServices.Library.GetTags();
        _suggestions = AppServices.Library.GetTagSuggestions();

        var tagged = AppServices.Library.AllBooks.Count(b => b.UserTags.Length > 0);
        SummaryText.Text = _tags.Count == 0 ? "No tags yet."
            : _tags.Count == 1 ? $"1 tag on {tagged:N0} books"
            : $"{_tags.Count:N0} tags on {tagged:N0} books";
        MineTab.Text = $"Your tags ({_tags.Count:N0})";
        SuggestTab.Text = $"Suggested ({_suggestions.Count:N0})";

        ShowTagList();
        ShowSuggestions();

        if (_firstLoad)
        {
            _firstLoad = false;
            if (_tags.Count == 0 && _suggestions.Count > 0) Tabs.SelectedItem = SuggestTab;   // nothing of the user's to show yet
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (MinePanel is null || SuggestArea is null) return;   // the XAML's own first selection, before the panels exist
        var mine = sender.SelectedItem == MineTab;
        MinePanel.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        SuggestArea.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- your tags ----

    /// <summary>The tags that match the search, in the chosen order, keeping the chosen tag chosen.</summary>
    private void ShowTagList()
    {
        var none = _tags.Count == 0;
        NoTagsPanel.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        ListArea.Visibility = DetailArea.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        SeeSuggestionsButton.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (none) return;

        var words = (TagSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _tags.Where(t => words.All(w => t.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
        shown = TagSort.SelectedIndex == 1
            ? shown.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            : shown.OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase);
        var list = shown.ToList();

        _filling = true;
        TagList.ItemsSource = list;
        TagList.SelectedItem = list.FirstOrDefault(t => string.Equals(t.Name, _selected, StringComparison.CurrentCultureIgnoreCase))
                               ?? list.FirstOrDefault();
        _filling = false;
        NoMatchText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListCountText.Text = list.Count == _tags.Count ? $"{_tags.Count:N0} tags" : $"{list.Count:N0} of {_tags.Count:N0} tags";
        ShowTag(TagList.SelectedItem as TagEntry);
    }

    private void OnTagSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        ShowTag(TagList.SelectedItem as TagEntry);
    }

    private void ShowTag(TagEntry? tag)
    {
        DetailArea.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
        if (tag is null) return;
        _selected = tag.Name;
        TagTitle.Text = tag.Name;
        TagInfo.Text = tag.Finished == 0 ? tag.CountText : $"{tag.CountText}  ·  {tag.Finished:N0} finished";
        BookGrid.ItemsSource = tag.Books.OrderBy(b => b.SortTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void OnTagSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ShowTagList();

    private void OnTagSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ShowTagList();
    }

    /// <summary>Side by side when there is room; on a narrow window the list sits above the books.</summary>
    private void OnMineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var narrow = width < 640;
        ListColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Clamp(width * 0.34, 220, 300));
        DetailColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ListRow.Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        DetailRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ListArea.MaxHeight = narrow ? 260 : double.PositiveInfinity;
        Grid.SetRow(DetailArea, narrow ? 1 : 0);
        Grid.SetColumn(DetailArea, narrow ? 0 : 1);
    }

    private void OnShowInLibrary(object sender, RoutedEventArgs e)
    {
        if (_selected.Length > 0) App.MainWindow?.ShowTag(_selected);
    }

    private void OnBookClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Book book) return;
        BookDetailsWindow.Show(book, Refresh);   // its tags may have changed in its details
    }

    private async void OnRenameTag(object sender, RoutedEventArgs e)
    {
        var tag = _tags.FirstOrDefault(t => t.Name == _selected);
        if (tag is null || _dialogOpen || XamlRoot is null) return;

        var box = new TextBox { Text = tag.Name, MaxLength = 80 };
        var note = new TextBlock { Style = (Style)Application.Current.Resources["PageHintStyle"] };
        var dialog = new ContentDialog
        {
            Title = "Rename tag",
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = new StackPanel { Spacing = 8, MinWidth = 320, Children = { box, note } }
        };
        void Check()
        {
            var name = (box.Text ?? "").Trim();
            var other = _tags.FirstOrDefault(t => t != tag && string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
            note.Text = name.Contains(',') ? "A tag cannot have a comma in it: commas are what separate tags."
                : other is not null ? $"“{other.Name}” is already a tag on {other.CountText}. Renaming joins the two."
                : $"On {tag.CountText}. The files are not changed.";
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && !name.Contains(',') && name != tag.Name;
        }
        box.TextChanged += (_, _) => Check();
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        Check();

        _dialogOpen = true;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        if (result != ContentDialogResult.Primary) return;

        var to = (box.Text ?? "").Trim();
        AppServices.Library.RenameTag(tag.Name, to);
        _selected = _tags.FirstOrDefault(t => string.Equals(t.Name, to, StringComparison.CurrentCultureIgnoreCase))?.Name ?? to;
        Refresh();
    }

    private async void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        var tag = _tags.FirstOrDefault(t => t.Name == _selected);
        if (tag is null || _dialogOpen || XamlRoot is null) return;

        _dialogOpen = true;
        ContentDialogResult result;
        try
        {
            result = await new ContentDialog
            {
                Title = "Remove this tag?",
                Content = $"“{tag.Name}” comes off {tag.CountText}. The books stay in the library, and the files are not changed.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        if (result != ContentDialogResult.Primary) return;

        AppServices.Library.RemoveTag(tag.Name);
        _selected = "";
        Refresh();
    }

    private void OnSeeSuggestions(object sender, RoutedEventArgs e) => Tabs.SelectedItem = SuggestTab;

    // ---- suggested ----

    private void ShowSuggestions()
    {
        SuggestList.ItemsSource = _suggestions;
        SuggestPanel.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoSuggestText.Visibility = _suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestHeader.Text = _suggestions.Count == 1
            ? "1 tag the library worked out by itself"
            : $"{_suggestions.Count:N0} tags the library worked out by itself";
        AddAllButton.Content = $"Add all {_suggestions.Count:N0}";
    }

    private void OnAddSuggestion(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TagSuggestion suggestion) return;
        AppServices.Library.ApplyTagSuggestion(suggestion);
        Refresh();
    }

    private void OnRefuseSuggestion(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TagSuggestion suggestion) return;
        AppServices.Library.RefuseTagSuggestion(suggestion);
        Refresh();
    }

    private async void OnAddAll(object sender, RoutedEventArgs e)
    {
        if (_suggestions.Count == 0 || _dialogOpen || XamlRoot is null) return;
        var books = _suggestions.Sum(s => s.Count);
        _dialogOpen = true;
        ContentDialogResult confirm;
        try
        {
            confirm = await new ContentDialog
            {
                Title = "Add every suggested tag",
                Content = $"{_suggestions.Count:N0} tags will be written onto {books:N0} books. They are your own tags, " +
                          "so any of them can be taken off again with Remove on the Your tags tab.",
                PrimaryButtonText = "Add them all",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        if (confirm != ContentDialogResult.Primary) return;

        foreach (var suggestion in _suggestions) AppServices.Library.ApplyTagSuggestion(suggestion);
        Refresh();
    }
}
