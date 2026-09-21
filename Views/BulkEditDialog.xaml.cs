using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// One edit for every ticked book. Empty fields are left alone, so setting only the series keeps each
/// book's own author. Tags are added to what a book already has, never replacing them.
/// </summary>
public sealed partial class BulkEditDialog : ContentDialog
{
    public BulkEditDialog(int count)
    {
        InitializeComponent();
        CountText.Text = count == 1 ? "1 book selected." : $"{count:N0} books selected.";
        Opened += (_, _) => AuthorBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    public string Author => AuthorBox.Text.Trim();
    public string Series => SeriesBox.Text.Trim();
    public string AddTags => TagsBox.Text.Trim();
    public string RemoveTags => RemoveTagsBox.Text.Trim();
}
