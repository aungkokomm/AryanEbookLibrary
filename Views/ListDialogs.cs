using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>The small dialogs for My lists: naming a new one or renaming one, and deleting one.</summary>
internal static class ListDialogs
{
    /// <summary>
    /// Asks for a list's name. Null when cancelled. A name another list has is refused in the dialog itself,
    /// so the user sees why before pressing anything (CineLibrary told them afterwards).
    /// </summary>
    public static async Task<string?> AskNameAsync(XamlRoot root, string title, string primary, string initial, string? renaming = null)
    {
        var box = new TextBox { Text = initial, PlaceholderText = "List name", MaxLength = 80 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "List name");
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
            var taken = AppServices.Library.FindList(name);
            var clash = taken is not null && !string.Equals(taken, renaming, StringComparison.CurrentCultureIgnoreCase);
            note.Text = clash ? $"There is already a list called “{taken}”." : "";
            note.Visibility = clash ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && !clash;
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

    public static async Task<bool> ConfirmDeleteAsync(XamlRoot root, string name, int count)
    {
        var books = count == 1 ? "1 book" : $"{count:N0} books";
        return await new ContentDialog
        {
            Title = $"Delete “{name}”?",
            Content = $"The list has {books} on it. The books stay in the library: only the list goes.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root
        }.ShowThemedAsync() == ContentDialogResult.Primary;
    }
}
