using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AryanEbookLibrary.Reader.Define;
using AryanEbookLibrary.Reader.Pdf;
using AryanEbookLibrary.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Reads a PDF: a continuous stack of pages that zooms, the book's own contents, find in book, text selection, links
/// and Define. The reading half of Ayaan PDF: its native core (native\reader_core), its tile pyramid, text layer, night
/// mode and dictionaries, with a view of its own that has nothing to do with editing.
/// </summary>
public sealed partial class PdfReaderView : UserControl
{
    /// <summary>The widest page, in "slot" DIPs at zoom 1. Every other page is scaled to it.</summary>
    private const double BaseWidth = 1000;
    /// <summary>Between pages, and above the first and below the last.</summary>
    private const double Gap = 16;
    /// <summary>Either side of the widest page.</summary>
    private const double Side = 24;
    /// <summary>Up to this level a page is rendered whole (2048 px wide); deeper zoom is drawn from tiles.</summary>
    private const int WholePageLevels = 2;

    public event Action<int>? PageChanged;
    public event Action? FullScreenRequested;
    public event Action? FinishedRequested;
    public event Action? OpenExternallyRequested;

    private enum Fit { Width, Page, Custom }

    private sealed class PageCard
    {
        public required int Page { get; init; }
        public required Grid Root { get; init; }
        public required Border Sheet { get; init; }
        public required Image Base { get; init; }
        public required Image Fine { get; init; }
        public required Canvas Tiles { get; init; }
        public required Canvas Marks { get; init; }
        public required Canvas Links { get; init; }
        public int BaseTheme { get; set; } = -1;
        public int FineLevel { get; set; } = -1;
        public int FineTheme { get; set; } = -1;
        public int TileLevel { get; set; } = -1;
        public Dictionary<(int Col, int Row), Image> TileImages { get; } = new();
    }

    private PdfBook? _book;
    private string _path = "";
    private bool _closed;
    private readonly PageStackGeometry _stack = new(Gap);
    private double _maxWidthPts = 612;
    private readonly Dictionary<int, PageCard> _cards = new();
    private RenderQueue? _queue;
    private int _theme;               // 0 paper, 1 sepia, 2 night
    private int _themeGeneration = 1;
    private Fit _fit = Fit.Width;
    private float _expectedZoom = -1;
    private float _lastZoom = 1;
    private int _currentPage = -1;
    private ReadingPosition? _pendingPosition;
    private bool _layoutPending;
    private bool _endOffered;

    public PdfReaderView()
    {
        InitializeComponent();
        _theme = AppServices.Settings.ReaderPageTheme switch { "Sepia" => 1, "Night" => 2, _ => 0 };
        (_theme switch { 1 => SepiaItem, 2 => NightItem, _ => PaperItem }).IsChecked = true;
        SetContentsOpen(AppServices.Settings.ReaderContentsOpen, remember: false);
        AddAccelerators();
    }

    // ================================================================ opening

    public int PageCount => _book?.PageCount ?? 0;
    public int CurrentPage => _currentPage;

    /// <summary>Whether reaching the last page offers "Mark as finished". The window says, from the book's status.</summary>
    public bool OfferFinish { get; set; } = true;

    public async Task OpenAsync(string path, ReadingPosition? position)
    {
        _path = path;
        _pendingPosition = position;
        ShowMessage("Opening the book...", "", ring: true);
        await OpenWithAsync(null);
    }

    private async Task OpenWithAsync(string? password)
    {
        var clock = Stopwatch.StartNew();
        var (book, status) = await Task.Run(() => PdfBook.Open(_path, password));
        if (_closed)
        {
            book?.Dispose();
            return;
        }

        if (status == PdfOpenStatus.NeedsPassword)
        {
            ShowPassword(wrong: password is not null);
            return;
        }
        if (status != PdfOpenStatus.Ok || book is null)
        {
            Log.Write($"reader: could not open {_path}");
            ShowMessage("This PDF could not be opened",
                "The file may be damaged, or not really a PDF. The app it was made with, or another reader, may still open it.",
                ring: false, external: true);
            return;
        }

        _book = book;
        Log.Write($"reader: opened {System.IO.Path.GetFileName(_path)}, {book.PageCount} pages, in {clock.ElapsedMilliseconds} ms");
        HideMessage();
        BuildLayout();
        _queue = new RenderQueue(Render, (job, result) => DispatcherQueue.TryEnqueue(() => OnRendered(job, result)));
        _ = LoadOutlineAsync();
        _layoutPending = true;
        TryApplyInitialView();
        ProbeDefine();
    }

    private void BuildLayout()
    {
        var book = _book!;
        _maxWidthPts = book.PageSizes.Max(s => s.Width);
        _stack.Rebuild(book.PageCount, i =>
        {
            var (w, h) = book.PageSizes[i];
            var slot = BaseWidth * w / _maxWidthPts;
            return (slot, slot * h / w);
        });
        PageCanvas.Width = ContentWidth;
        PageCanvas.Height = _stack.Height + 2 * Gap;
        PageCountText.Text = $"of {book.PageCount:N0}";
    }

    /// <summary>Once the scroller has a size: the zoom and place the book was left at, or the top at fit width.</summary>
    private void TryApplyInitialView()
    {
        if (!_layoutPending || _book is null || Scroller.ViewportWidth <= 0 || Scroller.ViewportHeight <= 0) return;
        _layoutPending = false;

        int page = 0;
        double fraction = 0;
        float? zoom = null;
        if (_pendingPosition is { } p && p.Page >= 0 && p.Page < _book.PageCount)
        {
            page = p.Page;
            var parts = (p.Position ?? "").Split(';');
            if (parts.Length >= 4 && parts[0] == "v1")
            {
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
                _fit = parts[2] switch { "P" => Fit.Page, "C" => Fit.Custom, _ => Fit.Width };
                if (_fit == Fit.Custom && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) zoom = z;
            }
        }

        _currentPage = page;
        // Measured first: the canvas was only just given its size, and until a layout pass the scroller clamps
        // any offset to the old, empty extent, so every book opened at its first page.
        Scroller.UpdateLayout();
        GoTo(page, Math.Clamp(fraction, 0, 0.999), zoom ?? FitZoom(page));
        ShowPageNumber();
        PageChanged?.Invoke(page);
    }

    /// <summary>
    /// For checking Define without a mouse: ARYAN_READER_DEFINE=word shows the definition of the first place that
    /// word is on the page the book opens at, selected, as a right-click and Define would. Nothing happens without it.
    /// </summary>
    private async void ProbeDefine()
    {
        var word = Environment.GetEnvironmentVariable("ARYAN_READER_DEFINE");
        if (string.IsNullOrWhiteSpace(word) || _book is null) return;
        await Task.Delay(1500);
        for (int p = Math.Max(0, _currentPage); p < Math.Min(_book.PageCount, _currentPage + 3); p++)
        {
            if (Text(p) is not { } layer) continue;
            var at = layer.FindMatches(word, new SearchOptions(WholeWord: true));
            if (at.Count == 0) continue;
            var (start, length) = at[0];
            _anchor = (p, start);
            _focus = (p, start + length - 1);
            DrawAllMarks();
            if (layer.GetRangeRects(start, length) is { Count: > 0 } rects)
                BringIntoView(p, (rects[0].Left + rects[0].Width / 2) * TextScale(p), rects[0].Top * TextScale(p));
            ShowDefinition(layer.Text.Substring(start, length), (p, start, length, layer.Text.Substring(start, length)));
            return;
        }
        Log.Write($"reader: probe found no \"{word}\" near page {_currentPage + 1}");
    }

