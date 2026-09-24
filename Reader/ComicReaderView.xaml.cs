using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Reader.Comic;
using AryanEbookLibrary.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Reads a CBZ or CBR comic as one continuous column of pages as wide as the view, or one page at a time fitted to the
/// page or to the width. Each page is decoded off the UI thread at exactly the size it is shown (a high-quality
/// downscale, where the screen's own scaling would shimmer on the printed dots), again at a higher resolution when
/// zoomed, and the pages either side are made ready before they are asked for. Arrow keys, Page Up and Down, Space,
/// the wheel at a page's end and a click near either side turn the page.
/// </summary>
public sealed partial class ComicReaderView : UserControl, IReaderView
{
    /// <summary>Wheel notches closer together than this turn one page: a touchpad sends a stream of them.</summary>
    private static readonly TimeSpan WheelGap = TimeSpan.FromMilliseconds(350);
    private const string PositionTag = "comic1";
    /// <summary>A page's shape before it has been read, in continuous view: most comic pages are about this.</summary>
    private static readonly Size UnknownPage = new(1000, 1540);

    private enum ViewMode { Continuous, Page, Width }

    public event Action<int, int>? PageChanged;

    // The page is XAML, so the window hears its input, Escape and Ctrl+W itself.
    public event Action? Activity { add { } remove { } }
    public event Action? EscapeRequested { add { } remove { } }
    public event Action? CloseRequested { add { } remove { } }
    public event Action? FullScreenRequested;
    public event Action? FinishedRequested;
    public event Action? OpenExternallyRequested;
    public event Action? ShortcutsRequested;

    /// <summary>What a page is decoded for: the fit, the view's size in DIPs, the screen's scale and the zoom.</summary>
    private sealed record Layout(bool FitWidth, double Width, double Height, double Scale, double Zoom);

    /// <summary>
    /// A decoded page. The source reads its bitmap when it is first drawn, not when it is given it: disposing the
    /// bitmap early crashed the app (0xC000027B) the moment a page made ready ahead was turned to. Both go together.
    /// </summary>
    private sealed record PageBitmap(SoftwareBitmapSource Source, SoftwareBitmap Bitmap, Layout Layout, double NaturalWidth, double NaturalHeight)
    {
        public void Dispose()
        {
            Source.Dispose();
            Bitmap.Dispose();
        }
    }

    private ComicBook? _book;
    private bool _closed;
    private int _page = -1;
    private ViewMode _mode;
    private bool _placePending;
    private bool _showBottom;
    private double _zoomBucket = 1;
    private bool _endOffered;
    private bool _pumping;
    private DateTime _lastWheelTurn;
    private readonly Dictionary<int, PageBitmap> _images = new();
    private readonly HashSet<int> _failed = new();

    // Continuous view: an Image per page, each page's size once it has been read, and each page's top in DIPs.
    private readonly List<Image> _strip = new();
    private readonly Dictionary<int, Size> _sizes = new();
    private Size? _typical;
    private double[] _tops = [];
    private bool _placing;

    private readonly ToolbarReveal _reveal;
    private readonly JumpHistory<int> _jumps = new();

