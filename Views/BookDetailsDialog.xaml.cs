using AryanEbookLibrary.Models;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

public sealed partial class BookDetailsDialog : ContentDialog
{
    private readonly LibraryViewModel _vm;

    public Book Book { get; }
    public string OfflineText => $"Offline: connect \"{Book.DriveLabel}\" to open this book.";
    public string PathText => Book.RelPath;

    public BookDetailsDialog(Book book, LibraryViewModel vm)
    {
        Book = book;
        _vm = vm;
        InitializeComponent();

        StatusBox.SelectedIndex = (int)book.Status;
        ProgressSlider.Value = book.Progress;
        RatingBox.Value = book.Rating > 0 ? book.Rating : -1;
        FavoriteSwitch.IsOn = book.IsFavorite;
        TagsBox.Text = book.UserTags;
        NotesBox.Text = book.Notes;
        TitleBox.Text = book.Title;
        TitleBox.PlaceholderText = book.FileTitle;
        AuthorBox.Text = book.Author;
        SeriesBox.Text = book.Series;
        if (book.CustomTitle is not null || book.CustomAuthor is not null || book.CustomSeries is not null)
        {
            // Already edited: show the fields, so "Use the file's details" is one click away.
            EditDetailsLink.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            EditPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }

        IsPrimaryButtonEnabled = book.IsAvailable;
        IsSecondaryButtonEnabled = book.IsAvailable;

        Closing += OnClosing;
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
        var customTitle = title.Length == 0 || title == Book.FileTitle ? null : title;
        var author = (AuthorBox.Text ?? "").Trim();
        var customAuthor = author == Book.FileAuthor ? null : author;
        var series = (SeriesBox.Text ?? "").Trim();
        var customSeries = series == Book.FileSeries ? null : series;
        var detailsChanged = customTitle != Book.CustomTitle || customAuthor != Book.CustomAuthor ||
                             customSeries != Book.CustomSeries;

        var changed = status != Book.Status || progress != Book.Progress || rating != Book.Rating ||
                      favorite != Book.IsFavorite || tags != Book.UserTags || notes != Book.Notes || detailsChanged;
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
        else
            _vm.SaveState(Book);

        _vm.ApplyFilter();
    }
}
