using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>A book's details, shown from pages other than the library the same way the library shows them.</summary>
internal static class BookDialogs
{
    private static bool _open;   // one ContentDialog at a time

    /// <summary>
    /// The details dialog, then what its buttons ask for: open the book, show it in its folder, or (after
    /// "Find details online" or picking a suggestion) the details again with what changed.
    /// </summary>
    public static async Task ShowDetailsAsync(XamlRoot root, Book book)
    {
        if (_open) return;
        var vm = AppServices.Library;
        while (true)
        {
            BookDetailsDialog dialog;
            ContentDialogResult result;
            _open = true;
            try
            {
                dialog = new BookDetailsDialog(book, vm) { XamlRoot = root };
                result = await dialog.ShowThemedAsync();
                if (dialog.Next == DetailsNext.FindOnline)
                    await new FindOnlineDialog(book) { XamlRoot = root }.ShowThemedAsync();
            }
            finally
            {
                _open = false;
            }
            if (dialog.Next is DetailsNext.FindOnline or DetailsNext.Reopen) continue;

            if (result == ContentDialogResult.Secondary) BookLauncher.ShowInFolder(book);
            if (result != ContentDialogResult.Primary || vm.OpenBook(book) is not { } error) return;

            _open = true;
            try
            {
                await new ContentDialog
                {
                    Title = "Cannot open this book",
                    Content = error,
                    CloseButtonText = "OK",
                    XamlRoot = root
                }.ShowThemedAsync();
            }
            finally
            {
                _open = false;
            }
            return;
        }
    }
}
