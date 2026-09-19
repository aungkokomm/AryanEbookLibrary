using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel => AppServices.Library;

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    private static Book? BookOf(object sender) => (sender as FrameworkElement)?.Tag as Book;

    // ---- clicks ----

    private async void OnBookClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Book book) await ShowDetailsAsync(book);
    }

    private async Task ShowDetailsAsync(Book book)
    {
        var dialog = new BookDetailsDialog(book, ViewModel) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary) await OpenAsync(book);
        else if (result == ContentDialogResult.Secondary) BookLauncher.ShowInFolder(book);
    }

    private async Task OpenAsync(Book book)
    {
        var error = ViewModel.OpenBook(book);
        if (error is null) return;

        var dialog = new ContentDialog
        {
            Title = "Cannot open this book",
            Content = error,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ---- context menu ----

    private async void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is { } b) await OpenAsync(b);
    }

    private async void OnMenuDetails(object sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is { } b) await ShowDetailsAsync(b);
    }

    private void OnMenuFavorite(object sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is { } b) ViewModel.ToggleFavorite(b);
    }

    private void OnMenuFinished(object sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is { } b) ViewModel.SetStatus(b, ReadStatus.Finished);
    }

    private void OnMenuUnread(object sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is { } b) ViewModel.SetStatus(b, ReadStatus.Unread);
    }

    private void OnMenuShowInFolder(object sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is { } b) BookLauncher.ShowInFolder(b);
    }

    // ---- toolbar ----

    private void OnToggleSortDirection(object sender, RoutedEventArgs e) =>
        ViewModel.SortDescending = !ViewModel.SortDescending;

    private void OnViewGrid(object sender, RoutedEventArgs e) => ViewModel.ViewMode = ViewMode.Grid;

    private void OnViewList(object sender, RoutedEventArgs e) => ViewModel.ViewMode = ViewMode.List;

    private async void OnSurprise(object sender, RoutedEventArgs e)
    {
        var pick = ViewModel.PickRandom();
        if (pick is null)
        {
            await new ContentDialog
            {
                Title = "Nothing to pick",
                Content = "No connected books match the current filter.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowAsync();
            return;
        }
        await ShowDetailsAsync(pick);
    }
}
