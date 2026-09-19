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

        IsPrimaryButtonEnabled = book.IsAvailable;
        IsSecondaryButtonEnabled = book.IsAvailable;

        Closing += OnClosing;
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

        var changed = status != Book.Status || progress != Book.Progress || rating != Book.Rating ||
                      favorite != Book.IsFavorite || tags != Book.UserTags || notes != Book.Notes;
        if (!changed) return;

        var statusChanged = status != Book.Status;
        Book.Progress = progress;
        Book.Rating = rating;
        Book.IsFavorite = favorite;
        Book.UserTags = tags;
        Book.Notes = notes;

        if (statusChanged)
            _vm.SetStatus(Book, status);   // also stamps finished date and saves
        else
            _vm.SaveState(Book);

        _vm.ApplyFilter();
    }
}
