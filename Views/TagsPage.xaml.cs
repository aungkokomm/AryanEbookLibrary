using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Every tag the user has written, with the books wearing it one click away, and the tags the library can
/// work out by itself waiting above. A suggestion is only ever written when the user says so, and any tag
/// can be taken off again by picking its books and using Edit... on the library page.
/// </summary>
public sealed partial class TagsPage : Page
{
    private List<TagSuggestion> _suggestions = new();
    private bool _hidden;

    public TagsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var tags = AppServices.Library.GetTags();
        TagList.ItemsSource = tags;

        var tagged = AppServices.Library.AllBooks.Count(b => b.UserTags.Length > 0);
        SummaryText.Text = tags.Count == 0
            ? "No tags yet."
            : $"{tags.Count:N0} tags on {tagged:N0} books";

        ShowSuggestions();
        EmptyText.Visibility = tags.Count == 0 && _suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSuggestions()
    {
        _suggestions = _hidden ? new List<TagSuggestion>() : AppServices.Library.GetTagSuggestions();
        SuggestList.ItemsSource = _suggestions;
        SuggestPanel.Visibility = _suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        if (_suggestions.Count == 0 || XamlRoot is null) return;
        var books = _suggestions.Sum(s => s.Count);
        var confirm = await new ContentDialog
        {
            Title = "Add every suggested tag",
            Content = $"{_suggestions.Count:N0} tags will be written onto {books:N0} books. They are your own tags, " +
                      "so any of them can be taken off again: open the tag, pick the books and use Edit...",
            PrimaryButtonText = "Add them all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        }.ShowAsync();
        if (confirm != ContentDialogResult.Primary) return;

        foreach (var suggestion in _suggestions) AppServices.Library.ApplyTagSuggestion(suggestion);
        Refresh();
    }

    private void OnHideSuggestions(object sender, RoutedEventArgs e)
    {
        _hidden = true;
        ShowSuggestions();
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TagEntry tag) App.MainWindow?.ShowTag(tag.Name);
    }
}
