using AryanEbookLibrary.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// What F1 shows: every key and mouse action the reader answers, for the kind of book open, and in the library window
/// the library's. Kept beside the key handling it describes (PdfReaderView, EpubReaderView with reader.js,
/// ComicReaderView, ReaderWindow; MainWindow, BookCardControl, BookRowControl); a key added there belongs here too.
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
                ("Ctrl+Right, Ctrl+Left", "Next or previous entry in the contents"),
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
                    ("Ctrl+Right, Ctrl+Left", "Next or previous chapter"),
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
            ? new[] { ("Ctrl+plus, Ctrl+minus", "Zoom in or out"), ("Ctrl+0", "Fit the width"), ("Ctrl+Shift+T", "Page colour: Paper, Sepia, Night") }
            : comic
                ? new[] { ("Ctrl+plus, Ctrl+minus", "Zoom in or out"), ("Ctrl+0", "Back to the fitted page") }
                : new[]
                {
                    ("Ctrl+plus, Ctrl+minus", "Larger or smaller text"),
                    ("Ctrl+0", "The usual text size"),
                    ("Ctrl+Shift+T", "Page colour: Paper, Sepia, Night"),
                    ("Ctrl+L", "Pages or Scroll layout"),
                };

        var pane = pdf
            ? new[] { ("Ctrl+Shift+C", "Contents"), ("Ctrl+Shift+P", "Pages"), ("Ctrl+Shift+H", "Highlights") }
            : comic
                ? new[] { ("Ctrl+Shift+P", "Pages"), ("Ctrl+Shift+H", "Highlights") }
                : new[] { ("Ctrl+Shift+C", "Contents"), ("Ctrl+Shift+H", "Highlights") };

        var panel = new StackPanel { Spacing = 4, MinWidth = 440 };
        AddGroup(panel, "Moving", moving);
        if (book) Note(panel, "In Scroll layout, Down and Up scroll a little and Space goes down one screen.");
        if (!comic)
        {
            var find = new List<(string, string)> { ("Ctrl+F", "Find in book"), ("F3, Shift+F3", "Next or previous match") };
            if (pdf) find.Add(("Ctrl+C", "Copy the selected text"));
            AddGroup(panel, "Finding", find);
        }
        AddGroup(panel, "Marking", new[]
        {
            ("Ctrl+D", "Bookmark this page, or take its bookmark off"),
            ("F2, Shift+F2", "Next or previous bookmark"),
            comic ? ("Ctrl+H", "Keep a box just drawn round a panel, in the colour used last")
                  : ("Ctrl+H", "Highlight the words selected, in the colour used last"),
        });
        AddGroup(panel, "Side pane", pane);
        Note(panel, "The same keys again put the pane away. It opens on the tab picked last.");
        if (!comic)
            AddGroup(panel, "Listening", new[]
            {
                ("Ctrl+Shift+U", "Read aloud from here, or stop; Escape stops too"),
                ("Ctrl+Shift+Right, Ctrl+Shift+Left", pdf ? "While reading aloud, the next or previous page" : "While reading aloud, the next or previous paragraph"),
            });
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

    /// <summary>The library window's keys and mouse actions.</summary>
    public static UIElement Library()
    {
        var panel = new StackPanel { Spacing = 4, MinWidth = 440 };
        AddGroup(panel, "Books", new[]
        {
            ("Tab, Shift+Tab", "Move between the books and the buttons"),
            ("Page Down, Page Up", "Down or up a screen of books"),
            ("Home, End", "The first or last book; Ctrl+Home and Ctrl+End too"),
            ("Enter", "Read the book"),
            ("Alt+Enter", "The book's details"),
            ("Shift+F10, Menu key", "The book's menu"),
        });
        AddGroup(panel, "Finding", new[]
        {
            ("Ctrl+F, Ctrl+E", "Search the library"),
            ("Escape", "Clear the search, in the search box"),
        });
        AddGroup(panel, "Mouse", new[]
        {
            ("Click a book", "The book's details"),
            ("Double-click a book", "Read it"),
            ("Point at a cover", "Read and View details buttons"),
            ("Right-click a book", "The book's menu"),
        });
        AddGroup(panel, "Window", new[] { ("F1", "This list") });
        Note(panel, "In a book, F1 lists the reader's own keys.");
        return panel;
    }

    /// <summary>
    /// The list in a dialog. It scrolls when it is taller than the window allows: a dialog's own content does not.
    /// </summary>
    public static async Task ShowAsync(UIElement list, XamlRoot root) =>
        await new ContentDialog
        {
            Title = "Keyboard and mouse",
            Content = new ScrollViewer { Content = list, Padding = new Thickness(0, 0, 16, 0) },
            CloseButtonText = "Close",
            XamlRoot = root,
        }.ShowThemedAsync();

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
