using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Every series in the library, with its books in reading order and the volumes that are missing between
/// the ones you have. Clicking a series shows exactly its books, numbered order first.
/// </summary>
public sealed partial class SeriesPage : Page
{
    private List<SeriesEntry> _series = new();

    public SeriesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _series = AppServices.Library.GetSeries();
        var books = _series.Sum(s => s.Count);
        var gaps = _series.Count(s => s.HasGaps);
        SummaryText.Text = $"{_series.Count:N0} series, {Fn.Count(books, "book")}" +
                           (gaps > 0 ? $", {gaps:N0} with a volume missing" : "");
        Show(SearchBox.Text ?? "");
    }

    private void Show(string query)
    {
        query = query.Trim();
        SeriesList.ItemsSource = query.Length == 0
            ? _series
            : _series.Where(s => s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                 s.Authors.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        Show(sender.Text ?? "");
    }

    private void OnSeriesClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SeriesEntry s) App.MainWindow?.ShowSeries(s.Name);
    }
}
