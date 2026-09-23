using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>What the library page does after the details dialog closes.</summary>
public enum DetailsNext { None, FindOnline, Reopen }

public sealed partial class BookDetailsDialog : ContentDialog
{
    private readonly LibraryViewModel _vm;
    // The shown title and author before any edit, as the dialog opened: typing them back means "no edit",
    // even if Open Library details are added or removed while the dialog is open.
    private readonly string _openBaseTitle;
    private readonly string _openBaseAuthor;

    public DetailsNext Next { get; private set; }

    public Book Book { get; }
    public string OfflineText => $"Offline: connect \"{Book.DriveLabel}\" to open this book.";
    public string PathText => Book.RelPath;

    public BookDetailsDialog(Book book, LibraryViewModel vm)
    {
        Book = book;
        _vm = vm;
        InitializeComponent();

        FinishedPicker.MaxDate = DateTimeOffset.Now;
        if (book.FinishedUtc is { } finished) FinishedPicker.Date = new DateTimeOffset(finished.ToLocalTime().Date);
        StatusBox.SelectedIndex = (int)book.Status;
        ProgressSlider.Value = book.Progress;
        RatingBox.Value = book.Rating > 0 ? book.Rating : -1;
        FavoriteSwitch.IsOn = book.IsFavorite;
        TagsBox.Text = book.UserTags;
        NotesBox.Text = book.Notes;
        _openBaseTitle = book.BaseTitle;
        _openBaseAuthor = book.BaseAuthor;
        TitleBox.Text = book.Title;
        TitleBox.PlaceholderText = book.BaseTitle;
        AuthorBox.Text = book.Author;
        SeriesBox.Text = book.Series;
        if (book.CustomTitle is not null || book.CustomAuthor is not null || book.CustomSeries is not null)
        {
            // Already edited: show the fields, so "Use the file's details" is one click away.
            EditDetailsLink.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            EditPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }

        ShowOnlineState();

        IsPrimaryButtonEnabled = book.IsAvailable;
        IsSecondaryButtonEnabled = book.IsAvailable;

        Closing += OnClosing;
        // Focus starts on Open, not on the first link: Enter must never remove details or open the editor.
        Opened += (_, _) => (GetTemplateChild("PrimaryButton") as Control)?.Focus(FocusState.Programmatic);
    }

