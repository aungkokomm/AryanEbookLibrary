using System.ComponentModel;
using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace AryanEbookLibrary.Views;

/// <summary>
/// One book in the grid. Port of CineLibrary's MovieCardControl: fixed-size card (all cards resize together
/// with the S/M/L/XL density), hover overlay with actions, single click = details, double click = open.
/// The ItemsRepeater recycles cards, so everything is re-populated whenever <see cref="Book"/> changes.
/// </summary>
public sealed partial class BookCardControl : UserControl
{
    public static readonly DependencyProperty BookProperty =
        DependencyProperty.Register(nameof(Book), typeof(Book), typeof(BookCardControl),
            new PropertyMetadata(null, OnBookChanged));

    public Book? Book
    {
        get => (Book?)GetValue(BookProperty);
        set => SetValue(BookProperty, value);
    }

    // ---- requests the Library page handles (static, so recycled cards need no wiring) ----

    public static event Action<Book>? DetailsRequested;
    public static event Action<Book, bool>? OpenRequested;
    public static event Action<Book>? FindOnlineRequested;

    public static void RequestDetails(Book b) => DetailsRequested?.Invoke(b);
    /// <summary>Asks the library page to open the book; <paramref name="withDefaultApp"/> skips the app's own reader.</summary>
    public static void RequestOpen(Book b, bool withDefaultApp = false) => OpenRequested?.Invoke(b, withDefaultApp);
    public static void RequestFindOnline(Book b) => FindOnlineRequested?.Invoke(b);

    // ---- global card size (all cards resize together when the density changes) ----

    public static double GlobalCardWidth { get; private set; } = 150;
    public static double GlobalCardHeight { get; private set; } = 240;
    private static event EventHandler? GlobalSizeChanged;

    // Covers are decoded at about twice the chosen card size so they stay sharp on high-DPI screens without
    // holding full-size images for thousands of books (CineLibrary: 240..500 px). It follows the S/M/L/XL
    // choice, not every window resize, so resizing the window does not reload every cover.
    private static int DecodeWidth { get; set; } = 300;

    public static void SetGlobalSize(double width, double height, double baseWidth)
    {
        GlobalCardWidth = width;
        GlobalCardHeight = height;
        DecodeWidth = (int)Math.Min(500, Math.Max(240, baseWidth * 2));
        GlobalSizeChanged?.Invoke(null, EventArgs.Empty);
    }

    private Book? _subscribed;
    private int _decodedAt;
    private CancellationTokenSource? _pendingSingleTap;

