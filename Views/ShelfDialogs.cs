using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>The small dialogs for naming a shelf: saving a new one (library page) and renaming one (navigation pane).</summary>
internal static class ShelfDialogs
{
    /// <summary>
    /// Asks for a shelf's name. Null when cancelled. When saving, a name that is taken replaces that shelf (and
    /// says so); when renaming (<paramref name="renaming"/> is the shelf's current name) a taken name is refused.
    /// </summary>
    public static async Task<string?> AskNameAsync(XamlRoot root, string title, string primary, string initial, string? renaming = null)
    {
        var box = new TextBox { Text = initial, PlaceholderText = "Shelf name", MaxLength = 80 };
        var note = new TextBlock
        {
            Style = (Style)Application.Current.Resources["PageHintStyle"],
            Visibility = Visibility.Collapsed
        };
        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = new StackPanel { Spacing = 8, MinWidth = 320, Children = { box, note } }
        };

        void Check()
        {
            var name = (box.Text ?? "").Trim();
            var taken = AppServices.Library.FindShelf(name);
            var clash = taken is not null && !string.Equals(taken.Name, renaming, StringComparison.CurrentCultureIgnoreCase);
            note.Text = !clash ? ""
                : renaming is null ? $"There is already a shelf called “{taken!.Name}”. Saving replaces it."
                : $"There is already a shelf called “{taken!.Name}”.";
            note.Visibility = clash ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && !(clash && renaming is not null);
        }

        box.TextChanged += (_, _) => Check();
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        Check();
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary ? (box.Text ?? "").Trim() : null;
    }
}
