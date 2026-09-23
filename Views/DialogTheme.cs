using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AryanEbookLibrary.Views;

public static class DialogTheme
{
    /// <summary>
    /// Shows the dialog in the app's own theme. A dialog lives in the popup layer, outside the element the
    /// theme is set on, so by itself it takes Windows' theme: a Dark app on light Windows showed white
    /// dialogs. Menus and flyouts do not need this, they take the theme of the control they open from.
    /// </summary>
    public static IAsyncOperation<ContentDialogResult> ShowThemedAsync(this ContentDialog dialog)
    {
        if (App.MainWindow?.Content is FrameworkElement root) dialog.RequestedTheme = root.ActualTheme;
        return dialog.ShowAsync();
    }
}
