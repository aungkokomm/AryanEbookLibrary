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

        var open = new MenuFlyoutItem { Text = "Open", Icon = new SymbolIcon(Symbol.OpenFile), KeyboardAcceleratorTextOverride = "Enter" };
        open.Click += (_, _) => BookCardControl.RequestOpen(book);
        flyout.Items.Add(open);

        if (BookLauncher.ReadsInApp(book))
        {
            var external = new MenuFlyoutItem { Text = "Open with default app", Icon = new FontIcon { Glyph = "\uE8A7" } };
            external.Click += (_, _) => BookCardControl.RequestOpen(book, withDefaultApp: true);
            flyout.Items.Add(external);
        }

        var details = new MenuFlyoutItem { Text = "View details", Icon = new SymbolIcon(Symbol.List), KeyboardAcceleratorTextOverride = "Alt+Enter" };
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
        flyout.Items.Add(ListsMenu(book, () => flyout.XamlRoot));

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

    /// <summary>
    /// "Add to list": every one of My lists with a tick where the book is on it, and "New list" to make one with
    /// the book already on it. The details window's Add to list button opens the same items.
    /// </summary>
    public static MenuFlyoutSubItem ListsMenu(Book book, Func<Microsoft.UI.Xaml.XamlRoot?> root)
    {
        var menu = new MenuFlyoutSubItem { Text = "Add to list", Icon = new FontIcon { Glyph = ((char)0xE8FD).ToString() } };
        FillLists(menu.Items, book, root);
        return menu;
    }

    public static void FillLists(IList<MenuFlyoutItemBase> items, Book book, Func<Microsoft.UI.Xaml.XamlRoot?> root)
    {
        var vm = AppServices.Library;
        items.Clear();
        if (vm.UserLists.Count == 0)
            items.Add(new MenuFlyoutItem { Text = "No lists yet", IsEnabled = false });
        foreach (var name in vm.UserLists)
        {
            var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = book.InList(name) };
            item.Click += (_, _) => vm.SetInList(book, name, item.IsChecked);
            items.Add(item);
        }
        items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "New list...", Icon = new SymbolIcon(Symbol.Add) };
        add.Click += async (_, _) =>
        {
            if (root() is not { } xamlRoot) return;
            try
            {
                var name = await ListDialogs.AskNameAsync(xamlRoot, "New list", "Create", "");
                if (name is not null && vm.CreateList(name)) vm.SetInList(book, name, true);
            }
            catch (Exception ex)
            {
                // another dialog was already open in that window
                Log.Write("New list from a menu: " + ex.Message);
            }
        };
        items.Add(add);
    }
}