    private async Task LoadOutlineAsync()
    {
        var book = _book;
        if (book is null) return;
        var outline = await Task.Run(book.ReadOutline);
        if (_closed) return;
        OutlineList.ItemsSource = outline.Select(e => new OutlineRow(e)).ToList();
        NoOutlineText.Visibility = outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- messages: opening, password, failure ----

    private void ShowMessage(string title, string text, bool ring, bool external = false)
    {
        MessagePanel.Visibility = Visibility.Visible;
        MessageRing.IsActive = ring;
        MessageRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Visibility = PasswordButton.Visibility = Visibility.Collapsed;
        ExternalButton.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPassword(bool wrong)
    {
        ShowMessage("This book is protected",
            wrong ? "That password did not open it. Try again." : "It needs a password to be opened.", ring: false);
        PasswordInput.Visibility = PasswordButton.Visibility = Visibility.Visible;
        PasswordInput.Password = "";
        PasswordInput.Focus(FocusState.Programmatic);
    }

    private void HideMessage()
    {
        MessagePanel.Visibility = Visibility.Collapsed;
        MessageRing.IsActive = false;
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        OnPasswordSubmit(sender, e);
    }

    private void OnPasswordSubmit(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length == 0) return;
        var password = PasswordInput.Password;
        ShowMessage("Opening the book...", "", ring: true);
        _ = OpenWithAsync(password);
    }

    private void OnOpenExternally(object sender, RoutedEventArgs e) => OpenExternallyRequested?.Invoke();

    // ================================================================ geometry

    private static double ContentWidth => BaseWidth + 2 * Side;
    private double SlotWidth(int p) => _stack.WidthOf(p);
    private double SlotHeight(int p) => _stack.HeightOf(p);
    private double SlotLeft(int p) => Side + (BaseWidth - _stack.WidthOf(p)) / 2;
    private double SlotTop(int p) => Gap + _stack.TopOf(p);

    private double RasterScale => XamlRoot?.RasterizationScale is > 0 and var s ? s : 1.0;

    /// <summary>The level a page needs to look sharp at this zoom: the first whose width covers its pixels on screen.</summary>
    private int LevelFor(int page, double zoom) =>
        Math.Min(TileGrid.LevelForWidth(SlotWidth(page) * zoom * RasterScale), 6);

    /// <summary>The page under a point of the canvas, and the point in that page's own slot DIPs.</summary>
    private (int Page, Point Local)? HitPage(Point canvas, bool nearest = false)
    {
        if (_book is null) return null;
        var y = canvas.Y - Gap;
        var (first, _) = _stack.Range(y, y);
        if (first < 0)
        {
            if (!nearest) return null;
            // In a gap, above the first page or below the last: the page just above the point, or the first.
            var (_, above) = _stack.Range(0, Math.Max(0, y));
            first = above >= 0 ? above : 0;
        }
        var local = new Point(canvas.X - SlotLeft(first), canvas.Y - SlotTop(first));
        if (!nearest && (local.X < 0 || local.X > SlotWidth(first))) return null;
        return (first, local);
    }

    /// <summary>
    /// What part of the canvas the scroller shows, in canvas coordinates, worked out from its offsets and zoom.
    /// </summary>
    /// <remarks>
    /// ⚠️ NOT TransformToVisual. That lags a ChangeView until the next layout pass: straight after the jump to where
    /// a book was left, it still described the top of the book, so the view built pages 1 and 2 while it showed
    /// page 76, and the reader saw a blank window. The offsets are right the moment ChangeView returns.
    /// The canvas is centred across when it is narrower than the view, and always starts at the top.
    /// </remarks>
    private Rect? VisibleRect()
    {
        var z = Scroller.ZoomFactor;
        if (z <= 0 || Scroller.ViewportWidth <= 0) return null;
        return new Rect((Scroller.HorizontalOffset - CenteringPad()) / z, Scroller.VerticalOffset / z,
            Scroller.ViewportWidth / z, Scroller.ViewportHeight / z);
    }

    /// <summary>How far the canvas is pushed right to centre it, in view pixels, when it is narrower than the view.</summary>
    private double CenteringPad() => Math.Max(0, (Scroller.ViewportWidth - ContentWidth * Scroller.ZoomFactor) / 2);

    /// <summary>A rectangle of the canvas where it is in the view (the Surface), by the same arithmetic.</summary>
    private Rect CanvasToView(Rect r)
    {
        var z = Scroller.ZoomFactor;
        return new Rect(r.X * z - Scroller.HorizontalOffset + CenteringPad(), r.Y * z - Scroller.VerticalOffset,
            r.Width * z, r.Height * z);
    }

    // ================================================================ zoom and position

    private double PercentPerZoom => BaseWidth / (_maxWidthPts * 96.0 / 72.0) * 100.0;

    private float FitZoom(int page) => _fit switch
    {
        Fit.Page => (float)Math.Min(Scroller.ViewportWidth / ContentWidth,
            Scroller.ViewportHeight / (SlotHeight(Math.Max(0, page)) + 2 * Gap)),
        _ => (float)(Scroller.ViewportWidth / ContentWidth),
    };

    /// <summary>Scrolls so <paramref name="fraction"/> of the way down <paramref name="page"/> is at the top.</summary>
    private void GoTo(int page, double fraction, float? zoom = null)
    {
        if (_book is null) return;
        page = Math.Clamp(page, 0, _book.PageCount - 1);
        var z = Math.Clamp(zoom ?? Scroller.ZoomFactor, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        var y = fraction <= 0 ? SlotTop(page) - Gap / 2 : SlotTop(page) + fraction * SlotHeight(page);
        // Across, the middle of what is shown now stays in the middle: zooming in from a page that fits goes into
        // its centre, not its left edge.
        var width = ContentWidth * z;
        var middle = VisibleRect() is { } view ? view.Left + view.Width / 2 : ContentWidth / 2;
        var h = width > Scroller.ViewportWidth ? Math.Max(0, middle * z - Scroller.ViewportWidth / 2) : 0;
        _expectedZoom = z;
        Scroller.ChangeView(h, Math.Max(0, y * z), z, true);
    }

    public void GoToPage(int page) => GoTo(page, 0);

    /// <summary>The page at the top of the view and how far down it the view starts (0..1).</summary>
    private (int Page, double Fraction) TopPosition()
    {
        if (_book is null) return (0, 0);
        var top = Scroller.VerticalOffset / Scroller.ZoomFactor - Gap;
        var (page, _) = _stack.Range(top, top);
        if (page < 0)
        {
            // The top of the view is in the gap between two pages (fit page puts it there): the page below it, from
            // its top. Past the last page, the last page.
            (page, _) = _stack.Range(top, double.MaxValue);
            return (page < 0 ? _book.PageCount - 1 : page, 0);
        }
        return (page, Math.Clamp((top - _stack.TopOf(page)) / SlotHeight(page), 0, 0.999));
    }

    /// <summary>Where the reader is, for the book to open there again.</summary>
    public ReadingPosition? Position()
    {
        if (_book is null) return null;
        var (page, fraction) = TopPosition();
        var fit = _fit switch { Fit.Page => "P", Fit.Custom => "C", _ => "W" };
        var detail = string.Create(CultureInfo.InvariantCulture, $"v1;{fraction:0.####};{fit};{Scroller.ZoomFactor:0.####}");
        return new ReadingPosition(page, _book.PageCount, detail);
    }

    private void SetZoom(float zoom, Fit fit)
    {
        if (_book is null) return;
        _fit = fit;
        var (page, fraction) = TopPosition();
        GoTo(page, fraction, zoom);
    }

    private static readonly int[] ZoomSteps = { 10, 25, 33, 50, 67, 75, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500, 600, 800 };

    private void StepZoom(int direction)
    {
        if (_book is null) return;
        var percent = Scroller.ZoomFactor * PercentPerZoom;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > percent + 0.5, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(s => s < percent - 0.5, ZoomSteps[0]);
        SetZoom((float)(next / PercentPerZoom), Fit.Custom);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => StepZoom(+1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void OnFitWidth(object sender, RoutedEventArgs e)
    {
        _fit = Fit.Width;
        SetZoom(FitZoom(_currentPage), Fit.Width);
    }

    private void OnFitPage(object sender, RoutedEventArgs e)
    {
        _fit = Fit.Page;
        var page = Math.Max(0, _currentPage);
        _expectedZoom = FitZoom(page);
        GoTo(page, 0, FitZoom(page));
    }

    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string tag && int.TryParse(tag, out var percent))
            SetZoom((float)(percent / PercentPerZoom), Fit.Custom);
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutPending)
        {
            TryApplyInitialView();
            return;
        }
        if (_book is null) return;
        if (_fit == Fit.Custom)
        {
            UpdateView(final: true);
            return;
        }
        var (page, fraction) = TopPosition();
        GoTo(page, fraction, FitZoom(page));
    }

    private void ShowZoom()
    {
        ZoomButton.Content = _fit switch
        {
            Fit.Width => "Fit width",
            Fit.Page => "Fit page",
            _ => $"{Math.Round(Scroller.ZoomFactor * PercentPerZoom):0}%",
        };
    }

    // ================================================================ the view: which pages exist, what to render

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_book is null || _layoutPending) return;

