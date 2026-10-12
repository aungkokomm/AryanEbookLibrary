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
    /// <summary>What the menu of several ticked books asks the library page to do (it shows how it went).</summary>
    public enum Many { Edit, FinishedToday, FinishedEarlier, Reading, Unread, Favorite, NotFavorite, Online, AddToList, NewList, CopyTo }

    /// <summary>An action on every ticked book; the list's name comes with AddToList.</summary>
    public static event Action<Many, string?>? ManyRequested;

    public static void Build(MenuFlyout flyout, Book? book)
    {
        flyout.Items.Clear();
        if (book is null) return;
        var vm = AppServices.Library;

        // Right-click one of several ticked books: the menu works on all of them, as in File Explorer.
        if (book.IsSelected && vm.SelectedCount > 1)
        {
            BuildMany(flyout.Items);
            return;
        }

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

    private static void BuildMany(IList<MenuFlyoutItemBase> items)
    {
        var vm = AppServices.Library;
        items.Add(new MenuFlyoutItem { Text = vm.SelectedText, IsEnabled = false });
        items.Add(new MenuFlyoutSeparator());

        var mark = new MenuFlyoutSubItem { Text = "Mark", Icon = new SymbolIcon(Symbol.Accept) };
        mark.Items.Add(Item("Finished today", Many.FinishedToday));
        mark.Items.Add(Item("Finished earlier (no date)", Many.FinishedEarlier));
        mark.Items.Add(Item("Reading", Many.Reading));
        mark.Items.Add(Item("Unread", Many.Unread));
        items.Add(mark);
        items.Add(Item("Favorite", Many.Favorite));
        items.Add(Item("Not favorite", Many.NotFavorite));

        items.Add(new MenuFlyoutSeparator());
        var lists = new MenuFlyoutSubItem { Text = "Add to list", Icon = new FontIcon { Glyph = ((char)0xE8FD).ToString() } };
        FillListsForMany(lists.Items);
        items.Add(lists);
        var edit = Item("Edit...", Many.Edit);
        edit.Icon = new SymbolIcon(Symbol.Edit);
        items.Add(edit);
        var online = Item("Find details online", Many.Online);
        online.Icon = new SymbolIcon(Symbol.World);
        items.Add(online);
        var copy = Item("Copy files to a folder...", Many.CopyTo);
        copy.Icon = new SymbolIcon(Symbol.Copy);
        items.Add(copy);

        items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear the ticks", Icon = new SymbolIcon(Symbol.Clear) };
        clear.Click += (_, _) => vm.ClearSelection();
        items.Add(clear);
    }

    private static MenuFlyoutItem Item(string text, Many action, string? list = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => ManyRequested?.Invoke(action, list);
        return item;
    }

    /// <summary>Every one of My lists, and "New list", for putting all the ticked books on one.</summary>
    public static void FillListsForMany(IList<MenuFlyoutItemBase> items)
    {
        var vm = AppServices.Library;
        items.Clear();
        if (vm.UserLists.Count == 0)
            items.Add(new MenuFlyoutItem { Text = "No lists yet", IsEnabled = false });
        foreach (var name in vm.UserLists) items.Add(Item(name, Many.AddToList, name));
        items.Add(new MenuFlyoutSeparator());
        var add = Item("New list...", Many.NewList);
        add.Icon = new SymbolIcon(Symbol.Add);
        items.Add(add);
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
