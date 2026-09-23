using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// A reader's toolbar: in its own row above the page, or floating over the page's top edge and out of sight until it
/// is wanted (the pointer at the top, a tap there, the keyboard moved into it, or a key that needs it, like Ctrl+F).
/// It stays while it is in use (the pointer on it or on the contents, the keyboard in it, one of its menus open) and
/// goes a moment after. Floating, it never moves the page: a book laid out in pages would be laid out again every time
/// the toolbar came and went.
/// </summary>
internal sealed class ToolbarReveal
{
    /// <summary>The pointer this close to the top of the page brings the toolbar out.</summary>
    public const double Edge = 32;
    /// <summary>How long the toolbar stays once nothing needs it.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1200);
    /// <summary>When the book opens, or the toolbar starts hiding, it stays this long so the reader sees it go.</summary>
    public static readonly TimeSpan Glimpse = TimeSpan.FromSeconds(2.5);
    /// <summary>A tap at the top of a touch screen shows it this long.</summary>
    public static readonly TimeSpan TapHold = TimeSpan.FromSeconds(4);

    private readonly Grid _root;
    private readonly FrameworkElement _bar;
    private readonly UIElement _backdrop;
    private readonly FrameworkElement? _pane;
    private readonly DispatcherQueueTimer _timer;
    private bool _hides;
    private bool _shown = true;
    private bool _atTop;
    private bool _onPane;
    private DateTime _keepUntil;

    /// <param name="root">The view's grid: the toolbar in row 0, the page (and the contents) in row 1.</param>
    /// <param name="backdrop">The toolbar's own background, shown only while it floats over the page.</param>
    /// <param name="pane">The contents pane, if the view has one: the pointer on it keeps the toolbar.</param>
    /// <param name="focusPages">Gives the keyboard back to the page.</param>
    public ToolbarReveal(Grid root, FrameworkElement bar, UIElement backdrop, FrameworkElement? pane, Action focusPages)
    {
        _root = root;
        _bar = bar;
        _backdrop = backdrop;
        _pane = pane;
        bar.VerticalAlignment = VerticalAlignment.Top;
        bar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
        bar.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        _timer = root.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();

        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        root.PointerExited += (_, _) => _atTop = _onPane = false;
        bar.PointerExited += (_, _) => _atTop = false;
        if (pane is not null) pane.PointerExited += (_, _) => _onPane = false;
        // Tab or Shift+Tab from the page into a hidden toolbar brings it out.
        root.GotFocus += (_, e) =>
        {
            if (Inside(e.OriginalSource as DependencyObject)) Show();
        };

        // A menu opened with the mouse gives the keyboard back to the page when it closes, not to its button, where
        // Space would open it again. Opened from the keyboard, the keyboard stays in the toolbar.
        foreach (var button in Descendants(bar).OfType<DropDownButton>())
        {
            if (button.Flyout is not { } flyout) continue;
            var fromKeyboard = false;
            flyout.Opening += (_, _) => fromKeyboard = button.FocusState == FocusState.Keyboard;
            flyout.Closed += (_, _) => root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                var focused = root.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) : null;
                if (!fromKeyboard && (focused is null || ReferenceEquals(focused, button))) focusPages();
            });
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        IEnumerable<DependencyObject> children = element switch
        {
            Panel panel => panel.Children,
            Border { Child: { } child } => [child],
            _ => [],
        };
        foreach (var child in children)
        {
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }

    /// <summary>The toolbar in its row above the page, or floating over the page and hiding.</summary>
    public void SetHides(bool hides)
    {
        if (hides == _hides) return;
        _hides = hides;
        Grid.SetRow(_bar, hides ? 1 : 0);
        Canvas.SetZIndex(_bar, hides ? 1 : 0);
        _backdrop.Visibility = hides ? Visibility.Visible : Visibility.Collapsed;
        _shown = false;
        Appear();
        if (hides) Show(Glimpse);
        else _timer.Stop();
    }

    /// <summary>Brings a hiding toolbar out, for at least <paramref name="hold"/> or as long as it is in use.</summary>
    public void Show(TimeSpan? hold = null)
    {
        var until = DateTime.UtcNow + (hold ?? Linger);
        if (until > _keepUntil) _keepUntil = until;
        if (!_hides) return;
        Appear();
        _timer.Start();
    }

    /// <summary>Shows the toolbar and puts the keyboard on its first control, as Alt does.</summary>
    public void FocusFirst()
    {
        Show();
        if (FocusManager.FindFirstFocusableElement(_bar) is Control first) first.Focus(FocusState.Keyboard);
    }

    /// <summary>From a web page, which keeps the pointer to itself: whether the pointer is at its top edge.</summary>
    public void PointerAtTop(bool atTop)
    {
        _atTop = atTop;
        if (atTop) Show();
    }

    /// <summary>Whether the keyboard is in the toolbar or the contents, where Escape takes it back to the page.</summary>
    public bool HasFocus => _root.XamlRoot is { } xr && Inside(FocusManager.GetFocusedElement(xr) as DependencyObject);

    public void Close() => _timer.Stop();

    private void Appear()
    {
        if (_shown) return;
        _shown = true;
        _bar.IsHitTestVisible = true;
        _bar.Opacity = 1;
        _bar.Translation = Vector3.Zero;
    }

    private void Hide()
    {
        _shown = false;
        _timer.Stop();
        // Still there for Tab and for screen readers; only out of sight and out of the pointer's way.
        _bar.IsHitTestVisible = false;
        _bar.Opacity = 0;
        _bar.Translation = new Vector3(0, -8, 0);
    }

    private void Tick()
    {
        if (!_hides || !_shown)
        {
            _timer.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        if (InUse())
        {
            if (now + Linger > _keepUntil) _keepUntil = now + Linger;
        }
        else if (now >= _keepUntil) Hide();
    }

    private bool InUse() => _atTop || _onPane || KeyboardInside() || MenuOpen();

    /// <summary>
    /// Typing in one of its boxes, or moved into it with the keyboard. A button clicked with the mouse keeps no hold:
    /// the reader's hand has gone back to the page.
    /// </summary>
    private bool KeyboardInside()
    {
        if (_root.XamlRoot is not { } xr || FocusManager.GetFocusedElement(xr) is not DependencyObject focused) return false;
        return Inside(focused) && (focused is TextBox || focused is Control { FocusState: FocusState.Keyboard });
    }

    /// <summary>One of the toolbar's menus (or any menu) open; a tooltip does not count.</summary>
    private bool MenuOpen() =>
        _root.XamlRoot is { } xr && VisualTreeHelper.GetOpenPopupsForXamlRoot(xr).Any(p => p.Child is not ToolTip);

    private bool Inside(DependencyObject? element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (ReferenceEquals(e, _bar) || ReferenceEquals(e, _pane)) return true;
        return false;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_hides) return;
        var at = e.GetCurrentPoint(_root).Position;
        // Hidden, the top edge brings it out; shown, the pointer anywhere on it keeps it.
        _atTop = at.Y >= 0 && at.Y < (_shown ? Math.Max(Edge, _bar.ActualHeight) : Edge);
        _onPane = _pane is { Visibility: Visibility.Visible } && at.X < _pane.ActualWidth;
        if (_atTop) Show();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_hides || e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) return;
        if (e.GetCurrentPoint(_root).Position.Y < Edge * 2) Show(TapHold);
    }
}
