using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// What F1 shows: every key and mouse action the reader answers, for the kind of book open. Kept beside the key
/// handling it describes (PdfReaderView, EpubReaderView with reader.js, ComicReaderView, ReaderWindow); a key added
/// there belongs here too.
/// </summary>
internal static class ReaderShortcuts
{
    public static UIElement Build(IReaderView reader)
    {
        var pdf = reader is PdfReaderView;
        var comic = reader is ComicReaderView;
        var book = !pdf && !comic;

        var moving = pdf
            ? new[]
            {
                ("Space, Page Down", "Down one screen"),
                ("Shift+Space, Page Up", "Up one screen"),
                ("Down, Up", "Scroll a little"),
                ("Right, Left", "Next or previous page"),
                ("Home, End", "First or last page"),
                ("Ctrl+G", "Go to a page"),
                ("Alt+Left, Alt+Right", "Back or forward after a link or a jump"),
            }
            : comic
                ? new[]
                {
                    ("Right, Left", "Next or previous page"),
                    ("Space, Page Down", "Down one screen, then on to the next page"),
                    ("Shift+Space, Page Up", "Up one screen, then back a page"),
                    ("Down, Up", "Scroll a little"),
                    ("Home, End", "First or last page"),
                    ("Ctrl+G", "Go to a page"),
                    ("Alt+Left, Alt+Right", "Back or forward after a jump"),
                }
                : new[]
                {
                    ("Right, Down, Space, Page Down", "Next page"),
                    ("Left, Up, Shift+Space, Page Up", "Previous page"),
                    ("Home, End", "Start or end of the book"),
                    ("Ctrl+G", "Go to a place in the book, as a percentage"),
                    ("Alt+Left, Alt+Right", "Back or forward after a link or a jump"),
                };

        var mouse = pdf
            ? new[]
            {
                ("Wheel", "Scroll"),
                ("Ctrl+wheel", "Zoom"),
                ("Double-click a word", "Select it"),
                ("Right-click a word", "Define, copy or find it"),
                ("Side buttons", "Back or forward after a link or a jump"),
            }
            : comic
                ? new[]
                {
                    ("Click the left or right side", "Previous or next page"),
                    ("Wheel", "Scroll; in Fit page and Fit width, turn the page at its end"),
                    ("Ctrl+wheel", "Zoom"),
                    ("Side buttons", "Back or forward after a jump"),
                }
                : new[]
                {
                    ("Click the left or right margin", "Previous or next page"),
                    ("Wheel", "Turn the page; in Scroll layout, scroll on through the whole book"),
                    ("Right-click a word", "Define, copy or find it"),
                    ("Side buttons", "Back or forward after a link or a jump"),
                };

        var view = pdf
            ? new[] { ("Ctrl+plus, Ctrl+minus", "Zoom in or out"), ("Ctrl+0", "Fit the width") }
            : comic
                ? new[] { ("Ctrl+plus, Ctrl+minus", "Zoom in or out"), ("Ctrl+0", "Back to the fitted page") }
                : new[] { ("Ctrl+plus, Ctrl+minus", "Larger or smaller text"), ("Ctrl+0", "The usual text size") };

        var panel = new StackPanel { Spacing = 4, MinWidth = 440 };
        AddGroup(panel, "Moving", moving);
        if (book) Note(panel, "In Scroll layout, Down and Up scroll a little and Space goes down one screen.");
        if (!comic)
        {
            var find = new List<(string, string)> { ("Ctrl+F", "Find in book"), ("F3, Shift+F3", "Next or previous match") };
            if (pdf) find.Add(("Ctrl+C", "Copy the selected text"));
            AddGroup(panel, "Finding", find);
        }
        AddGroup(panel, "View", view);
        AddGroup(panel, "Mouse", mouse);
        AddGroup(panel, "Window", new[]
        {
            ("Alt", "Show the toolbar and move to it; Escape goes back to the page"),
            ("F11", "Full screen; Escape leaves it"),
            ("Ctrl+W", "Close the book and go back to the library"),
            ("F1", "This list"),
        });
        Note(panel, "When the toolbar hides (Settings, Reading), point at the top of the page to bring it back.");
        return panel;
    }

    private static void AddGroup(StackPanel panel, string title, IReadOnlyList<(string Keys, string What)> rows)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 14, 0, 4),
        });
        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = rows[i].Keys, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var what = new TextBlock { Text = rows[i].What, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            grid.Children.Add(keys);
            grid.Children.Add(what);
        }
        panel.Children.Add(grid);
    }

    private static void Note(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Margin = new Thickness(0, 8, 0, 0),
        });
}
