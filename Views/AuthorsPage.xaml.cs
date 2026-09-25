using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// One entry per author, with the names that look like the same person written differently offered for
/// merging. Merging is an edit like any other: it writes the chosen name onto those books' own details and
/// never touches the files.
/// </summary>
public sealed partial class AuthorsPage : Page
{
    private static LibraryViewModel Library => AppServices.Library;
    private List<AuthorEntry> _authors = new();

    public AuthorsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _authors = Library.GetAuthors();
        var withBooks = _authors.Sum(a => a.BookCount);
        SummaryText.Text = $"{Fn.Count(_authors.Count, "person", "people")} named on {Fn.Count(withBooks, "book")}";

        var dismissed = AppServices.Settings.NotSamePeople;
        var groups = AuthorIndex.Similar(_authors).Where(g => !dismissed.Contains(g.Key)).ToList();
        MergeList.ItemsSource = groups;
        MergeHeader.Text = groups.Count == 1
            ? "Two names look like the same person"
            : $"{groups.Count} names look like the same person";
        MergePanel.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var offers = Library.GetAuthorOffers();
        OfferList.ItemsSource = offers;
        OfferPanel.Visibility = offers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Show(SearchBox.Text ?? "");
    }

    private void Show(string query)
    {
        query = query.Trim();
        AuthorList.ItemsSource = query.Length == 0
            ? _authors
            : _authors.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        Show(sender.Text ?? "");
    }

    private void OnAuthorClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AuthorEntry a) App.MainWindow?.ShowBooksBy(a.Name);
    }

    private async void OnMerge(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AuthorVariants group) return;
        var changed = 0;
        foreach (var other in group.Members.Where(m => m.Name != group.Canonical))
            changed += Library.RenamePerson(other.Name, group.Canonical);

        Refresh();
        await new ContentDialog
        {
            Title = "Names joined",
            Content = changed == 0
                ? "Nothing to change."
                : $"{changed} book(s) now name {group.Canonical}. Undo it in a book's details, under \"Edit title, author and series\".",
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        }.ShowThemedAsync();
    }

    private async void OnSetAuthor(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AuthorOffer offer) return;
        var changed = Library.SetAuthor(offer.Books, offer.Name);
        Refresh();
        await new ContentDialog
        {
            Title = "Author set",
            Content = $"{changed} book(s) now name {offer.Name}. Undo it in a book's details, under \"Edit title, author and series\".",
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        }.ShowThemedAsync();
    }

    private void OnNotAuthor(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AuthorOffer offer) return;
        AppServices.Settings.NoAuthorOffers.Add(offer.Key);
        AppServices.Settings.Save();
        Refresh();
    }

    private void OnNotSame(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AuthorVariants group) return;
        AppServices.Settings.NotSamePeople.Add(group.Key);
        AppServices.Settings.Save();
        Refresh();
    }
}
