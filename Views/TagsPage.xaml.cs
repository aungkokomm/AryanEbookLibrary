using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>Every tag the user has written, with the books wearing it one click away.</summary>
public sealed partial class TagsPage : Page
{
    public TagsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var tags = AppServices.Library.GetTags();
        TagList.ItemsSource = tags;
        EmptyText.Visibility = tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var tagged = AppServices.Library.AllBooks.Count(b => b.UserTags.Length > 0);
        SummaryText.Text = tags.Count == 0
            ? "No tags yet."
            : $"{tags.Count:N0} tags on {tagged:N0} books";
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TagEntry tag) App.MainWindow?.ShowTag(tag.Name);
    }
}