    public ComicReaderView()
    {
        InitializeComponent();
        _mode = Enum.TryParse<ViewMode>(AppServices.Settings.ReaderComicView, out var mode) ? mode : ViewMode.Continuous;
        var item = _mode switch { ViewMode.Page => FitPageItem, ViewMode.Width => FitWidthItem, _ => ContinuousItem };
        item.IsChecked = true;
        FitButton.Content = item.Text;
        ShowMode();
        Scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        _reveal = new ToolbarReveal(Root, ToolBar, ToolBarBack, HighlightsPane, FocusPages);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSideButton), true);
        AddAccelerators();
    }

    // ================================================================ opening

    public int PageCount => _book?.PageCount ?? 0;

    /// <summary>Whether reaching the last page offers "Mark as finished". The window says, from the book's status.</summary>
    public bool OfferFinish { get; set; } = true;

    public async Task OpenAsync(string path, ReadingPosition? position)
    {
        ShowMessage("Opening the comic...", "", ring: true);
        var clock = Stopwatch.StartNew();
        ComicBook book;
        try
        {
            book = await Task.Run(() => ComicBook.Open(path));
        }
        catch (Exception ex)
        {
            Log.Write($"reader: could not open {path}: {ex.Message}");
            ShowMessage("This comic could not be opened", ex is NotSupportedException
                    ? "It is a solid RAR archive, which can only be read from its start. Another comic reader may still open it."
                    : "The file may be damaged, or not really a comic archive. Another comic reader may still open it.",
                ring: false, external: true);
            return;
        }
        if (_closed)
        {
            book.Dispose();
            return;
        }
        if (book.PageCount == 0)
        {
            book.Dispose();
            ShowMessage("This comic has no pages", "No pictures were found in the archive.", ring: false, external: true);
            return;
        }

        _book = book;
        Log.Write($"reader: opened {Path.GetFileName(path)}, {book.PageCount} pages, in {clock.ElapsedMilliseconds} ms");
        PageCountText.Text = $"of {book.PageCount:N0}";
        _reveal.Show(ToolbarReveal.Glimpse);
        GoTo(position is { } p && p.Page >= 0 && p.Page < book.PageCount ? p.Page : 0);
        if (_pendingReveal is { } reveal)
        {
            _pendingReveal = null;
            Reveal(reveal);
        }
        _ = MakeMissingClipsAsync();
        ProbeHighlights();
    }

    // ================================================================ pages

    private bool Continuous => _mode == ViewMode.Continuous;

    /// <summary>Shows the page from its top. In continuous view that scrolls to it, even when it is already the page being read.</summary>
    private void GoTo(int page, bool showBottom = false)
    {
        if (_book is null) return;
        var wanted = page;
        page = Math.Clamp(page, 0, _book.PageCount - 1);
        // Past either end nothing happens; in continuous view it would scroll back to the page's top.
        if (page == _page && (!Continuous || page != wanted)) return;
        _placePending = true;
        _showBottom = showBottom;
        if (!Continuous) _zoomBucket = 1;
        if (page != _page) SetPage(page);
        ShowCurrent();
        Evict();
        Pump();
    }

    /// <summary>The page being read is now this one: its number, the reading record, and at the end the offer to finish.</summary>
    private void SetPage(int page)
    {
        _page = page;
        ShowPageNumber();
        PageChanged?.Invoke(page, page);
        if (OfferFinish && !_endOffered && page == _book!.PageCount - 1)
        {
            _endOffered = true;
            EndBar.IsOpen = true;
        }
    }

    /// <summary>The current page at the current fit, even while a sharper decode for it is on its way.</summary>
    private void ShowCurrent()
    {
        DrawMarksSoon();
        if (Continuous)
        {
            ShowStrip();
            return;
        }
        if (_failed.Contains(_page))
        {
            PageImage.Source = null;
            ShowMessage($"Page {_page + 1} could not be shown", "Its picture may be damaged.", ring: false);
            return;
        }
        if (!_images.TryGetValue(_page, out var image) || CurrentLayout(1) is not { } layout)
        {
            PageImage.Source = null;
            return;
        }

        HideMessage();
        var (w, h) = DisplaySize(image.NaturalWidth, image.NaturalHeight, layout);
        PageImage.Width = w;
        PageImage.Height = h;
        if (!ReferenceEquals(PageImage.Source, image.Source)) PageImage.Source = image.Source;

        if (!_placePending) return;
        _placePending = false;
        // A new page starts unzoomed, at its top, or at its end when the reader came back to it by scrolling up.
        Scroller.UpdateLayout();
        Scroller.ChangeView(0, _showBottom ? Scroller.ScrollableHeight : 0, 1f, disableAnimation: true);
    }

    private static (double Width, double Height) DisplaySize(double naturalWidth, double naturalHeight, Layout layout)
    {
        var scale = layout.FitWidth
            ? layout.Width / naturalWidth
            : Math.Min(layout.Width / naturalWidth, layout.Height / naturalHeight);
        return (naturalWidth * scale, naturalHeight * scale);
    }

    /// <summary>
    /// Continuous view: every page laid out at its size, the ones decoded showing their pictures. A page not read yet
    /// is laid out like the first one that was, and takes its own size once it is read.
    /// </summary>
    private void ShowStrip()
    {
        if (_book is null || CurrentLayout(1) is not { } layout) return;
        HideMessage();
        if (_strip.Count == 0)
        {
            for (var i = 0; i < _book.PageCount; i++)
            {
                var image = new Image { Stretch = Stretch.Fill };
                AutomationProperties.SetName(image, $"Page {i + 1}");
                Strip.Children.Add(image);
                Scroller.RegisterAnchorCandidate(image);
                _strip.Add(image);
            }
        }

        var tops = new double[_strip.Count];
        var top = 0.0;
        for (var i = 0; i < _strip.Count; i++)
        {
            var size = _sizes.TryGetValue(i, out var known) ? known : _typical ?? UnknownPage;
            var (w, h) = DisplaySize(size.Width, size.Height, layout);
            _strip[i].Width = w;
            _strip[i].Height = h;
            tops[i] = top;
            top += h + Strip.Spacing;
        }
        _tops = tops;
        foreach (var (page, image) in _images)
            if (!ReferenceEquals(_strip[page].Source, image.Source)) _strip[page].Source = image.Source;

        // A jump, to the page's top. Until the view gets there a page above it can learn its size and move that top,
        // so it is asked again.
        if (!_placePending && !_placing) return;
        _placePending = false;
        _placing = true;
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, _tops[_page] * Scroller.ZoomFactor, null, disableAnimation: true);
    }

    /// <summary>The pages in the view: in continuous view the ones the view crosses, otherwise the one page.</summary>
    private (int First, int Last) InView()
    {
        if (!Continuous || _tops.Length == 0) return (_page, _page);
        var zoom = Scroller.ZoomFactor;
        var top = Scroller.VerticalOffset / zoom;
        var (first, last) = (PageAt(top), PageAt(top + Scroller.ViewportHeight / zoom));
        // Until a jump has been scrolled to, the page jumped to.
        return _page >= first && _page <= last ? (first, last) : (_page, _page);
    }

    /// <summary>Continuous view: the page at this height in the column.</summary>
    private int PageAt(double y)
    {
        var i = Array.BinarySearch(_tops, y);
        return Math.Clamp(i >= 0 ? i : ~i - 1, 0, _tops.Length - 1);
    }

    /// <summary>The layout pages are decoded for now; null until the view has a size.</summary>
    private Layout? CurrentLayout(double zoom)
    {
        var w = Scroller.ActualWidth;
        var h = Scroller.ActualHeight;
        if (w < 1 || h < 1 || XamlRoot is null) return null;
        return new Layout(_mode != ViewMode.Page, w, h, XamlRoot.RasterizationScale, zoom);
    }

    private Layout? WantedLayout(int page)
    {
        var (first, last) = InView();
        return CurrentLayout(page >= first && page <= last ? _zoomBucket : 1);
    }

    /// <summary>The pages in view first, then the next two, then the one before: the order they are likely to be wanted.</summary>
    private (int Page, Layout Layout)? NextWanted()
    {
        if (_book is null) return null;
        var (first, last) = InView();
        foreach (var page in Enumerable.Range(first, last - first + 1).Append(last + 1).Append(last + 2).Append(first - 1))
        {
            if (page < 0 || page >= _book.PageCount || _failed.Contains(page)) continue;
            if (WantedLayout(page) is not { } layout) return null;
            if (!_images.TryGetValue(page, out var image) || image.Layout != layout) return (page, layout);
        }
        return null;
    }

    /// <summary>Decodes the pages that are wanted, one at a time, until none is missing or out of date.</summary>
    private async void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (!_closed && _book is { } book && NextWanted() is { } want)
            {
                var (page, layout) = want;
                (SoftwareBitmap Bitmap, double Width, double Height)? decoded;
                try
                {
                    decoded = await Task.Run(() => DecodeAsync(book, page, layout));
                }
                catch (Exception ex)
                {
                    Log.Write($"reader: page {page + 1} could not be decoded: {ex.Message}");
                    decoded = null;
                }
                if (_closed || !ReferenceEquals(book, _book))
                {
                    decoded?.Bitmap.Dispose();
                    break;
                }
                if (decoded is not { } d)
                {
                    _failed.Add(page);
                    if (page == _page) ShowCurrent();
                    continue;
                }

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(d.Bitmap);
                var decodedPage = new PageBitmap(source, d.Bitmap, layout, d.Width, d.Height);
                if (_closed)
                {
                    decodedPage.Dispose();
                    break;
                }
                var old = _images.GetValueOrDefault(page);
                _images[page] = decodedPage;
                _sizes[page] = new Size(d.Width, d.Height);
                _typical ??= _sizes[page];
                if (page == _page || Continuous) ShowCurrent();
                old?.Dispose();
                Evict();
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    /// <summary>Reads the page and decodes it at the size it is shown, never larger than the picture itself.</summary>
    private static async Task<(SoftwareBitmap Bitmap, double Width, double Height)?> DecodeAsync(ComicBook book, int page, Layout layout)
    {
        if (book.ReadPage(page) is not { } bytes) return null;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double naturalWidth = decoder.PixelWidth, naturalHeight = decoder.PixelHeight;
        var (w, _) = DisplaySize(naturalWidth, naturalHeight, layout);
        var width = Math.Clamp(Math.Round(w * layout.Scale * layout.Zoom), 1, naturalWidth);
        var transform = new BitmapTransform
        {
            InterpolationMode = BitmapInterpolationMode.Fant,
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)Math.Max(1, Math.Round(width * naturalHeight / naturalWidth)),
        };
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (bitmap, naturalWidth, naturalHeight);
    }

    /// <summary>Keeps the page before, the ones in view and the two after; the rest go.</summary>
    private void Evict()
    {
        var (first, last) = InView();
        foreach (var page in _images.Keys.Where(p => p < first - 1 || p > last + 2).ToList())
        {
            _images.Remove(page, out var image);
            if (page < _strip.Count) _strip[page].Source = null;
            if (!ReferenceEquals(PageImage.Source, image!.Source)) image.Dispose();
        }
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // At least the view's size, so a fitted page sits in the middle of it.
        PageHost.MinWidth = e.NewSize.Width;
        PageHost.MinHeight = e.NewSize.Height;
        ShowCurrent();
        Pump();
    }

    /// <summary>Zoomed in, the page is decoded again at the new size so it stays sharp.</summary>
    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        PlaceBar();
        if (Continuous)
        {
            // A jump has arrived, or the reader has taken over the scrolling.
            if (_placing && (e.IsIntermediate || Math.Abs(Scroller.VerticalOffset
                    - Math.Min(_tops[_page] * Scroller.ZoomFactor, Scroller.ScrollableHeight)) < 1))
                _placing = false;
            FollowScroll();
        }
        if (e.IsIntermediate) return;
        var zoom = Scroller.ZoomFactor;
        var bucket = zoom <= 1.05f ? 1 : Math.Min(4, Math.Ceiling(zoom * 2) / 2);
        if (bucket == _zoomBucket) return;
        _zoomBucket = bucket;
        Pump();
    }

    /// <summary>
    /// Continuous view, as it scrolls: the page across the upper middle of the view is the one being read (the last
    /// once the end is showing), the pages coming into view are decoded and the ones left behind let go.
    /// </summary>
    private void FollowScroll()
    {
        if (_book is null || _tops.Length == 0 || _placePending || _placing) return;
        var y = (Scroller.VerticalOffset + Scroller.ViewportHeight * 0.4) / Scroller.ZoomFactor;
        var page = Scroller.ScrollableHeight >= 1 && AtBottom ? _tops.Length - 1 : PageAt(y);
        if (page != _page) SetPage(page);
        Evict();
        Pump();
    }

    // ================================================================ turning pages

    private bool AtTop => Scroller.VerticalOffset <= 1;
    private bool AtBottom => Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;

    /// <summary>
    /// A jump to a page (a page number typed in, Home or End), which Back returns from. Turning pages is not a jump.
    /// </summary>
    private void JumpTo(int page)
    {
        if (_book is null) return;
        if (_page >= 0 && Math.Clamp(page, 0, _book.PageCount - 1) != _page) _jumps.Jumped(_page);
        GoTo(page);
    }

    private void GoBack()
    {
        if (_jumps.Back(_page) is { } page) GoTo(page);
    }

    private void GoForward()
    {
        if (_jumps.Forward(_page) is { } page) GoTo(page);
    }

    /// <summary>The mouse's side buttons: back and forward after a jump.</summary>
    private void OnSideButton(object sender, PointerRoutedEventArgs e)
    {
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsXButton1Pressed) GoBack();
        else if (buttons.IsXButton2Pressed) GoForward();
        else return;
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // In the toolbar the keys are the toolbar's: Tab and the arrows between its buttons, Space to press one.
        if (_book is null || e.OriginalSource is TextBox || _reveal.HasFocus) return;
        var modifiers = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                        || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (modifiers) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        // Left and Right pan a page zoomed wider than the view; otherwise they turn it.
        var wide = Scroller.ScrollableWidth >= 1;

        switch (e.Key)
        {
            case VirtualKey.Right when !wide:
                GoTo(_page + 1);
                break;
            case VirtualKey.Left when !wide:
                GoTo(_page - 1);
                break;
            case VirtualKey.PageDown or VirtualKey.Down when AtBottom:
                GoTo(_page + 1);
                break;
            case VirtualKey.PageUp or VirtualKey.Up when AtTop:
                GoTo(_page - 1, showBottom: true);
                break;
            case VirtualKey.Space when !shift:
                if (AtBottom) GoTo(_page + 1);
                else Scroller.ChangeView(null, Scroller.VerticalOffset + Scroller.ViewportHeight * 0.9, null);
                break;
            case VirtualKey.Space:
                if (AtTop) GoTo(_page - 1, showBottom: true);
                else Scroller.ChangeView(null, Scroller.VerticalOffset - Scroller.ViewportHeight * 0.9, null);
                break;
            case VirtualKey.Home:
                JumpTo(0);
                break;
            case VirtualKey.End:
                JumpTo(_book.PageCount - 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// The wheel scrolls a long page, and turns it once the page's end (or start) is already showing. In continuous
    /// view it only scrolls.
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_book is null || Continuous || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        var now = DateTime.UtcNow;
        if (delta == 0 || now - _lastWheelTurn < WheelGap) return;
        if (delta < 0 && AtBottom)
        {
            _lastWheelTurn = now;
            GoTo(_page + 1);
        }
        else if (delta > 0 && AtTop)
        {
            _lastWheelTurn = now;
            GoTo(_page - 1, showBottom: true);
        }
    }

    /// <summary>A click on a clipped panel or a page's note opens it; near the left edge goes back a page, near the right edge on.</summary>
    private void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (AnnotationUnder(e.GetPosition(PageHost)) is { } hit)
        {
            EditAt(hit);
            return;
        }
        var x = e.GetPosition(Scroller).X;
        var w = Scroller.ActualWidth;
        if (x < w * 0.3) GoTo(_page - 1);
        else if (x > w * 0.7) GoTo(_page + 1);
        FocusPages();
    }

    private void ZoomBy(float factor)
    {
        var zoom = Math.Clamp(Scroller.ZoomFactor * factor, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        var ratio = zoom / Scroller.ZoomFactor;
        // About the middle of the view, not its top left corner.
        var x = (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) * ratio - Scroller.ViewportWidth / 2;
        var y = (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) * ratio - Scroller.ViewportHeight / 2;
        Scroller.ChangeView(Math.Max(0, x), Math.Max(0, y), zoom);
    }

    private void AddAccelerators()
    {
        void Add(VirtualKey key, Action action, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                action();
                e.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.Add, () => ZoomBy(1.25f));
        Add((VirtualKey)187, () => ZoomBy(1.25f));     // the = + key
        Add(VirtualKey.Subtract, () => ZoomBy(0.8f));
        Add((VirtualKey)189, () => ZoomBy(0.8f));      // the - key
        Add(VirtualKey.Number0, () => Scroller.ChangeView(0, 0, 1f));
        Add(VirtualKey.G, () =>
        {
            PageBox.Focus(FocusState.Keyboard);
            PageBox.SelectAll();
        });
        Add(VirtualKey.Left, GoBack, VirtualKeyModifiers.Menu);
        Add(VirtualKey.Right, GoForward, VirtualKeyModifiers.Menu);
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // ================================================================ toolbar

    private void OnPreviousPage(object sender, RoutedEventArgs e)
    {
        GoTo(_page - 1);
        FocusPages();
    }

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        GoTo(_page + 1);
        FocusPages();
    }

    /// <summary>Not while the reader is typing a page number. The first page can come before the view has a window.</summary>
    private void ShowPageNumber()
    {
        if (_page < 0 || XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), PageBox)) return;
        PageBox.Text = (_page + 1).ToString(CultureInfo.CurrentCulture);
    }

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n)) JumpTo(n - 1);
        FocusPages();
        ShowPageNumber();
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) =>
        PageBox.Text = _page >= 0 ? (_page + 1).ToString(CultureInfo.CurrentCulture) : "";

    private void OnFit(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag } item || !Enum.TryParse<ViewMode>(tag, out var mode)) return;
        if (mode == _mode) return;
        _mode = mode;
        FitButton.Content = item.Text;
        AppServices.Settings.ReaderComicView = tag;
        AppServices.Settings.Save();
        ShowMode();
        _placePending = true;
        _showBottom = false;
        if (!Continuous) _zoomBucket = 1;
        ShowCurrent();
        Evict();
        Pump();
        FocusPages();
    }

    /// <summary>The column of pages in continuous view, the one page otherwise; the other lets go of its pictures.</summary>
    private void ShowMode()
    {
        Strip.Visibility = Continuous ? Visibility.Visible : Visibility.Collapsed;
        PageImage.Visibility = Continuous ? Visibility.Collapsed : Visibility.Visible;
        if (Continuous) PageImage.Source = null;
        else foreach (var image in _strip) image.Source = null;
    }

    public void SetTimeLeft(string text) => TimeLeftText.Text = text;

    private void OnMarkFinished(object sender, RoutedEventArgs e)
    {
        EndBar.IsOpen = false;
        FinishedRequested?.Invoke();
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    private void OnShortcuts(object sender, RoutedEventArgs e) => ShortcutsRequested?.Invoke();

    private void OnOpenExternally(object sender, RoutedEventArgs e) => OpenExternallyRequested?.Invoke();

    private void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e) =>
        ToolbarFit.Fit(ToolBar, Root.ActualWidth,
            () => TimeLeftText.Visibility = ShortcutsButton.Visibility = Visibility.Visible,
            () => TimeLeftText.Visibility = Visibility.Collapsed,
            () => ShortcutsButton.Visibility = Visibility.Collapsed);

    private void ShowMessage(string title, string text, bool ring, bool external = false)
    {
        MessagePanel.Visibility = Visibility.Visible;
        MessageRing.IsActive = ring;
        MessageRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExternalButton.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideMessage()
    {
        MessagePanel.Visibility = Visibility.Collapsed;
        MessageRing.IsActive = false;
    }

    // ================================================================ highlights and notes

    private ReaderAnnotations? _notes;
    private Annotation? _pendingReveal;
    /// <summary>A box being drawn: its page and corners, in the page host's DIPs.</summary>
    private (int Page, Point Start, Point End)? _drag;
    private Point _pressedAt;
    /// <summary>A box drawn and waiting for a colour, in fractions of the page.</summary>
    private (int Page, Rect Box)? _pendingArea;
    /// <summary>What the bar is about, in fractions of the page.</summary>
    private (int Page, Rect Box)? _barAnchor;
    /// <summary>Outlined for a moment after a jump to it, in fractions of the page.</summary>
    private (int Page, Rect Box)? _flash;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _flashTimer;

    /// <summary>A clip's picture is no wider than this: enough to read a panel's words, small enough to keep.</summary>
    private const int ClipMaxWidth = 1200;

    public void UseAnnotations(ReaderAnnotations notes)
    {
        _notes = notes;
        notes.Attach(Bar);
        notes.Drawn += _ => DrawMarks();
        notes.Removed += _ => DrawMarks();
        notes.Reloaded += DrawMarks;
        notes.Created += a => _ = MakeClipAsync(a);
        Bar.Closed += () =>
        {
            if (_pendingArea is null) return;
            _pendingArea = null;
            DrawMarks();
        };
        HighlightsList.EmptyMessage =
            "Drag a box round a panel to keep it. Right-click a page to highlight all of it or add a note to it. They all show here.";
        HighlightsList.CountChanged += n => PaneTitle.Text = n > 0 ? $"Highlights ({n:N0})" : "Highlights";
        HighlightsList.OpenRequested += a =>
        {
            Reveal(a);
            FocusPages();
        };
        HighlightsList.NoteRequested += a =>
        {
            Reveal(a);
            notes.EditNote(a, BarBox(), SurfaceSize);
        };
        HighlightsList.Bind(notes);
    }

    private static (int Page, Rect Box)? AreaOf(Annotation a) =>
        Annotation.AnchorNumbers(a.Anchor, "comicarea1") is [var p, var x, var y, var w, var h] ? ((int)p, new Rect(x, y, w, h)) : null;

    private static int PageOf(Annotation a) => AreaOf(a)?.Page ?? a.Page;

    private Size SurfaceSize => new(Surface.ActualWidth, Surface.ActualHeight);

    /// <summary>Where a page's picture is laid out, in the page host's DIPs; null when it is not (another page, in page view).</summary>
    private Rect? PageBounds(int page)
    {
        FrameworkElement? image = Continuous ? (page >= 0 && page < _strip.Count ? _strip[page] : null) : page == _page ? PageImage : null;
        if (image is null || image.ActualWidth < 1 || image.ActualHeight < 1) return null;
        return image.TransformToVisual(PageHost).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
    }

    /// <summary>The page under a point of the page host, of those in and next to the view.</summary>
    private int? PageUnder(Point host)
    {
        if (_book is null) return null;
        var (first, last) = InView();
        for (var p = Math.Max(0, first - 1); p <= Math.Min(_book.PageCount - 1, last + 1); p++)
            if (PageBounds(p) is { } b && b.Contains(host)) return p;
        return null;
    }

    private static Rect Within(Rect bounds, Rect fraction) =>
        new(bounds.X + fraction.X * bounds.Width, bounds.Y + fraction.Y * bounds.Height, fraction.Width * bounds.Width, fraction.Height * bounds.Height);

    /// <summary>Where the i-th note on a page shows: small notes along the page's top edge, from the right.</summary>
    private static Rect BadgeIn(Rect bounds, int i) => new(bounds.Right - 36 - i * 34, bounds.Top + 8, 28, 28);

    private List<Annotation> PageNotesOn(int page) =>
        _notes?.Items.Where(a => a.Kind == AnnotationKind.PageNote && a.Page == page).ToList() ?? new();

    private static Rect DragBox((int Page, Point Start, Point End) drag) =>
        new(Math.Min(drag.Start.X, drag.End.X), Math.Min(drag.Start.Y, drag.End.Y),
            Math.Abs(drag.End.X - drag.Start.X), Math.Abs(drag.End.Y - drag.Start.Y));

    /// <summary>What is under a click: a note on the page first, then the clipped panels, the latest on top.</summary>
    private Annotation? AnnotationUnder(Point host)
    {
        if (_notes is null || PageUnder(host) is not { } page || PageBounds(page) is not { } bounds) return null;
        var notes = PageNotesOn(page);
        for (var i = 0; i < notes.Count; i++)
            if (BadgeIn(bounds, i).Contains(host)) return notes[i];
        return _notes.Items.LastOrDefault(a => AreaOf(a) is { } area && area.Page == page && Within(bounds, area.Box).Contains(host));
    }

    // ---- drawing ----

    private void DrawMarksSoon() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, DrawMarks);

    /// <summary>The clipped panels, the notes on pages, a box being drawn and the outline of one jumped to.</summary>
    private void DrawMarks()
    {
        MarksLayer.Children.Clear();
        if (_notes is null || _book is null || _closed) return;

        void AddBox(Rect r, Windows.UI.Color? fill, Windows.UI.Color stroke, double thickness, bool dashed = false)
        {
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(1, r.Width),
                Height = Math.Max(1, r.Height),
                Fill = fill is { } f ? new SolidColorBrush(f) : null,
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = thickness,
                RadiusX = 3,
                RadiusY = 3,
            };
            if (dashed) rect.StrokeDashArray = new DoubleCollection { 4, 3 };
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
            MarksLayer.Children.Add(rect);
        }

        foreach (var a in _notes.Items)
            if (AreaOf(a) is { } area && PageBounds(area.Page) is { } b)
                AddBox(Within(b, area.Box), HighlightColors.Wash(a.Color, 0x2E), HighlightColors.Of(a.Color), 2.5);

        foreach (var page in _notes.Items.Where(a => a.Kind == AnnotationKind.PageNote).Select(a => a.Page).Distinct())
        {
            if (PageBounds(page) is not { } b) continue;
            var count = PageNotesOn(page).Count;
            for (var i = 0; i < count; i++) DrawNoteBadge(BadgeIn(b, i));
        }

        var blue = Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x78, 0xE6);
        var wash = Windows.UI.Color.FromArgb(0x22, 0x1E, 0x78, 0xE6);
        if (_drag is { } drag && Math.Abs(drag.End.X - drag.Start.X) + Math.Abs(drag.End.Y - drag.Start.Y) >= 6)
            AddBox(DragBox(drag), wash, blue, 1.5, dashed: true);
        if (_pendingArea is { } waiting && PageBounds(waiting.Page) is { } wb)
            AddBox(Within(wb, waiting.Box), wash, blue, 1.5, dashed: true);
        if (_flash is { } flash && PageBounds(flash.Page) is { } fb)
        {
            var r = Within(fb, flash.Box);
            AddBox(new Rect(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8), null, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00), 3);
        }
        PlaceBar();
    }

    /// <summary>A note on a page: a small yellow note in its top corner, the look of Define's popup.</summary>
    private void DrawNoteBadge(Rect at)
    {
        var badge = new Border
        {
            Width = at.Width,
            Height = at.Height,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xF4, 0xCE)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xC8, 0x4F)),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = ((char)0xE70B).ToString(),
                FontSize = 14,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x3D, 0x34, 0x13)),
            },
        };
        Canvas.SetLeft(badge, at.X);
        Canvas.SetTop(badge, at.Y);
        MarksLayer.Children.Add(badge);
    }

    // ---- drawing a box ----

    private void OnHostPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || _notes is null) return;
        var point = e.GetCurrentPoint(PageHost);
        if (!point.Properties.IsLeftButtonPressed) return;
        _notes.HideBar();
        if (PageUnder(point.Position) is not { } page) return;
        _drag = (page, point.Position, point.Position);
        _pressedAt = point.Position;
        PageHost.CapturePointer(e.Pointer);
    }

    private void OnHostMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || PageBounds(drag.Page) is not { } b) return;
        var at = e.GetCurrentPoint(PageHost).Position;
        _drag = (drag.Page, drag.Start, new Point(Math.Clamp(at.X, b.Left, b.Right), Math.Clamp(at.Y, b.Top, b.Bottom)));
        if (Math.Abs(at.X - _pressedAt.X) + Math.Abs(at.Y - _pressedAt.Y) >= 6) DrawMarks();
    }

    /// <summary>A box drawn round part of a page: the bar offers the colours for it. A click is left to Tapped.</summary>
    private void OnHostReleased(object sender, PointerRoutedEventArgs e)
    {
        PageHost.ReleasePointerCapture(e.Pointer);
        if (_drag is not { } drag) return;
        _drag = null;
        var box = DragBox(drag);
        if (box.Width >= 12 && box.Height >= 12 && PageBounds(drag.Page) is { } b)
            OfferArea(drag.Page, new Rect((box.X - b.X) / b.Width, (box.Y - b.Y) / b.Height, box.Width / b.Width, box.Height / b.Height));
        else DrawMarks();
    }

    private void OnHostCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null) return;
        _drag = null;
        DrawMarks();
    }

    // ---- the bar ----

    /// <summary>What the bar is about, where it is over the view now.</summary>
    private Rect? BarBox()
    {
        if (_barAnchor is not { } a) return null;
        if (PageBounds(a.Page) is not { } b) return new Rect(-1000, -1000, 0, 0);
        return PageHost.TransformToVisual(Surface).TransformBounds(Within(b, a.Box));
    }

    private void PlaceBar()
    {
        if (_notes?.BarOpen == true) _notes.Place(BarBox(), SurfaceSize);
    }

    private void OfferArea(int page, Rect fraction)
    {
        if (_notes is null || _book is null) return;
        _pendingArea = (page, fraction);
        _barAnchor = (page, fraction);
        DrawMarks();
        var count = _book.PageCount;
        _notes.Offer(() =>
        {
            _pendingArea = null;
            return new Annotation
            {
                Kind = AnnotationKind.Area,
                Anchor = Annotation.MakeAnchor("comicarea1", page, fraction.X, fraction.Y, fraction.Width, fraction.Height),
                Page = page,
                Position = (page + Math.Clamp(fraction.Y, 0, 0.999)) / Math.Max(1, count),
            };
        }, "", BarBox(), SurfaceSize);
    }

    private void NewPageNote(int page)
    {
        if (_notes is null || _book is null) return;
        _barAnchor = (page, new Rect(0.5, 0.05, 0, 0));
        var count = _book.PageCount;
        _notes.NewPageNote(() => new Annotation
        {
            Kind = AnnotationKind.PageNote,
            Anchor = Annotation.MakeAnchor("page1", page),
            Page = page,
            Position = page / (double)Math.Max(1, count),
        }, BarBox(), SurfaceSize);
    }

    private void EditAt(Annotation a)
    {
        if (_notes is null) return;
        var page = PageOf(a);
        if (a.Kind == AnnotationKind.PageNote)
        {
            var index = PageNotesOn(page).IndexOf(a);
            _barAnchor = PageBounds(page) is { } b && index >= 0
                ? (page, new Rect((BadgeIn(b, index).X - b.X) / b.Width, (BadgeIn(b, index).Y - b.Y) / b.Height, 28 / b.Width, 28 / b.Height))
                : (page, new Rect(0.5, 0.05, 0, 0));
            _notes.EditNote(a, BarBox(), SurfaceSize);
            return;
        }
        _barAnchor = (page, AreaOf(a)?.Box ?? new Rect(0.5, 0.05, 0, 0));
        _notes.Edit(a, BarBox(), SurfaceSize);
    }

    /// <summary>Right-click on a page: highlight all of it, or add a note to it.</summary>
    private void OnPageRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_notes is null || PageUnder(e.GetPosition(PageHost)) is not { } page) return;
        var menu = new MenuFlyout();
        var whole = new MenuFlyoutItem { Text = "Highlight this page...", Icon = new FontIcon { Glyph = ((char)0xE7E6).ToString() } };
        whole.Click += (_, _) => OfferArea(page, new Rect(0, 0, 1, 1));
        menu.Items.Add(whole);
        WordMenu.AddNoteItems(menu, null, null, () => NewPageNote(page));
        e.Handled = true;
        menu.ShowAt(PageHost, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(PageHost) });
    }

    /// <summary>
    /// For checking highlights without a mouse, each only when its variable is set: ARYAN_READER_AREA=x,y,w,h draws
    /// a box (fractions of the page it opens at); ARYAN_READER_PAGENOTE=1 starts a note on it. The bar shows as it
    /// would, for UI Automation to pick a colour or write the note.
    /// </summary>
    private async void ProbeHighlights()
    {
        var area = Environment.GetEnvironmentVariable("ARYAN_READER_AREA");
        var note = Environment.GetEnvironmentVariable("ARYAN_READER_PAGENOTE");
        if (_book is null || (string.IsNullOrWhiteSpace(area) && string.IsNullOrWhiteSpace(note))) return;
        await Task.Delay(2500);
        if (_closed) return;
        if (!string.IsNullOrWhiteSpace(area))
        {
            var n = area.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            OfferArea(_page, new Rect(n[0], n[1], n[2], n[3]));
        }
        else NewPageNote(_page);
    }

    // ---- going to one, and the side pane ----

    /// <summary>Turns to its page, as a jump Back returns from, and outlines a clipped panel for a moment.</summary>
    public void Reveal(Annotation a)
    {
        if (_book is null)
        {
            _pendingReveal = a;
            return;
        }
        var page = Math.Clamp(PageOf(a), 0, _book.PageCount - 1);
        if (_page >= 0 && page != _page) _jumps.Jumped(_page);
        GoTo(page);
        _barAnchor = (page, AreaOf(a)?.Box ?? new Rect(0.5, 0.05, 0, 0));
        if (AreaOf(a) is { } area)
        {
            _flash = (page, area.Box);
            if (_flashTimer is null)
            {
                _flashTimer = DispatcherQueue.CreateTimer();
                _flashTimer.Interval = TimeSpan.FromMilliseconds(1600);
                _flashTimer.IsRepeating = false;
                _flashTimer.Tick += (_, _) =>
                {
                    _flash = null;
                    DrawMarks();
                };
            }
            _flashTimer.Stop();
            _flashTimer.Start();
        }
        DrawMarksSoon();
    }

    private void OnHighlightsClick(object sender, RoutedEventArgs e)
    {
        SetPaneOpen(HighlightsButton.IsChecked == true);
        FocusPages();
    }

    private void SetPaneOpen(bool open)
    {
        HighlightsButton.IsChecked = open;
        HighlightsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- pictures ----

    /// <summary>Keeps a picture of a clipped panel, decoded from the page itself at up to <see cref="ClipMaxWidth"/>.</summary>
    private async Task MakeClipAsync(Annotation a)
    {
        var book = _book;
        if (book is null || _closed || AreaOf(a) is not { } area || area.Page < 0 || area.Page >= book.PageCount) return;
        try
        {
            var bytes = await Task.Run(() => book.ReadPage(area.Page));
            if (bytes is null || _closed) return;
            var png = await ClipMaker.CropImageAsync(bytes, area.Box, ClipMaxWidth);
            if (png is not null && !_closed) ClipStore.Save(a.Id, png);
        }
        catch (Exception ex)
        {
            Log.Write($"reader: a clip of page {area.Page + 1} could not be made: {ex.Message}");
        }
    }

    /// <summary>Pictures made on another computer, or never made here: made now the comic is open, one at a time.</summary>
    private async Task MakeMissingClipsAsync()
    {
        if (_notes is null) return;
        foreach (var a in _notes.Items.Where(a => a.KeepsPicture && !ClipStore.Exists(a.Id)).ToList())
        {
            if (_closed) return;
            await MakeClipAsync(a);
        }
    }

    // ================================================================ IReaderView

    public ReadingPosition? Position() =>
        _book is null || _page < 0 ? null : new ReadingPosition(_page, _book.PageCount, PositionTag);

    /// <summary>Escape from the window: puts the highlight bar away, or takes the keyboard from the toolbar back to the page.</summary>
    public bool HandleEscape()
    {
        if (_notes?.BarOpen == true)
        {
            _notes.HideBar();
            FocusPages();
            return true;
        }
        if (!_reveal.HasFocus) return false;
        FocusPages();
        return true;
    }

    public void FocusPages() => Scroller.Focus(FocusState.Programmatic);

    public void SetToolbar(bool hides, bool fullScreen)
    {
        _reveal.SetHides(hides);
        if (fullScreen == _fullScreen) return;
        _fullScreen = fullScreen;
        if (fullScreen) SetPaneOpen(false);   // going full screen puts the side pane away, as the other readers do
    }

    private bool _fullScreen;

    public void FocusToolbar() => _reveal.FocusFirst();

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _reveal.Close();
        _flashTimer?.Stop();
        PageImage.Source = null;
        foreach (var image in _strip) image.Source = null;
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        // A page being read holds the book's lock; closing it waits for that, so not on the UI thread.
        var book = _book;
        _book = null;
        if (book is not null) _ = Task.Run(book.Dispose);
    }
}
