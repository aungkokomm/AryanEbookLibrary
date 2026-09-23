using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AryanEbookLibrary.Reader.Comic;
using AryanEbookLibrary.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Reads a CBZ or CBR comic one page at a time, fitted to the page or to the width. Each page is decoded off the UI
/// thread at exactly the size it is shown (a high-quality downscale, where the screen's own scaling would shimmer on
/// the printed dots), again at a higher resolution when zoomed, and the pages either side are made ready before
/// they are asked for. Arrow keys, Page Up and Down, Space, the wheel at a page's end and a click near either side
/// turn the page.
/// </summary>
public sealed partial class ComicReaderView : UserControl, IReaderView
{
    /// <summary>Wheel notches closer together than this turn one page: a touchpad sends a stream of them.</summary>
    private static readonly TimeSpan WheelGap = TimeSpan.FromMilliseconds(350);
    private const string PositionTag = "comic1";

    public event Action<int, int>? PageChanged;

    // The page is XAML, so the window hears its input and Escape itself.
    public event Action? Activity { add { } remove { } }
    public event Action? EscapeRequested { add { } remove { } }
    public event Action? FullScreenRequested;
    public event Action? FinishedRequested;
    public event Action? OpenExternallyRequested;

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
    private bool _fitWidth;
    private bool _placePending;
    private bool _showBottom;
    private double _zoomBucket = 1;
    private bool _endOffered;
    private bool _pumping;
    private DateTime _lastWheelTurn;
    private readonly Dictionary<int, PageBitmap> _images = new();
    private readonly HashSet<int> _failed = new();

    public ComicReaderView()
    {
        InitializeComponent();
        _fitWidth = AppServices.Settings.ReaderComicFit == "Width";
        (_fitWidth ? FitWidthItem : FitPageItem).IsChecked = true;
        FitButton.Content = _fitWidth ? "Fit width" : "Fit page";
        Scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
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
        GoTo(position is { } p && p.Page >= 0 && p.Page < book.PageCount ? p.Page : 0);
    }

    // ================================================================ pages

    private void GoTo(int page, bool showBottom = false)
    {
        if (_book is null) return;
        page = Math.Clamp(page, 0, _book.PageCount - 1);
        if (page == _page) return;
        _page = page;
        _placePending = true;
        _showBottom = showBottom;
        _zoomBucket = 1;
        ShowPageNumber();
        ShowCurrent();
        Evict();
        Pump();

        PageChanged?.Invoke(page, page);
        if (OfferFinish && !_endOffered && page == _book.PageCount - 1)
        {
            _endOffered = true;
            EndBar.IsOpen = true;
        }
    }

    /// <summary>The current page at the current fit, even while a sharper decode for it is on its way.</summary>
    private void ShowCurrent()
    {
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

    /// <summary>The layout pages are decoded for now; null until the view has a size.</summary>
    private Layout? CurrentLayout(double zoom)
    {
        var w = Scroller.ActualWidth;
        var h = Scroller.ActualHeight;
        if (w < 1 || h < 1 || XamlRoot is null) return null;
        return new Layout(_fitWidth, w, h, XamlRoot.RasterizationScale, zoom);
    }

    private Layout? WantedLayout(int page) => CurrentLayout(page == _page ? _zoomBucket : 1);

    /// <summary>This page first, then the next two, then the one before: the order they are likely to be wanted.</summary>
    private (int Page, Layout Layout)? NextWanted()
    {
        if (_book is null) return null;
        foreach (var page in new[] { _page, _page + 1, _page + 2, _page - 1 })
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
                if (page == _page) ShowCurrent();
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

    /// <summary>Keeps the page before, this one and the two after; the rest go.</summary>
    private void Evict()
    {
        foreach (var page in _images.Keys.Where(p => p < _page - 1 || p > _page + 2).ToList())
        {
            _images.Remove(page, out var image);
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
        if (e.IsIntermediate) return;
        var zoom = Scroller.ZoomFactor;
        var bucket = zoom <= 1.05f ? 1 : Math.Min(4, Math.Ceiling(zoom * 2) / 2);
        if (bucket == _zoomBucket) return;
        _zoomBucket = bucket;
        Pump();
    }

    // ================================================================ turning pages

    private bool AtTop => Scroller.VerticalOffset <= 1;
    private bool AtBottom => Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_book is null || e.OriginalSource is TextBox) return;
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
                GoTo(0);
                break;
            case VirtualKey.End:
                GoTo(_book.PageCount - 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The wheel scrolls a long page, and turns it once the page's end (or start) is already showing.</summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_book is null || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
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

    /// <summary>A click near the left edge goes back a page, near the right edge on.</summary>
    private void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
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
        void Add(VirtualKey key, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
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
        if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n)) GoTo(n - 1);
        FocusPages();
        ShowPageNumber();
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) =>
        PageBox.Text = _page >= 0 ? (_page + 1).ToString(CultureInfo.CurrentCulture) : "";

    private void OnFit(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag is not string tag) return;
        var fitWidth = tag == "Width";
        if (fitWidth == _fitWidth) return;
        _fitWidth = fitWidth;
        FitButton.Content = fitWidth ? "Fit width" : "Fit page";
        AppServices.Settings.ReaderComicFit = tag;
        AppServices.Settings.Save();
        _placePending = true;
        _showBottom = false;
        ShowCurrent();
        Pump();
        FocusPages();
    }

    public void SetTimeLeft(string text) => TimeLeftText.Text = text;

    private void OnMarkFinished(object sender, RoutedEventArgs e)
    {
        EndBar.IsOpen = false;
        FinishedRequested?.Invoke();
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    private void OnOpenExternally(object sender, RoutedEventArgs e) => OpenExternallyRequested?.Invoke();

    private void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e) =>
        TimeLeftText.Visibility = e.NewSize.Width < 520 ? Visibility.Collapsed : Visibility.Visible;

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

    // ================================================================ IReaderView

    public ReadingPosition? Position() =>
        _book is null || _page < 0 ? null : new ReadingPosition(_page, _book.PageCount, PositionTag);

    public bool HandleEscape() => false;

    public void FocusPages() => Scroller.Focus(FocusState.Programmatic);

    /// <summary>Hides the toolbar for full screen, and brings it back.</summary>
    public void SetChromeVisible(bool visible) => ToolBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        PageImage.Source = null;
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        // A page being read holds the book's lock; closing it waits for that, so not on the UI thread.
        var book = _book;
        _book = null;
        if (book is not null) _ = Task.Run(book.Dispose);
    }
}
