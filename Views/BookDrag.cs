using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Books dragged from the grid or the list onto a list in the navigation pane (CineLibrary's drag to a list).
/// The books travel by key in the package's own properties, which only this app reads; their titles go along as
/// text, so a drop into a text editor gives something sensible.
/// </summary>
internal static class BookDrag
{
    private const string KeysProperty = "aryan/book-keys";
    private const char Break = (char)10;   // a key has "|" in it; a path never has a line break

    /// <summary>The book, or every selected book when it is one of them.</summary>
    public static void Start(DragStartingEventArgs args, Book book)
    {
        var vm = AppServices.Library;
        var books = book.IsSelected && vm.SelectedCount > 1 ? vm.Selected : new List<Book> { book };
        args.Data.Properties[KeysProperty] = string.Join(Break, books.Select(b => b.StateKey));
        args.Data.SetText(string.Join(Environment.NewLine, books.Select(b => b.Title)));
        args.Data.RequestedOperation = DataPackageOperation.Link;
    }

    public static bool HasBooks(DataPackageView view) => view.Properties.ContainsKey(KeysProperty);

    public static List<Book> Read(DataPackageView view)
    {
        if (!view.Properties.TryGetValue(KeysProperty, out var raw) || raw is not string keys) return new();
        var vm = AppServices.Library;
        return keys.Split(Break, StringSplitOptions.RemoveEmptyEntries).Select(vm.FindByKey).OfType<Book>().ToList();
    }
}