        var zoom = Scroller.ZoomFactor;
        if (!e.IsIntermediate)
        {
            // Ctrl+wheel and pinch zoom the scroller directly, and then the fit no longer holds.
            var ours = _expectedZoom > 0 && Math.Abs(zoom - _expectedZoom) < 0.002;
            if (!ours && Math.Abs(zoom - _lastZoom) > 0.002) _fit = Fit.Custom;
            _lastZoom = zoom;
            _expectedZoom = -1;
            ShowZoom();
        }
        UpdateView(final: !e.IsIntermediate);
        PlaceDefinition();
    }

    /// <summary>
    /// Makes cards for the pages in and near the view, drops the rest, and hands the render queue a new plan:
    /// the rough page first so nothing is blank, then the sharp page (or its tiles, zoomed deep), current page first.
    /// </summary>
    private bool _updating;
    private bool _updateAgain;
    private bool _againFinal;

    /// <summary>
    /// ⚠️ NEVER RE-ENTERED. A ChangeView with animation off raises ViewChanged synchronously, so an update can arrive
    /// in the middle of another one; the inner one dropped cards the outer one was still walking, and the reader
    /// crashed with a missing page (measured: page 61 of the Wi-Fi book, on a jump to the middle). A call that
    /// arrives during an update is run once the update is done.
    /// </summary>
    private void UpdateView(bool final)
    {
        if (_updating)
        {
            _updateAgain = true;
            _againFinal |= final;
            return;
        }
        _updating = true;
        try
        {
            UpdateViewOnce(final);
            for (int i = 0; i < 4 && _updateAgain; i++)
            {
                var again = _againFinal;
                _updateAgain = _againFinal = false;
                UpdateViewOnce(again);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateViewOnce(bool final)
    {
        if (_book is null || VisibleRect() is not { } view) return;
        var zoom = Scroller.ZoomFactor;

        var (first, last) = _stack.Range(view.Top - Gap, view.Bottom - Gap);
        var ahead = view.Height;
        var (keepFirst, keepLast) = _stack.Range(view.Top - Gap - ahead, view.Bottom - Gap + ahead);
        if (keepFirst < 0)
        {
            (keepFirst, keepLast) = (first, last);
            if (keepFirst < 0) return;
        }

        foreach (var page in _cards.Keys.Where(p => p < keepFirst - 1 || p > keepLast + 1).ToList()) DropCard(page);
        for (int p = keepFirst; p <= keepLast; p++)
            if (!_cards.ContainsKey(p)) MakeCard(p);

        // The current page: the one across the middle of the view, or the first one in it.
        var middle = HitPage(new Point(ContentWidth / 2, view.Top + view.Height * 0.4), nearest: true)?.Page ?? first;
        if (middle >= 0 && middle != _currentPage)
        {
            _currentPage = middle;
            ShowPageNumber();
            PageChanged?.Invoke(middle);
            if (middle == _book.PageCount - 1 && OfferFinish && !_endOffered && _book.PageCount > 1)
            {
                _endOffered = true;
                EndBar.IsOpen = true;
            }
        }

        var visible = first < 0 ? new List<int>() : Enumerable.Range(first, last - first + 1)
            .OrderBy(p => Math.Abs(p - _currentPage)).ToList();
        var near = Enumerable.Range(keepFirst, keepLast - keepFirst + 1).Where(p => !visible.Contains(p))
            .OrderBy(p => Math.Abs(p - _currentPage)).ToList();

        var plan = new List<RenderJob>();
        var gen = _themeGeneration;
        foreach (var p in visible.Concat(near))
            if (_cards.TryGetValue(p, out var c) && c.BaseTheme != gen) plan.Add(new RenderJob(p, 0, -1, -1, TileGrid.TileSize, gen));

        foreach (var p in visible.Concat(near))
        {
            if (!_cards.TryGetValue(p, out var card))
            {
                Log.Write($"reader: page {p + 1} has no card during an update");
                continue;
            }
            var level = LevelFor(p, zoom);
            var pageLevel = Math.Min(level, WholePageLevels);

            // A sharp page much finer than needed now (zoomed far out) goes, so a view of many pages does not
            // hold a big bitmap for each.
            if (card.FineLevel > pageLevel + 1)
            {
                card.Fine.Source = null;
                card.FineLevel = -1;
            }
            if (level <= WholePageLevels && card.TileLevel >= 0) ClearTiles(card);

            if (pageLevel >= 1 && (card.FineLevel < pageLevel || card.FineTheme != gen))
                plan.Add(new RenderJob(p, pageLevel, -1, -1, TileGrid.TileSize << pageLevel, gen));

            if (level > WholePageLevels && final && visible.Contains(p))
            {
                var local = new Rect(view.Left - SlotLeft(p), view.Top - SlotTop(p), view.Width, view.Height);
                foreach (var t in TileGrid.VisibleTiles(SlotWidth(p), SlotHeight(p), level,
                             local.Left, local.Top, local.Right, local.Bottom))
                {
                    if (card.TileLevel == level && card.TileImages.ContainsKey((t.Address.Col, t.Address.Row))) continue;
                    plan.Add(new RenderJob(p, level, t.Address.Col, t.Address.Row, TileGrid.TileSize, gen));
                }
            }
        }

        _queue?.SetPlan(plan);
    }

    private void MakeCard(int page)
    {
        var width = SlotWidth(page);
        var height = SlotHeight(page);
        Image NewImage() => new() { Width = width, Height = height, Stretch = Stretch.Fill, UseLayoutRounding = false };

        var card = new PageCard
        {
            Page = page,
            Root = new Grid { Width = width, Height = height, UseLayoutRounding = false },
            Sheet = new Border
            {
                Background = new SolidColorBrush(PageColor()),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(-1),
            },
            Base = NewImage(),
            Fine = NewImage(),
            Tiles = new Canvas { UseLayoutRounding = false },
            Marks = new Canvas { UseLayoutRounding = false, IsHitTestVisible = false },
            Links = new Canvas { UseLayoutRounding = false },
        };
        // Tiles on the last row and column run past the page; they are cut at its edge exactly, or the white they
        // carry beyond it shows as hairlines in the gap below.
        card.Tiles.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        card.Root.Children.Add(card.Sheet);
        card.Root.Children.Add(card.Base);
        card.Root.Children.Add(card.Fine);
        card.Root.Children.Add(card.Tiles);
        card.Root.Children.Add(card.Marks);
        card.Root.Children.Add(card.Links);
        Canvas.SetLeft(card.Root, SlotLeft(page));
        Canvas.SetTop(card.Root, SlotTop(page));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card.Root, $"Page {page + 1}");
        PageCanvas.Children.Add(card.Root);
        _cards[page] = card;

        DrawMarks(card);
        _ = LoadLinksAsync(card);
    }

    private void DropCard(int page)
    {
        if (!_cards.Remove(page, out var card)) return;
        card.Base.Source = null;
        card.Fine.Source = null;
        ClearTiles(card);
        PageCanvas.Children.Remove(card.Root);
    }

    private static void ClearTiles(PageCard card)
    {
        card.Tiles.Children.Clear();
        card.TileImages.Clear();
        card.TileLevel = -1;
    }

    // ================================================================ rendering

    /// <summary>On the render thread: the pixels for a job, coloured for the page theme it was asked in.</summary>
    private (int, int, byte[])? Render(RenderJob job)
    {
        var book = _book;
        if (book is null || job.Theme != _themeGeneration) return null;
        var result = job.IsTile ? book.RenderTile(job.Page, job.Level, job.Col, job.Row) : book.RenderPage(job.Page, job.Width);
        if (result is not { } r) return null;
        PageColors.Apply(r.Bgra, _theme);
        return r;
    }

    /// <summary>On the UI thread: puts a render in its card, if the card still wants it.</summary>
    private void OnRendered(RenderJob job, (int Width, int Height, byte[] Bgra) result)
    {
        if (_book is null || job.Theme != _themeGeneration || !_cards.TryGetValue(job.Page, out var card)) return;

        if (job.IsTile)
        {
            if (job.Level != LevelFor(job.Page, Scroller.ZoomFactor)) return;
            if (card.TileLevel != job.Level)
            {
                ClearTiles(card);
                card.TileLevel = job.Level;
            }
            if (card.TileImages.ContainsKey((job.Col, job.Row))) return;
            var dip = TileGrid.TileDip(SlotWidth(job.Page), job.Level);
            var image = new Image
            {
                Source = ToBitmap(result),
                Width = dip,
                Height = dip,
                Stretch = Stretch.Fill,
                UseLayoutRounding = false,
            };
            Canvas.SetLeft(image, job.Col * dip);
            Canvas.SetTop(image, job.Row * dip);
            card.Tiles.Children.Add(image);
            card.TileImages[(job.Col, job.Row)] = image;
        }
        else if (job.Level == 0)
        {
            card.Base.Source = ToBitmap(result);
            card.BaseTheme = job.Theme;
        }
        else if (job.Level >= card.FineLevel || card.FineTheme != job.Theme)
        {
            card.Fine.Source = ToBitmap(result);
            card.FineLevel = job.Level;
            card.FineTheme = job.Theme;
        }
    }

    private static WriteableBitmap ToBitmap((int Width, int Height, byte[] Bgra) r)
    {
        var bitmap = new WriteableBitmap(r.Width, r.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(r.Bgra, 0, Math.Min(r.Width * r.Height * 4, r.Bgra.Length));
        bitmap.Invalidate();
        return bitmap;
    }

    // ---- page colour ----

    private Windows.UI.Color PageColor() => _theme switch
    {
        1 => PageColors.SepiaPaper,
        2 => Windows.UI.Color.FromArgb(0xFF, NightMode.Floor, NightMode.Floor, NightMode.Floor),
        _ => Colors.White,
    };

    private void OnPageTheme(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag is not string tag) return;
        var theme = tag switch { "Sepia" => 1, "Night" => 2, _ => 0 };
        if (theme == _theme) return;
        _theme = theme;
        AppServices.Settings.ReaderPageTheme = tag;
        AppServices.Settings.Save();

        _themeGeneration++;
        foreach (var card in _cards.Values)
        {
            ((SolidColorBrush)card.Sheet.Background).Color = PageColor();
            card.Base.Source = null;
            card.Fine.Source = null;
            card.BaseTheme = card.FineTheme = card.FineLevel = -1;
            ClearTiles(card);
        }
        UpdateView(final: true);
    }

    // ================================================================ page number and moving by pages

    private void ShowPageNumber()
    {
        if (_currentPage >= 0 && !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), PageBox))
            PageBox.Text = (_currentPage + 1).ToString(CultureInfo.CurrentCulture);
    }

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            PageBox.Text = (_currentPage + 1).ToString(CultureInfo.CurrentCulture);
            Scroller.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && _book is not null)
            GoToPage(Math.Clamp(n, 1, _book.PageCount) - 1);
        Scroller.Focus(FocusState.Programmatic);
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) =>
        PageBox.Text = _currentPage >= 0 ? (_currentPage + 1).ToString(CultureInfo.CurrentCulture) : "";

    private void OnPreviousPage(object sender, RoutedEventArgs e) => GoToPage(Math.Max(0, _currentPage - 1));

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        if (_book is not null) GoToPage(Math.Min(_book.PageCount - 1, _currentPage + 1));
    }

    private void ScrollByScreen(int direction)
    {
        var step = Scroller.ViewportHeight * 0.9;
        Scroller.ChangeView(null, Math.Max(0, Scroller.VerticalOffset + direction * step), null, false);
    }

    private void OnScrollerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Space:
                ScrollByScreen(shift ? -1 : 1);
                e.Handled = true;
                break;
            case VirtualKey.Left when Scroller.ScrollableWidth <= 0:
                OnPreviousPage(sender, e);
                e.Handled = true;
                break;
            case VirtualKey.Right when Scroller.ScrollableWidth <= 0:
                OnNextPage(sender, e);
                e.Handled = true;
                break;
        }
    }

    public void SetTimeLeft(string text) => TimeLeftText.Text = text;

    private void OnMarkFinished(object sender, RoutedEventArgs e)
    {
        EndBar.IsOpen = false;
        FinishedRequested?.Invoke();
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    /// <summary>The toolbar gives way on a narrow window: the time left, then the search box's width, then the zoom.</summary>
    private void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        TimeLeftText.Visibility = w < 900 ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Width = w < 760 ? 130 : 200;
        SearchCountText.Visibility = w < 700 ? Visibility.Collapsed : Visibility.Visible;
        ZoomGroup.Visibility = w < 600 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================================================================ contents

    private void OnContentsClick(object sender, RoutedEventArgs e) => SetContentsOpen(ContentsButton.IsChecked == true, remember: true);

    private void SetContentsOpen(bool open, bool remember)
    {
        ContentsButton.IsChecked = open;
        ContentsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!remember) return;
        AppServices.Settings.ReaderContentsOpen = open;
        AppServices.Settings.Save();
    }

    private void OnOutlineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineRow { Page: >= 0 } row) GoToPage(row.Page);
    }

    // ================================================================ links

    private async Task LoadLinksAsync(PageCard card)
    {
        var book = _book;
        if (book is null) return;
        var links = await Task.Run(() => book.ReadLinks(card.Page));
        if (_book != book || !_cards.TryGetValue(card.Page, out var still) || still != card) return;

        var width = SlotWidth(card.Page);
        foreach (var link in links)
        {
            var area = new LinkArea
            {
                Width = Math.Max(4, (link.Right - link.Left) * width),
                Height = Math.Max(4, (link.Bottom - link.Top) * width),
                Background = new SolidColorBrush(Colors.Transparent),
            };
            Canvas.SetLeft(area, link.Left * width);
            Canvas.SetTop(area, link.Top * width);
            ToolTipService.SetToolTip(area, link.TargetPage >= 0 ? $"Go to page {link.TargetPage + 1}" : link.Uri);
            area.PointerPressed += (_, e) => e.Handled = true;   // a click on a link is not the start of a selection
            area.Tapped += (_, _) => FollowLink(link);
            card.Links.Children.Add(area);
        }
    }

    private void FollowLink(PdfLink link)
    {
        if (link.TargetPage >= 0)
        {
            GoToPage(link.TargetPage);
            return;
        }
        // Web and mail addresses only: a PDF can also carry "launch" and file links, which are not followed.
        if (Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            _ = Launcher.LaunchUriAsync(uri);
    }

    // ================================================================ text: layers, selection, copy

    private readonly Dictionary<int, PageTextLayer> _text = new();
    private readonly object _textGate = new();

    /// <summary>
    /// A page's text, in pixels of a render as wide as its slot (so, near enough, in slot DIPs; see
    /// <see cref="TextScale"/>). Read once and kept; any thread.
    /// </summary>
    private PageTextLayer? Text(int page)
    {
        lock (_textGate)
        {
            if (_text.TryGetValue(page, out var known)) return known;
        }
        var layer = _book?.ReadText(page, TextWidth(page));
        if (layer is null) return null;
        lock (_textGate)
        {
            if (_text.Count > 400) _text.Clear();
            _text[page] = layer;
        }
        return layer;
    }

    private int TextWidth(int page) => Math.Max(1, (int)Math.Round(SlotWidth(page)));

    /// <summary>Text-layer units to slot DIPs for a page.</summary>
    private double TextScale(int page) => SlotWidth(page) / TextWidth(page);

    private (int Page, int Index)? _anchor;
    private (int Page, int Index)? _focus;
    private bool _selecting;
    private Point _pressedAt;

    private void OnPagePressed(object sender, PointerRoutedEventArgs e)
    {
        HideDefinition();
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(PageCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;

        Scroller.Focus(FocusState.Pointer);
        ClearSelection();
        if (HitPage(point.Position) is not { } hit || Text(hit.Page) is not { CharCount: > 0 } layer) return;

        var scale = TextScale(hit.Page);
        _anchor = (hit.Page, layer.HitTestNearest(hit.Local.X / scale, hit.Local.Y / scale));
        _selecting = true;
        _pressedAt = point.Position;
        PageCanvas.CapturePointer(e.Pointer);
    }

    private void OnPageMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        var at = e.GetCurrentPoint(PageCanvas).Position;
        if (_focus is null && Math.Abs(at.X - _pressedAt.X) + Math.Abs(at.Y - _pressedAt.Y) < 4) return;
        if (HitPage(at, nearest: true) is not { } hit || Text(hit.Page) is not { CharCount: > 0 } layer) return;
        var scale = TextScale(hit.Page);
        var focus = (hit.Page, layer.HitTestNearest(hit.Local.X / scale, hit.Local.Y / scale));
        if (_focus == focus) return;
        _focus = focus;
        DrawAllMarks();
    }

    private void OnPageReleased(object sender, PointerRoutedEventArgs e)
    {
        _selecting = false;
        PageCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnPageCaptureLost(object sender, PointerRoutedEventArgs e) => _selecting = false;

    private void OnPageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (WordAt(e.GetPosition(PageCanvas)) is not { } word) return;
        _anchor = (word.Page, word.Start);
        _focus = (word.Page, word.Start + word.Length - 1);
        DrawAllMarks();
    }

    private void ClearSelection()
    {
        var had = _focus is not null;
        _anchor = _focus = null;
        if (had) DrawAllMarks();
    }

    /// <summary>The selection in reading order, or null when nothing is selected.</summary>
    private ((int Page, int Index) Start, (int Page, int Index) End)? Selection()
    {
        if (_anchor is not { } a || _focus is not { } f) return null;
        return (a.Page < f.Page || (a.Page == f.Page && a.Index <= f.Index)) ? (a, f) : (f, a);
    }

    /// <summary>The selected part of one page's text, as a range, or null.</summary>
    private (int Start, int Length)? SelectedOn(int page, PageTextLayer layer)
    {
        if (Selection() is not { } s || page < s.Start.Page || page > s.End.Page) return null;
        var start = page == s.Start.Page ? s.Start.Index : 0;
        var end = page == s.End.Page ? s.End.Index + 1 : layer.CharCount;
        end = Math.Min(end, layer.CharCount);
        return end > start ? (start, end - start) : null;
    }

    public string SelectedText()
    {
        if (Selection() is not { } s) return "";
        var parts = new List<string>();
        for (int p = s.Start.Page; p <= s.End.Page && p - s.Start.Page < 500; p++)
        {
            if (Text(p) is not { } layer || SelectedOn(p, layer) is not { } range) continue;
            parts.Add(layer.Text.Substring(range.Start, range.Length));
        }
        return string.Join(Environment.NewLine, parts).Trim();
    }

    private void CopySelection()
    {
        var text = SelectedText();
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---- highlights: search matches under the selection ----

    private void DrawAllMarks()
    {
        foreach (var card in _cards.Values) DrawMarks(card);
    }

    private void DrawMarks(PageCard card)
    {
        card.Marks.Children.Clear();
        var hasMatches = _matches.Count > 0 && MatchesOn(card.Page).Any();
        var selected = Selection() is { } s && card.Page >= s.Start.Page && card.Page <= s.End.Page;
        if (!hasMatches && !selected) return;
        if (Text(card.Page) is not { } layer) return;
        var scale = TextScale(card.Page);

        void Add(int start, int length, Windows.UI.Color color)
        {
            foreach (var r in layer.GetRangeRects(start, length))
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(1, r.Width * scale),
                    Height = Math.Max(1, r.Height * scale),
                    Fill = new SolidColorBrush(color),
                };
                Canvas.SetLeft(rect, r.Left * scale);
                Canvas.SetTop(rect, r.Top * scale);
                card.Marks.Children.Add(rect);
            }
        }

        foreach (var m in MatchesOn(card.Page))
        {
            var current = _matchIndex >= 0 && _matchIndex < _matches.Count && _matches[_matchIndex] == m;
            Add(m.Start, m.Length, current ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0x8C, 0x00) : Windows.UI.Color.FromArgb(0x55, 0xFF, 0xD7, 0x00));
        }
        if (SelectedOn(card.Page, layer) is { } range) Add(range.Start, range.Length, Windows.UI.Color.FromArgb(0x55, 0x1E, 0x78, 0xE6));
    }

    // ================================================================ right-click: define, copy, find

    private (int Page, int Start, int Length, string Text)? WordAt(Point canvas)
    {
        if (HitPage(canvas) is not { } hit || Text(hit.Page) is not { CharCount: > 0 } layer) return null;
        var scale = TextScale(hit.Page);
        if (layer.WordAt(hit.Local.X / scale, hit.Local.Y / scale) is not { } w) return null;
        return (hit.Page, w.Start, w.Length, layer.Text.Substring(w.Start, w.Length));
    }

    private void OnPageRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var at = e.GetPosition(PageCanvas);
        var word = WordAt(at);
        var selected = SelectedText();
        var menu = new MenuFlyout();

        // Define: the selection when it is one English word, else the English word under the pointer.
        string? define = EnglishWord.TryNormalize(selected, out var s) ? s
            : selected.Length == 0 && word is { } w && EnglishWord.TryNormalize(w.Text, out var ww) ? ww : null;
        if (define is not null)
        {
            var anchorWord = word;
            var item = new MenuFlyoutItem { Text = $"Define \u201C{define}\u201D", Icon = new FontIcon { Glyph = "\uE82D" } };
            item.Click += (_, _) => ShowDefinition(define, anchorWord);
            menu.Items.Add(item);
        }

        if (selected.Length > 0)
        {
            var copy = new MenuFlyoutItem { Text = "Copy", Icon = new SymbolIcon(Symbol.Copy) };
            copy.KeyboardAcceleratorTextOverride = "Ctrl+C";
            copy.Click += (_, _) => CopySelection();
            menu.Items.Add(copy);
        }
        else if (word is { } wc)
        {
            var copy = new MenuFlyoutItem { Text = $"Copy \u201C{Shorten(wc.Text)}\u201D", Icon = new SymbolIcon(Symbol.Copy) };
            copy.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(wc.Text);
                Clipboard.SetContent(package);
            };
            menu.Items.Add(copy);
        }

        var findText = selected.Length is > 0 and <= 80 && !selected.Contains('\n') ? selected : word?.Text;
        if (!string.IsNullOrWhiteSpace(findText))
        {
            var find = new MenuFlyoutItem { Text = $"Find \u201C{Shorten(findText)}\u201D in this book", Icon = new SymbolIcon(Symbol.Find) };
            find.Click += (_, _) =>
            {
                SearchBox.Text = findText.Trim();
                StartSearch();
            };
            menu.Items.Add(find);
        }

        if (menu.Items.Count == 0) return;
        e.Handled = true;
        menu.ShowAt(PageCanvas, new FlyoutShowOptions { Position = at });
    }

    private static string Shorten(string text) => text.Length <= 24 ? text : text[..22] + "...";

    // ================================================================ Define (Ayaan PDF's popup)

    private (int Page, Rect Box)? _defineAnchor;
    private int _defineRequest;

    private async void ShowDefinition(string word, (int Page, int Start, int Length, string Text)? at)
    {
        var request = ++_defineRequest;
        _defineAnchor = null;
        if (at is { } a && Text(a.Page) is { } layer)
        {
            var rects = layer.GetRangeRects(a.Start, a.Length);
            if (rects.Count > 0)
            {
                var scale = TextScale(a.Page);
                var r = rects[0];
                _defineAnchor = (a.Page, new Rect(r.Left * scale, r.Top * scale, r.Width * scale, r.Height * scale));
            }
        }
        else if (Selection() is { } s && Text(s.Start.Page) is { } sl && SelectedOn(s.Start.Page, sl) is { } range)
        {
            var rects = sl.GetRangeRects(range.Start, range.Length);
            if (rects.Count > 0)
            {
                var scale = TextScale(s.Start.Page);
                var r = rects[0];
                _defineAnchor = (s.Start.Page, new Rect(r.Left * scale, r.Top * scale, r.Width * scale, r.Height * scale));
            }
        }

        DefinitionWord.Text = word;
        var english = DefinitionDictionary.LoadAsync();
        var myanmar = AppServices.Settings.DefineShowsMyanmar ? DefinitionDictionary.LoadMyanmarAsync() : Task.FromResult<MyanmarGlosses?>(null);
        var hindi = AppServices.Settings.DefineShowsHindi ? DefinitionDictionary.LoadHindiAsync() : Task.FromResult<HindiGlosses?>(null);
        if (!english.IsCompleted || !myanmar.IsCompleted || !hindi.IsCompleted)
        {
            DefinitionText.Text = "Looking up...";
            DefinitionRule.Visibility = DefinitionMyanmar.Visibility = DefinitionHindi.Visibility = Visibility.Collapsed;
            DefinitionPopup.Visibility = Visibility.Visible;
            PlaceDefinition();
        }

        var dictionary = await english;
        var glosses = await myanmar;
        var hindiGlosses = await hindi;
        if (request != _defineRequest || _closed) return;

        DefinitionPopup.Visibility = Visibility.Visible;
        FillDefinition(word, dictionary, glosses, hindiGlosses);
        PlaceDefinition();
    }

    /// <summary>
    /// A glance, not an entry: up to three parts of speech, two senses each, one example, then the Myanmar and Hindi
    /// meanings of the same headword and part of speech. Ayaan PDF's FillDefinition, as it is there.
    /// </summary>
    private void FillDefinition(string word, WordDefinitions? dictionary, MyanmarGlosses? glosses, HindiGlosses? hindi)
    {
        DefinitionMyanmar.Inlines.Clear();
        DefinitionHindi.Inlines.Clear();
        DefinitionRule.Visibility = DefinitionMyanmar.Visibility = DefinitionHindi.Visibility = Visibility.Collapsed;

        if (dictionary is null)
        {
            DefinitionText.Text = "The dictionary could not be loaded.";
            return;
        }

        void AddLine(TextBlock block, string label, IReadOnlyList<string> meanings)
        {
            var isMyanmar = ReferenceEquals(block, DefinitionMyanmar);
            if (block.Inlines.Count > 0) block.Inlines.Add(new LineBreak());
            block.Inlines.Add(new Run { Text = label + ": ", FontWeight = FontWeights.SemiBold });
            block.Inlines.Add(new Run
            {
                Text = string.Join(isMyanmar ? "\u104A " : ", ", meanings),
                FontFamily = new FontFamily(isMyanmar ? "Pyidaungsu, Myanmar Text" : "Nirmala UI"),
            });
        }

        void ShowTranslations(int myanmarLines, int hindiLines)
        {
            DefinitionRule.Visibility = myanmarLines + hindiLines > 0 ? Visibility.Visible : Visibility.Collapsed;
            DefinitionMyanmar.Visibility = myanmarLines > 0 ? Visibility.Visible : Visibility.Collapsed;
            DefinitionHindi.Visibility = hindiLines > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (dictionary.Lookup(word) is not { } found)
        {
            // No English entry, but the Myanmar and Hindi lists may still know the word. Grammar words stay refused.
            var grammarWord = WordDefinitions.IsGrammarWord(word);
            var none = Array.Empty<(string PartOfSpeech, IReadOnlyList<string> Meanings)>();
            var myanmarOnly = grammarWord || glosses is null ? none : glosses.ForWord(word);
            var hindiOnly = grammarWord || hindi is null ? none : hindi.ForWord(word);
            if (myanmarOnly.Count + hindiOnly.Count == 0)
            {
                DefinitionText.Text = $"No definition found for \u201C{word}\u201D.";
                return;
            }
            DefinitionText.Text = "No English definition.";
            foreach (var (pos, meanings) in myanmarOnly) AddLine(DefinitionMyanmar, pos, meanings);
            foreach (var (pos, meanings) in hindiOnly) AddLine(DefinitionHindi, pos, meanings);
            ShowTranslations(myanmarOnly.Count, hindiOnly.Count);
            return;
        }

        string LabelFor(WordSense sense) =>
            string.Equals(sense.Headword, word, StringComparison.OrdinalIgnoreCase) ? sense.PartOfSpeech : $"{sense.PartOfSpeech}, {sense.Headword}";

        // Inlines.Clear, not Text = "": empty text leaves an empty Run behind that counts as a first line.
        DefinitionText.Inlines.Clear();
        foreach (var sense in found.Senses)
        {
            if (DefinitionText.Inlines.Count > 0) DefinitionText.Inlines.Add(new LineBreak());
            var meaning = sense.Definitions.Count > 1 ? $"{sense.Definitions[0]}; {sense.Definitions[1]}" : sense.Definitions[0];
            DefinitionText.Inlines.Add(new Run { Text = LabelFor(sense) + ": ", FontWeight = FontWeights.SemiBold });
            DefinitionText.Inlines.Add(new Run { Text = meaning });
            if (ReferenceEquals(sense, found.Senses[0]) && sense.Example is { Length: > 0 } example)
            {
                DefinitionText.Inlines.Add(new LineBreak());
                DefinitionText.Inlines.Add(new Run { Text = "\u201C" + example + "\u201D", FontStyle = Windows.UI.Text.FontStyle.Italic });
            }
        }

        var myanmarLines = 0;
        foreach (var sense in found.Senses)
        {
            if (glosses?.For(sense.Headword, sense.PartOfSpeech) is not { Count: > 0 } meanings) continue;
            myanmarLines++;
            AddLine(DefinitionMyanmar, LabelFor(sense), meanings);
        }
        var hindiLines = 0;
        foreach (var sense in found.Senses)
        {
            if (hindi?.For(sense.Headword, sense.PartOfSpeech) is not { Count: > 0 } meanings) continue;
            hindiLines++;
            AddLine(DefinitionHindi, LabelFor(sense), meanings);
        }
        ShowTranslations(myanmarLines, hindiLines);
    }

    private void OnDefinitionSizeChanged(object sender, SizeChangedEventArgs e) => PlaceDefinition();

    /// <summary>Above the word, below it when there is no room, always inside the view; hidden while the word is scrolled away.</summary>
    private void PlaceDefinition()
    {
        if (DefinitionPopup.Visibility != Visibility.Visible) return;
        var w = DefinitionPopup.ActualWidth;
        var h = DefinitionPopup.ActualHeight;
        var viewW = Surface.ActualWidth;
        var viewH = Surface.ActualHeight;

        Rect box;
        if (_defineAnchor is { } a && _cards.ContainsKey(a.Page))
        {
            box = CanvasToView(new Rect(SlotLeft(a.Page) + a.Box.X, SlotTop(a.Page) + a.Box.Y, a.Box.Width, a.Box.Height));
            if (box.Bottom < 0 || box.Top > viewH)
            {
                DefinitionPopup.Opacity = 0;
                return;
            }
        }
        else
        {
            box = new Rect(viewW / 2, viewH / 3, 0, 0);
        }

        var left = Math.Clamp(box.X, 12, Math.Max(12, viewW - w - 12));
        var top = box.Y - h - 10;
        if (top < 12) top = box.Bottom + 10;
        top = Math.Clamp(top, 12, Math.Max(12, viewH - h - 12));
        DefinitionPopup.Translation = new System.Numerics.Vector3((float)left, (float)top, 32);
        DefinitionPopup.Opacity = w > 0 ? 1 : 0;
    }

    private void HideDefinition()
    {
        if (DefinitionPopup.Visibility != Visibility.Visible) return;
        _defineRequest++;
        DefinitionPopup.Visibility = Visibility.Collapsed;
    }

    // ================================================================ find in book

    private readonly List<SearchMatch> _matches = new();
    private int _matchIndex = -1;
    private string _searchQuery = "";
    private CancellationTokenSource? _searchCts;
    private bool _searchDone;

    private IEnumerable<SearchMatch> MatchesOn(int page) => _matches.Where(m => m.PageIndex == page);

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = "";
            Scroller.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (SearchBox.Text.Trim() != _searchQuery) StartSearch();
        else StepMatch(shift ? -1 : 1);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length == 0) StopSearch();
    }

    private void OnSearchNext(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim() != _searchQuery) StartSearch();
        else StepMatch(1);
    }

    private void OnSearchPrevious(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim() != _searchQuery) StartSearch();
        else StepMatch(-1);
    }

    private void StopSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchQuery = "";
        _matches.Clear();
        _matchIndex = -1;
        SearchCountText.Text = "";
        DrawAllMarks();
    }

    /// <summary>
    /// Reads every page's text off the UI thread, from the current page onwards and round to it, keeping the matches
    /// in page order; the first one found is shown at once, the count fills in as the rest of the book is read.
    /// </summary>
    private void StartSearch()
    {
        var query = SearchBox.Text.Trim();
        StopSearch();
        if (query.Length == 0 || _book is null) return;

        _searchQuery = query;
        _searchDone = false;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var book = _book;
        var start = Math.Max(0, _currentPage);
        SearchCountText.Text = "Searching...";

        _ = Task.Run(() =>
        {
            var found = new List<SearchMatch>();
            var clock = Stopwatch.StartNew();
            var first = true;
            for (int i = 0; i < book.PageCount && !cts.IsCancellationRequested; i++)
            {
                var page = (start + i) % book.PageCount;
                if (Text(page) is not { } layer) continue;
                var hits = layer.FindMatches(query, new SearchOptions()).Select(m => new SearchMatch(page, m.Start, m.Length)).ToList();
                if (hits.Count > 0) found.AddRange(hits);

                // The first hit at once; after that the count every half second or so.
                if ((first && hits.Count > 0) || clock.ElapsedMilliseconds > 500 || i == book.PageCount - 1)
                {
                    first = false;
                    clock.Restart();
                    var batch = found.ToList();
                    found.Clear();
                    var done = i == book.PageCount - 1;
                    var scanned = i + 1;
                    DispatcherQueue.TryEnqueue(() => AddMatches(cts, batch, done, scanned, book.PageCount));
                }
            }
        }, cts.Token);
    }

    private void AddMatches(CancellationTokenSource cts, List<SearchMatch> batch, bool done, int scanned, int pages)
    {
        if (cts != _searchCts) return;
        var current = _matchIndex >= 0 ? _matches[_matchIndex] : (SearchMatch?)null;
        _matches.AddRange(batch);
        _matches.Sort((a, b) => a.PageIndex != b.PageIndex ? a.PageIndex.CompareTo(b.PageIndex) : a.Start.CompareTo(b.Start));
        _searchDone = done;

        if (current is { } c) _matchIndex = _matches.IndexOf(c);
        else if (_matches.Count > 0)
        {
            // The first match at or after where the reader is.
            var from = Math.Max(0, _currentPage);
            _matchIndex = _matches.FindIndex(m => m.PageIndex >= from);
            if (_matchIndex < 0) _matchIndex = 0;
            ShowMatch();
        }

        SearchCountText.Text = _matches.Count == 0
            ? (done ? "Not found" : $"Searching... {scanned * 100 / Math.Max(1, pages)}%")
            : $"{_matchIndex + 1:N0} of {_matches.Count:N0}{(done ? "" : "+")}";
        if (done && _matches.Count == 0 && _book is { } b && Enumerable.Range(0, Math.Min(b.PageCount, 5)).All(p => Text(p)?.CharCount == 0))
            SearchCountText.Text = "No text to search";
        DrawAllMarks();
    }

    private void StepMatch(int direction)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + direction + _matches.Count) % _matches.Count;
        ShowMatch();
        SearchCountText.Text = $"{_matchIndex + 1:N0} of {_matches.Count:N0}{(_searchDone ? "" : "+")}";
        DrawAllMarks();
    }

    /// <summary>Scrolls the current match into the upper third of the view, unless it is already comfortably in view.</summary>
    private void ShowMatch()
    {
        if (_matchIndex < 0 || _matchIndex >= _matches.Count || _book is null) return;
        var m = _matches[_matchIndex];
        if (Text(m.PageIndex) is not { } layer || layer.GetRangeRects(m.Start, m.Length) is not { Count: > 0 } rects)
        {
            GoToPage(m.PageIndex);
            return;
        }

        var scale = TextScale(m.PageIndex);
        BringIntoView(m.PageIndex, (rects[0].Left + rects[0].Width / 2) * scale, rects[0].Top * scale);
    }

    /// <summary>
    /// Scrolls a point of a page (its slot DIPs) into the upper third of the view, and across into the middle when
    /// the page is wider than the view, unless it is comfortably in view already.
    /// </summary>
    private void BringIntoView(int page, double x, double y)
    {
        var cx = SlotLeft(page) + x;
        var cy = SlotTop(page) + y;
        if (VisibleRect() is { } view && cy > view.Top + view.Height * 0.1 && cy < view.Bottom - view.Height * 0.2
            && cx > view.Left && cx < view.Right) return;
        var z = Scroller.ZoomFactor;
        double? h = ContentWidth * z > Scroller.ViewportWidth ? Math.Max(0, cx * z - Scroller.ViewportWidth / 2) : null;
        Scroller.ChangeView(h, Math.Max(0, cy * z - Scroller.ViewportHeight / 3), null, true);
    }

    // ================================================================ keys

    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action) => AddIf(key, modifiers, () =>
        {
            action();
            return true;
        });

        void AddIf(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) => e.Handled = action();
            Root.KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.F, VirtualKeyModifiers.Control, () =>
        {
            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
        });
        AddIf(VirtualKey.C, VirtualKeyModifiers.Control, () =>
        {
            if (_focus is null) return false;   // nothing selected on the pages: the key is the text box's
            CopySelection();
            return true;
        });
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, () => StepZoom(+1));
        Add((VirtualKey)187, VirtualKeyModifiers.Control, () => StepZoom(+1));     // the = + key
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => StepZoom(-1));
        Add((VirtualKey)189, VirtualKeyModifiers.Control, () => StepZoom(-1));     // the - key
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, () => OnFitWidth(this, new RoutedEventArgs()));
        Add(VirtualKey.F3, VirtualKeyModifiers.None, () => OnSearchNext(this, new RoutedEventArgs()));
        Add(VirtualKey.F3, VirtualKeyModifiers.Shift, () => OnSearchPrevious(this, new RoutedEventArgs()));
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Escape from the window: puts away the definition, then the selection. True when it did something.</summary>
    public bool HandleEscape()
    {
        if (DefinitionPopup.Visibility == Visibility.Visible)
        {
            HideDefinition();
            return true;
        }
        if (_focus is not null)
        {
            ClearSelection();
            return true;
        }
        return false;
    }

    public void FocusPages() => Scroller.Focus(FocusState.Programmatic);

    /// <summary>Hides the toolbar for full screen, and brings it back.</summary>
    public void SetChromeVisible(bool visible)
    {
        ToolBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) ContentsPane.Visibility = Visibility.Collapsed;
        else SetContentsOpen(ContentsButton.IsChecked == true, remember: false);
    }

    // ================================================================ closing

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _searchCts?.Cancel();
        _queue?.Dispose();
        var book = _book;
        _book = null;
        foreach (var page in _cards.Keys.ToList()) DropCard(page);
        book?.Dispose();
    }
}