    /// <summary>The date shows for a finished book; one being finished now starts at today.</summary>
    private void OnStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        var finished = StatusBox.SelectedIndex == (int)ReadStatus.Finished;
        FinishedPicker.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        if (finished && Book.Status != ReadStatus.Finished && FinishedPicker.Date is null)
            FinishedPicker.Date = new DateTimeOffset(DateTime.Today);
    }

    /// <summary>
    /// The finish date the dialog asks for, and whether it differs from the book's. Only the day counts: an
    /// untouched picker keeps the stored time, and a picked day is stored at noon so it stays that day.
    /// </summary>
    private (DateTime? Utc, bool Changed) PickedFinish(ReadStatus status)
    {
        if (status != ReadStatus.Finished) return (Book.FinishedUtc, false);
        var picked = FinishedPicker.Date?.Date;
        var had = Book.Status == ReadStatus.Finished ? Book.FinishedUtc?.ToLocalTime().Date : null;
        if (picked == had) return (Book.FinishedUtc, false);
        return (picked is { } day ? DateTime.SpecifyKind(day.AddHours(12), DateTimeKind.Local).ToUniversalTime() : null, true);
    }

    private void OnEditDetails(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        EditDetailsLink.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        EditPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        TitleBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnUseFileDetails(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TitleBox.Text = Book.FileTitle;
        AuthorBox.Text = Book.FileAuthor;
        SeriesBox.Text = Book.FileSeries;
    }

    // ---- Open Library ----

    private void ShowOnlineState()
    {
        var o = Book.Online;
        if (o is { Status: OnlineDetails.Suggested })
        {
            var by = string.IsNullOrWhiteSpace(o.Author) ? "" : " by " + o.Author;
            var year = o.Year is { } y ? $" ({y})" : "";
            SuggestionText.Text = $"“{o.Title}”{by}{year}" +
                                  (o.How == OnlineDetails.ByIsbn ? ", found by the ISBN printed in the book." : ".");
            SuggestionBar.IsOpen = true;
        }
        // What came from where: Open Library's record, plus anything Wikidata or Wikipedia filled in.
        var from = new List<string>();
        if (o is { IsApplied: true })
            from.Add(o.How switch
            {
                OnlineDetails.ByIsbn => "Open Library, found by the ISBN in the book",
                OnlineDetails.ByMatch => "Open Library, matched by title and author",
                _ => "Open Library, chosen by you"
            });
        if (Book.DescriptionSource is { } d && d != OnlineSource.OpenLibrary) from.Add("the description from " + OnlineSource.Name(d));
        if (Book.SeriesSource is { } s) from.Add("the series from " + OnlineSource.Name(s));
        if (Book.CoverSource is { } c && c != OnlineSource.OpenLibrary) from.Add("the cover from " + OnlineSource.Name(c));
        if (Book.YearSource is { } y2 && from.Count == 0) from.Add("the year from " + OnlineSource.Name(y2));

        if (from.Count > 0)
        {
            OnlineNoteText.Text = "Details from " + Join(from) + ".";
            OnlineNote.Visibility = Visibility.Visible;
        }
    }

    private static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private void OnFindOnline(object sender, RoutedEventArgs e)
    {
        Next = DetailsNext.FindOnline;
        Hide();
    }

    /// <summary>The suggestion is this book: fetch its description and cover and show it as picked.</summary>
    private void OnUseSuggestion(object sender, RoutedEventArgs e)
    {
        if (Book.Online is not { Status: OnlineDetails.Suggested } s) return;
        var o = s.Copy();
        o.Status = OnlineDetails.Found;
        o.How = OnlineDetails.ByPick;
        o.UseCover = Book.NeedsCover;
        AppServices.Online.ApplyPicked(Book, o);   // the description and cover follow when they arrive
        Next = DetailsNext.Reopen;                 // show the new details
        Hide();
    }

    private void OnRejectSuggestion(object sender, RoutedEventArgs e)
    {
        var o = new OnlineDetails { Status = OnlineDetails.None };   // remembered, so it is not suggested again
        AppServices.Repo.UpsertOnline(Book.StateKey, o);
        Book.SetOnline(o);
        SuggestionBar.IsOpen = false;
    }

    /// <summary>Drops every online detail for this book, and remembers not to look it up again by itself.</summary>
    private void OnRemoveOnline(object sender, RoutedEventArgs e)
    {
        foreach (var source in new[] { OnlineSource.OpenLibrary, OnlineSource.Wikidata, OnlineSource.Wikipedia })
        {
            var o = new OnlineDetails { Source = source, Status = OnlineDetails.None };
            AppServices.Repo.UpsertOnline(Book.StateKey, o);
            Book.SetOnline(o);
        }
        Next = DetailsNext.Reopen;
        Hide();
    }

    /// <summary>Saves the personal state when the dialog closes (whichever button was pressed).</summary>
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        var status = (ReadStatus)Math.Max(0, StatusBox.SelectedIndex);
        var progress = (int)ProgressSlider.Value;
        var rating = (int)Math.Max(0, RatingBox.Value);
        var favorite = FavoriteSwitch.IsOn;
        var tags = (TagsBox.Text ?? "").Trim();
        var notes = NotesBox.Text ?? "";

        // An edit equal to the file's value is no edit (null), so a later rescan's better value shows through.
        // An empty title is not allowed, an empty author or series means "none".
        var title = (TitleBox.Text ?? "").Trim();
        var customTitle = title.Length == 0 || title == _openBaseTitle ? null : title;
        var author = (AuthorBox.Text ?? "").Trim();
        var customAuthor = author == _openBaseAuthor ? null : author;
        var series = (SeriesBox.Text ?? "").Trim();
        var customSeries = series == Book.FileSeries ? null : series;
        var detailsChanged = customTitle != Book.CustomTitle || customAuthor != Book.CustomAuthor ||
                             customSeries != Book.CustomSeries;

        var (finishedUtc, finishedChanged) = PickedFinish(status);

        var changed = status != Book.Status || progress != Book.Progress || rating != Book.Rating ||
                      favorite != Book.IsFavorite || tags != Book.UserTags || notes != Book.Notes || detailsChanged ||
                      finishedChanged;
        if (!changed) return;

        var statusChanged = status != Book.Status;
        Book.Progress = progress;
        Book.Rating = rating;
        Book.IsFavorite = favorite;
        Book.UserTags = tags;
        Book.Notes = notes;
        if (detailsChanged) Book.SetCustomDetails(customTitle, customAuthor, customSeries);

        if (statusChanged)
            _vm.SetStatus(Book, status);   // also stamps finished date and saves
        if (finishedChanged)
            Book.FinishedUtc = finishedUtc;   // the day the user gave (or none), over SetStatus's "now"
        if (!statusChanged || finishedChanged)
            _vm.SaveState(Book);

        _vm.ApplyFilter();
    }
}
