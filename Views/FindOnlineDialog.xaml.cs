using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.Services.Online;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AryanEbookLibrary.Views;

/// <summary>One Open Library result in the Find online list.</summary>
public sealed class OnlineResultItem
{
    public OnlineCandidate Candidate { get; }
    public string Title { get; }
    public string Authors { get; }
    public string Info { get; }
    public string Badge { get; }
    public Visibility BadgeVisibility { get; }
    public ImageSource? Cover { get; }

    public OnlineResultItem(OnlineCandidate c, bool best)
    {
        Candidate = c;
        Title = string.IsNullOrWhiteSpace(c.Subtitle) ? c.Title : c.Title + ": " + c.Subtitle;
        Authors = c.Authors.Count == 0 ? "Unknown author" : "by " + c.AuthorText + (c.Authors.Count > 3 ? " and others" : "");
        var info = new List<string>();
        if (c.Year is { } y) info.Add(y.ToString());
        if (!string.IsNullOrWhiteSpace(c.Publisher)) info.Add(c.Publisher);
        if (c.EditionCount > 1) info.Add($"{c.EditionCount} editions");
        Info = string.Join("  ·  ", info);
        Badge = c.ByIsbn ? "Same ISBN as this book" : best ? "Title and author match this book" : "";
        BadgeVisibility = Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (c.CoverUrl('M') is { } url) Cover = new BitmapImage { UriSource = new Uri(url), DecodePixelWidth = 108 };
    }
}

/// <summary>
/// Searches Open Library for a book and lets the user pick the right one (Calibre's "Download metadata",
/// Audiobookshelf's "Match"). The picked details are shown over the file's own; the file is not changed.
/// </summary>
public sealed partial class FindOnlineDialog : ContentDialog
{
    private readonly Book _book;
    private CancellationTokenSource? _search;

    public FindOnlineDialog(Book book)
    {
        _book = book;
        InitializeComponent();

        TitleBox.Text = OnlineMatcher.MainTitleOf(OnlineLookupService.ToLookup(book));
        AuthorBox.Text = book.Author;
        UseCoverBox.IsChecked = book.NeedsCover;
        UseCoverBox.Content = book.NeedsCover ? "Use its cover" : "Use its cover instead of the book's";

        PrimaryButtonClick += OnUse;
        Opened += (_, _) =>
        {
            AppServices.Online.Hold(true);
            _ = SearchAsync(withIsbn: true);
        };
        Closed += (_, _) =>
        {
            _search?.Cancel();
            AppServices.Online.Hold(false);
        };
    }

    private void OnSearch(object sender, RoutedEventArgs e) => _ = SearchAsync(withIsbn: false);

    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        _ = SearchAsync(withIsbn: false);
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = Results.SelectedItem is OnlineResultItem;

    private async Task SearchAsync(bool withIsbn)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var title = (TitleBox.Text ?? "").Trim();
        var author = (AuthorBox.Text ?? "").Split(',')[0].Trim();   // the first person searches best
        var book = OnlineLookupService.ToLookup(_book);
        if (title.Length == 0 && !(withIsbn && book.Isbn is not null))
        {
            StatusText.Text = "Type a title to search for.";
            return;
        }

        Results.ItemsSource = null;
        IsPrimaryButtonEnabled = false;
        SetBusy(true, "Searching Open Library...");
        OnlineCandidate? byIsbn = null;
        var found = new List<OnlineCandidate>();
        try
        {
            var client = AppServices.Online.Client;
            if (withIsbn && book.Isbn is { } isbn) byIsbn = await client.ByIsbnAsync(isbn, cts.Token);
            if (title.Length > 0)
            {
                found = await client.SearchAsync(title, author.Length > 0 ? author : null, 12, cts.Token);
                if (found.Count == 0 && author.Length > 0)
                    found = await client.SearchAsync(title, null, 12, cts.Token);   // the author may be spelled otherwise
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OnlineUnavailableException)
        {
            SetBusy(false, "Open Library didn't answer. Check the internet connection, then press Search to try again.");
            return;
        }

        // The match an automatic lookup would trust goes first and is picked already.
        var best = OnlineMatcher.Decide(book, byIsbn, found) is { Status: OnlineDetails.Found, Candidate: { } c } ? c : null;
        var ordered = new List<OnlineCandidate>();
        if (byIsbn is not null) ordered.Add(byIsbn);
        if (best is not null && !ordered.Any(o => o.WorkKey == best.WorkKey)) ordered.Add(best);
        ordered.AddRange(found.Where(f => !ordered.Any(o => o.WorkKey == f.WorkKey)));

        var items = ordered.Select(o => new OnlineResultItem(o, ReferenceEquals(o, best))).ToList();
        Results.ItemsSource = items;
        if (best is not null) Results.SelectedIndex = ordered.IndexOf(best);
        SetBusy(false, items.Count switch
        {
            0 => "No books found. Try fewer words, only the title, or no author.",
            1 => "1 book found. Pick it if it is this book.",
            _ => $"{items.Count} books found. Pick the one that is this book."
        });
    }

    private void SetBusy(bool busy, string text)
    {
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = text;
        SearchButton.IsEnabled = !busy;
    }

    /// <summary>The details show at once; the description and cover follow when Open Library sends them.</summary>
    private void OnUse(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Results.SelectedItem is not OnlineResultItem item)
        {
            args.Cancel = true;
            return;
        }
        var o = OnlineLookupService.FromCandidate(item.Candidate, OnlineDetails.Found, OnlineDetails.ByPick);
        o.UseCover = UseCoverBox.IsChecked == true;
        AppServices.Online.ApplyPicked(_book, o);
    }
}
