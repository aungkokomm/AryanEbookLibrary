using AryanEbookLibrary.Models;
using AryanEbookLibrary.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// The reader's side panel of the book's highlights and notes, in book order. A click goes to one; right-click
/// changes its note or colour, copies or deletes it.
/// </summary>
public sealed partial class HighlightsPanel : UserControl
{
    private ReaderAnnotations? _notes;

    public HighlightsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Go to it on the page.</summary>
    public event Action<Annotation>? OpenRequested;
    /// <summary>Go to it and open its note.</summary>
    public event Action<Annotation>? NoteRequested;
    /// <summary>How many there are now.</summary>
    public event Action<int>? CountChanged;

    /// <summary>What to say when there are none: the readers of pictures have no words to select.</summary>
    public string EmptyMessage { get; set; } =
        "Select words and pick a colour to highlight them. Right-click a page to add a note to it. They all show here.";

    public void Bind(ReaderAnnotations notes)
    {
        _notes = notes;
        notes.ListChanged += Refresh;
        Refresh();
    }

    public int Count => _notes?.Items.Count ?? 0;

    private void Refresh()
    {
        if (_notes is null) return;
        var items = _notes.Items.Select(a => new AnnotationItem(a, null)).ToList();
        List.ItemsSource = items;
        EmptyText.Text = EmptyMessage;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountChanged?.Invoke(items.Count);
    }

    /// <summary>A menu icon that is a dot of one highlight colour (Segoe's filled circle).</summary>
    public static FontIcon ColorDot(int color) => new()
    {
        Glyph = ((char)0xEA3B).ToString(),
        Foreground = new SolidColorBrush(HighlightColors.Of(color)),
    };

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnotationItem item) OpenRequested?.Invoke(item.Annotation);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_notes is null || (e.OriginalSource as FrameworkElement)?.DataContext is not AnnotationItem item) return;
        var a = item.Annotation;
        var menu = new MenuFlyout();

        var go = new MenuFlyoutItem { Text = "Go to it", Icon = new FontIcon { Glyph = ((char)0xE8AD).ToString() } };
        go.Click += (_, _) => OpenRequested?.Invoke(a);
        menu.Items.Add(go);

        var note = new MenuFlyoutItem { Text = a.HasNote ? "Edit note..." : "Add a note...", Icon = new FontIcon { Glyph = ((char)0xE70B).ToString() } };
        note.Click += (_, _) => NoteRequested?.Invoke(a);
        menu.Items.Add(note);

        if (a.IsMark)
        {
            var colours = new MenuFlyoutSubItem { Text = "Colour", Icon = new FontIcon { Glyph = ((char)0xE790).ToString() } };
            for (var i = 1; i <= HighlightColors.Count; i++)
            {
                var color = i;
                var item_ = new RadioMenuFlyoutItem
                {
                    Text = HighlightColors.Name(color),
                    GroupName = "Colour",
                    IsChecked = a.Color == color,
                    Icon = ColorDot(color),
                };
                item_.Click += (_, _) =>
                {
                    a.Color = color;
                    _notes.Save(a);
                };
                colours.Items.Add(item_);
            }
            menu.Items.Add(colours);
        }

        if (a.Quote.Trim().Length > 0)
        {
            var copy = new MenuFlyoutItem { Text = "Copy the words", Icon = new SymbolIcon(Symbol.Copy) };
            copy.Click += (_, _) => WordMenu.CopyText(a.Quote.Trim());
            menu.Items.Add(copy);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += (_, _) => _notes.Delete(a);
        menu.Items.Add(delete);

        e.Handled = true;
        menu.ShowAt((FrameworkElement)e.OriginalSource, new FlyoutShowOptions { Position = e.GetPosition((FrameworkElement)e.OriginalSource) });
    }
}
