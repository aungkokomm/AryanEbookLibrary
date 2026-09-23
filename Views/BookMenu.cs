using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Right-click menu for a book card or list row, rebuilt each time it opens so the ticks show the
/// current state (as CineLibrary's card menu does).
/// </summary>
internal static class BookMenu
{
    public static void Build(MenuFlyout flyout, Book? book)
    {
        flyout.Items.Clear();
        if (book is null) return;
        var vm = AppServices.Library;

        var open = new MenuFlyoutItem { Text = "Open", Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += (_, _) => BookCardControl.RequestOpen(book);
        flyout.Items.Add(open);

        if (BookLauncher.ReadsInApp(book))
        {
            var external = new MenuFlyoutItem { Text = "Open with default app", Icon = new FontIcon { Glyph = "\uE8A7" } };
            external.Click += (_, _) => BookCardControl.RequestOpen(book, withDefaultApp: true);
            flyout.Items.Add(external);
        }

        var details = new MenuFlyoutItem { Text = "View details", Icon = new SymbolIcon(Symbol.List) };
        details.Click += (_, _) => BookCardControl.RequestDetails(book);
        flyout.Items.Add(details);

        var online = new MenuFlyoutItem { Text = "Find details online...", Icon = new SymbolIcon(Symbol.World) };
        online.Click += (_, _) => BookCardControl.RequestFindOnline(book);
        flyout.Items.Add(online);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var favorite = new ToggleMenuFlyoutItem { Text = "Favorite", IsChecked = book.IsFavorite };
        favorite.Click += (_, _) => vm.ToggleFavorite(book);
        flyout.Items.Add(favorite);

        var reading = new ToggleMenuFlyoutItem { Text = "Reading", IsChecked = book.Status == ReadStatus.Reading };
        reading.Click += (_, _) => vm.SetStatus(book, reading.IsChecked ? ReadStatus.Reading : ReadStatus.Unread);
        flyout.Items.Add(reading);

        var finished = new ToggleMenuFlyoutItem { Text = "Finished", IsChecked = book.Status == ReadStatus.Finished };
        finished.Click += (_, _) => vm.SetStatus(book, finished.IsChecked ? ReadStatus.Finished : ReadStatus.Unread);
        flyout.Items.Add(finished);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var folder = new MenuFlyoutItem
        {
            Text = "Show in folder",
            Icon = new SymbolIcon(Symbol.Folder),
            IsEnabled = book.IsAvailable
        };
        folder.Click += (_, _) => BookLauncher.ShowInFolder(book);
        flyout.Items.Add(folder);
    }
}
