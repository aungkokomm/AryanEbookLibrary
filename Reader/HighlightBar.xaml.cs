using AryanEbookLibrary.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// The bar over a selection or a highlight, the same in every reader: the five colours, a note, copy and delete.
/// It only says what was chosen; the reader it sits in does it. Placed like Define: above what it is about, below it
/// when there is no room, always inside the view.
/// </summary>
public sealed partial class HighlightBar : UserControl
{
    private readonly List<Ellipse> _dots = new();
    private Rect? _box;
    private Size _area;

    /// <summary>A colour was chosen, 1 to 5.</summary>
    public event Action<int>? ColorPicked;
    /// <summary>The note button: the reader makes the highlight first when there is none yet, then asks for the note.</summary>
    public event Action? NoteRequested;
    public event Action<string>? NoteSaved;
    public event Action? CopyRequested;
    public event Action? DeleteRequested;
    /// <summary>The bar went away, whatever the reason: what it was offered for is no longer waiting.</summary>
    public event Action? Closed;

    public HighlightBar()
    {
        InitializeComponent();
        for (var i = 1; i <= HighlightColors.Count; i++)
        {
            var color = i;
            var dot = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = new SolidColorBrush(HighlightColors.Of(color)),
                Stroke = new SolidColorBrush(Colors.Transparent),
            };
            _dots.Add(dot);
            var button = new Button
            {
                Style = (Style)Resources["BarButtonStyle"],
                Width = 30,
                Content = dot,
            };
            var name = HighlightColors.Name(color);
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Highlight " + name);
            button.Click += (_, _) => ColorPicked?.Invoke(color);
            Swatches.Children.Add(button);
        }
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public bool IsEditingNote => IsOpen && NotePanel.Visibility == Visibility.Visible;

    /// <summary>
    /// Shows the colours: for words just selected (<paramref name="current"/> null), or for a highlight, whose colour is
    /// ringed. Copy only when there are words; Delete only for one that exists; no colours for a note on a page.
    /// </summary>
    public void Show(int? current, bool canCopy, bool canDelete, bool colors = true)
    {
        Swatches.Visibility = colors ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _dots.Count; i++)
        {
            var on = current == i + 1;
            _dots[i].StrokeThickness = on ? 3 : 0;
            ((SolidColorBrush)_dots[i].Stroke).Color = on ? (ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black) : Colors.Transparent;
        }
        CopyButton.Visibility = canCopy ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        NoteButton.Visibility = Visibility.Visible;
        NotePanel.Visibility = Visibility.Collapsed;
        SyncRow();
        Visibility = Visibility.Visible;
        Place();
    }

    /// <summary>Opens the note box under the colours, with the keyboard in it.</summary>
    public void EditNote(string note)
    {
        NotePanel.Visibility = Visibility.Visible;
        NoteButton.Visibility = Visibility.Collapsed;   // the box is open: the button for it says nothing more
        SyncRow();
        NoteBox.Text = note;
        Visibility = Visibility.Visible;
        Place();
        UpdateLayout();
        NoteBox.Focus(FocusState.Programmatic);
        NoteBox.SelectionStart = NoteBox.Text.Length;
    }

    /// <summary>
    /// The rule between the colours and the buttons only when both show, and no top row at all when nothing is in it
    /// (a new note on a page: just the box).
    /// </summary>
    private void SyncRow()
    {
        static bool Shown(UIElement e) => e.Visibility == Visibility.Visible;
        var buttons = Shown(NoteButton) || Shown(CopyButton) || Shown(DeleteButton);
        SwatchRule.Visibility = Shown(Swatches) && buttons ? Visibility.Visible : Visibility.Collapsed;
        TopRow.Visibility = Shown(Swatches) || buttons ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Hide()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        NotePanel.Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    /// <summary>
    /// Where the words are, in the coordinates of the area the bar is laid over, and that area's size. A null box puts
    /// the bar in the upper middle; a box outside the area hides it until the words are back in view.
    /// </summary>
    public void PlaceNear(Rect? box, Size area)
    {
        _box = box;
        _area = area;
        Place();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Place();

    private void Place()
    {
        if (!IsOpen) return;
        var w = ActualWidth;
        var h = ActualHeight;
        var box = _box ?? new Rect(_area.Width / 2 - w / 2, _area.Height / 3, 0, 0);
        if (_box is not null && (box.Bottom < 0 || box.Top > _area.Height || box.Right < 0 || box.Left > _area.Width))
        {
            Opacity = 0;
            IsHitTestVisible = false;
            return;
        }

        var left = Math.Clamp(box.X, 12, Math.Max(12, _area.Width - w - 12));
        var top = box.Y - h - 10;
        if (top < 12) top = box.Bottom + 10;
        top = Math.Clamp(top, 12, Math.Max(12, _area.Height - h - 12));
        Translation = new System.Numerics.Vector3((float)left, (float)top, 32);
        Opacity = w > 0 ? 1 : 0;
        IsHitTestVisible = true;
    }

    private void OnNote(object sender, RoutedEventArgs e) => NoteRequested?.Invoke();

    private void OnCopy(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke();

    private void OnNoteSave(object sender, RoutedEventArgs e) => NoteSaved?.Invoke(NoteBox.Text.Trim());

    private void OnNoteCancel(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Ctrl+Enter saves, Escape puts the note away unsaved; Enter alone is a new line.</summary>
    private void OnNoteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter
                 && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            NoteSaved?.Invoke(NoteBox.Text.Trim());
            e.Handled = true;
        }
    }
}