    public BookCardControl()
    {
        InitializeComponent();
        ApplySize();
        // Subscribe on Loaded, unsubscribe on Unloaded: a recycled card goes back on screen many times.
        Loaded += OnCardLoaded;
        Unloaded += OnCardUnloaded;

        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => BookMenu.Build(flyout, Book);
        ContextFlyout = flyout;

        // Drag the book (or every selected book) onto one of My lists in the pane.
        CanDrag = true;
        DragStarting += (_, e) =>
        {
            if (Book is { } b) BookDrag.Start(e, b);
            else e.Cancel = true;
        };

        PointerEntered += (_, _) =>
        {
            HoverOverlay.Visibility = Visibility.Visible;
            AnimateOverlay(HoverOverlay.Opacity, 1, 200);
            CardLift.Y = -4;
            CardScale.ScaleX = CardScale.ScaleY = 1.025;
        };
        PointerExited += (_, _) =>
        {
            AnimateOverlay(HoverOverlay.Opacity, 0, 160);
            CardLift.Y = 0;
            CardScale.ScaleX = CardScale.ScaleY = 1;
        };

        // Single tap opens details after a short delay, so a double tap can cancel it and open the book.
        // While several books are being worked on at once, a tap ticks the book instead.
        Tapped += (_, e) =>
        {
            if (TapOriginatedInButton(e.OriginalSource as DependencyObject)) { e.Handled = true; return; }
            if (Book is not { } book) return;
            if (AppServices.Library.SelectionMode)
            {
                AppServices.Library.ToggleSelect(book);
                e.Handled = true;
                return;
            }
            ScheduleSingleTap();
        };
        DoubleTapped += (_, e) =>
        {
            if (TapOriginatedInButton(e.OriginalSource as DependencyObject)) { e.Handled = true; return; }
            if (AppServices.Library.SelectionMode) { e.Handled = true; return; }
            _pendingSingleTap?.Cancel();
            _pendingSingleTap = null;
            if (Book is { } b) RequestOpen(b);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && Book is { } b)
            {
                RequestDetails(b);
                e.Handled = true;
            }
        };
    }

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        GlobalSizeChanged -= OnGlobalSizeChanged;
        GlobalSizeChanged += OnGlobalSizeChanged;
        AppServices.Library.SelectionModeChanged -= OnSelectionModeChanged;
        AppServices.Library.SelectionModeChanged += OnSelectionModeChanged;
        ApplySize();
        Watch(Book);
        ShowSelection();
    }

    private void OnSelectionModeChanged(object? sender, EventArgs e) => ShowSelection();

    /// <summary>The tick and the highlight follow both the mode and this book's own state.</summary>
    private void ShowSelection()
    {
        var on = AppServices.Library.SelectionMode;
        var picked = on && Book is { IsSelected: true };
        SelectBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SelectTick.Text = picked ? "✓" : "";
        SelectBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            picked ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x18, 0x6B, 0xD5) : Microsoft.UI.ColorHelper.FromArgb(0xCC, 0, 0, 0));
        CardBorder.BorderThickness = new Thickness(picked ? 3 : 1);
        CardBorder.BorderBrush = picked
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x18, 0x6B, 0xD5))
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
    }

    private void OnCardUnloaded(object sender, RoutedEventArgs e)
    {
        GlobalSizeChanged -= OnGlobalSizeChanged;
        AppServices.Library.SelectionModeChanged -= OnSelectionModeChanged;
        Watch(null);
        // A card recycled mid-hover would otherwise come back lifted with the overlay showing.
        HoverOverlay.Opacity = 0;
        HoverOverlay.Visibility = Visibility.Collapsed;
        CardLift.Y = 0;
        CardScale.ScaleX = CardScale.ScaleY = 1;
    }

    private void OnGlobalSizeChanged(object? s, EventArgs e)
    {
        ApplySize();
        if (Book is { } b && _decodedAt != DecodeWidth) LoadCover(b);
    }

    private void ApplySize()
    {
        CardRoot.Width = GlobalCardWidth;
        CardRoot.Height = GlobalCardHeight;
    }

    private static void OnBookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BookCardControl c) return;
        c.Watch(e.NewValue as Book);
        if (e.NewValue is Book b)
        {
            c.Populate(b);
            c.LoadCover(b);
            c.ShowSelection();
        }
    }

    private void Watch(Book? book)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnBookPropertyChanged;
        _subscribed = book;
        if (book is not null) book.PropertyChanged += OnBookPropertyChanged;
    }

    private void OnBookPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Book b || !ReferenceEquals(b, Book)) return;
        if (e.PropertyName == nameof(Book.CoverPath)) LoadCover(b);   // a cover from Open Library arrived
        if (e.PropertyName == nameof(Book.IsSelected)) { ShowSelection(); return; }
        Populate(b);
    }

    private void Populate(Book b)
    {
        TitleText.Text = b.Title;
        PlaceholderTitle.Text = b.Title;
        // No "Unknown author" on half the shelf: a book without one shows its series, or nothing.
        MetaText.Text = string.IsNullOrWhiteSpace(b.Author) ? b.SeriesLine : b.Author;
        AutomationProperties.SetName(this, b.Title);

        if (b.Rating > 0)
        {
            RatingInline.Text = "★ " + b.Rating;
            RatingInline.Visibility = Visibility.Visible;
        }
        else
        {
            RatingInline.Visibility = Visibility.Collapsed;
        }

        OfflineBadge.Visibility = b.IsOffline ? Visibility.Visible : Visibility.Collapsed;
        FavBadge.Visibility = b.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        FinishedBadge.Visibility = b.Status == ReadStatus.Finished ? Visibility.Visible : Visibility.Collapsed;
        FinishedToggleBtn.Content = b.Status == ReadStatus.Finished ? "✓ Finished" : "○ Mark Finished";

        ReadingProgress.Value = b.Progress;
        ReadingProgress.Visibility = b.ShowProgress ? Visibility.Visible : Visibility.Collapsed;

        var info = new List<string> { b.FormatLabel };
        if (b.Year.HasValue) info.Add(b.Year.Value.ToString());
        if (!string.IsNullOrWhiteSpace(b.SeriesLine)) info.Add(b.SeriesLine);
        if (b.IsOffline) info.Add("Offline: " + b.DriveLabel);
        InfoText.Text = string.Join(" · ", info);
    }

    private void LoadCover(Book b)
    {
        CoverPlaceholder.Visibility = Visibility.Visible;
        CoverImage.Source = null;

        _decodedAt = DecodeWidth;
        var path = b.CoverPath;
        if (path is null || !File.Exists(path)) return;

        _ = CoverLoader.ShowAsync(CoverImage, path, DecodeWidth);
    }

    private void OnCoverOpened(object sender, RoutedEventArgs e) => CoverPlaceholder.Visibility = Visibility.Collapsed;

    private void OnCoverFailed(object sender, ExceptionRoutedEventArgs e) => CoverPlaceholder.Visibility = Visibility.Visible;

    private void OnViewDetails(object sender, RoutedEventArgs e)
    {
        if (Book is { } b) RequestDetails(b);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Book is { } b) RequestOpen(b);
    }

    private void OnFinishedToggle(object sender, RoutedEventArgs e)
    {
        if (Book is not { } b) return;
        AppServices.Library.SetStatus(b, b.Status == ReadStatus.Finished ? ReadStatus.Unread : ReadStatus.Finished);
    }

    private void AnimateOverlay(double from, double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, HoverOverlay);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        if (to == 0) sb.Completed += (_, _) => { if (HoverOverlay.Opacity == 0) HoverOverlay.Visibility = Visibility.Collapsed; };
        sb.Begin();
    }

    private void ScheduleSingleTap()
    {
        _pendingSingleTap?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingSingleTap = cts;
        var book = Book;
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(220, cts.Token); }
            catch (OperationCanceledException) { return; }
            dq.TryEnqueue(() =>
            {
                if (!cts.IsCancellationRequested && book is not null) RequestDetails(book);
            });
        });
    }

    internal static bool TapOriginatedInButton(DependencyObject? src)
    {
        for (var cur = src; cur is not null; cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur))
            if (cur is ButtonBase) return true;
        return false;
    }
}
