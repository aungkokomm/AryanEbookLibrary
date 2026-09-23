using System.ComponentModel;
using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AryanEbookLibrary.Views;

/// <summary>One book in the list view. Port of CineLibrary's MovieRowControl.</summary>
public sealed partial class BookRowControl : UserControl
{
    public static readonly DependencyProperty BookProperty =
        DependencyProperty.Register(nameof(Book), typeof(Book), typeof(BookRowControl),
            new PropertyMetadata(null, OnBookChanged));

    public Book? Book
    {
        get => (Book?)GetValue(BookProperty);
        set => SetValue(BookProperty, value);
    }

    private static readonly SolidColorBrush Green = new(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush GreenWash = new(Color.FromArgb(0x33, 0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush Blue = new(Color.FromArgb(0xFF, 0x3B, 0x8B, 0xF0));
    private static readonly SolidColorBrush BlueWash = new(Color.FromArgb(0x33, 0x3B, 0x8B, 0xF0));
    private static readonly SolidColorBrush Gold = new(Color.FromArgb(0xFF, 0xFA, 0xAC, 0x17));
    private static readonly SolidColorBrush Grey = new(Color.FromArgb(0xFF, 0x90, 0x90, 0xA0));
    private static readonly SolidColorBrush OfflineGrey = new(Color.FromArgb(0xFF, 0x6B, 0x72, 0x80));

    private Book? _subscribed;
    private CancellationTokenSource? _pendingSingleTap;

    public BookRowControl()
    {
        InitializeComponent();
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => BookMenu.Build(flyout, Book);
        ContextFlyout = flyout;

        Loaded += (_, _) =>
        {
            Watch(Book);
            AppServices.Library.SelectionModeChanged -= OnSelectionModeChanged;
            AppServices.Library.SelectionModeChanged += OnSelectionModeChanged;
            ShowSelection();
        };
        Unloaded += (_, _) =>
        {
            Watch(null);
            AppServices.Library.SelectionModeChanged -= OnSelectionModeChanged;
            HoverFill.Opacity = 0;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && Book is { } b)
            {
                BookCardControl.RequestDetails(b);
                e.Handled = true;
            }
        };
    }

    private static void OnBookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BookRowControl r) return;
        r.Watch(e.NewValue as Book);
        if (e.NewValue is Book b)
        {
            r.Populate(b);
            r.LoadThumb(b);
            r.ShowSelection();
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
        if (e.PropertyName == nameof(Book.CoverPath)) LoadThumb(b);   // a cover from Open Library arrived
        if (e.PropertyName == nameof(Book.IsSelected)) { ShowSelection(); return; }
        Populate(b);
    }

    private void Populate(Book b)
    {
        RowTitle.Text = b.Title;
        AutomationProperties.SetName(this, b.Title);
        RowFav.Visibility = b.IsFavorite ? Visibility.Visible : Visibility.Collapsed;

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.Author)) meta.Add(b.Author);   // no "Unknown author" in every other row
        if (!string.IsNullOrWhiteSpace(b.SeriesLine)) meta.Add(b.SeriesLine);
        meta.Add(b.FormatLabel);
        if (b.Year.HasValue) meta.Add(b.Year.Value.ToString());
        RowMeta.Text = string.Join(" · ", meta);

        DriveLabel.Text = b.IsOffline ? b.DriveLabel + " (not connected)" : b.DriveLabel;
        DriveDot.Fill = b.IsOffline ? OfflineGrey : Green;

        RowRating.Text = Fn.Stars(b.Rating);

        switch (b.Status)
        {
            case ReadStatus.Reading:
                StatusBadge.Background = BlueWash;
                StatusText.Foreground = Blue;
                StatusText.Text = b.Progress > 0 ? $"READING {b.Progress}%" : "READING";
                StatusBadge.Visibility = Visibility.Visible;
                break;
            case ReadStatus.Finished:
                StatusBadge.Background = GreenWash;
                StatusText.Foreground = Green;
                StatusText.Text = "FINISHED";
                StatusBadge.Visibility = Visibility.Visible;
                break;
            default:
                StatusBadge.Visibility = Visibility.Collapsed;   // "unread" on every row would be noise
                break;
        }

        var finished = b.Status == ReadStatus.Finished;
        FinishedBtn.Content = finished ? "✓" : "○";
        FinishedBtn.Foreground = finished ? Green : Grey;
        ToolTipService.SetToolTip(FinishedBtn, finished ? "Finished (click to mark unread)" : "Mark finished");
        AutomationProperties.SetName(FinishedBtn, finished ? "Mark unread" : "Mark finished");

        FavBtn.Content = b.IsFavorite ? "★" : "☆";
        FavBtn.Foreground = b.IsFavorite ? Gold : Grey;
        ToolTipService.SetToolTip(FavBtn, b.IsFavorite ? "Remove from favorites" : "Add to favorites");
        AutomationProperties.SetName(FavBtn, b.IsFavorite ? "Remove from favorites" : "Add to favorites");
    }

    private void LoadThumb(Book b)
    {
        ThumbPlaceholder.Visibility = Visibility.Visible;
        ThumbImage.Source = null;
        var path = b.CoverPath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            ThumbImage.Source = new BitmapImage { DecodePixelWidth = 96, UriSource = new Uri(path) };
        }
        catch
        {
            // keep the placeholder
        }
    }

    private void OnThumbOpened(object sender, RoutedEventArgs e) => ThumbPlaceholder.Visibility = Visibility.Collapsed;

    private void OnThumbFailed(object sender, ExceptionRoutedEventArgs e) => ThumbPlaceholder.Visibility = Visibility.Visible;

    private void OnSelectionModeChanged(object? sender, EventArgs e) => ShowSelection();

    /// <summary>The tick takes the thumbnail's place while several books are being worked on at once.</summary>
    private void ShowSelection()
    {
        var on = AppServices.Library.SelectionMode;
        var picked = on && Book is { IsSelected: true };
        SelectBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Thumb.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SelectTick.Text = picked ? "✓" : "";
        SelectTick.Foreground = Blue;
        SelectBadge.BorderBrush = picked ? Blue : Grey;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (BookCardControl.TapOriginatedInButton(e.OriginalSource as DependencyObject)) { e.Handled = true; return; }
        if (Book is not { } book) return;
        if (AppServices.Library.SelectionMode)
        {
            AppServices.Library.ToggleSelect(book);
            e.Handled = true;
            return;
        }

        _pendingSingleTap?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingSingleTap = cts;
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(220, cts.Token); }
            catch (OperationCanceledException) { return; }
            dq.TryEnqueue(() =>
            {
                if (!cts.IsCancellationRequested) BookCardControl.RequestDetails(book);
            });
        });
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (BookCardControl.TapOriginatedInButton(e.OriginalSource as DependencyObject)) { e.Handled = true; return; }
        if (AppServices.Library.SelectionMode) { e.Handled = true; return; }
        _pendingSingleTap?.Cancel();
        _pendingSingleTap = null;
        if (Book is { } b) BookCardControl.RequestOpen(b);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => HoverFill.Opacity = 1;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => HoverFill.Opacity = 0;

    private void OnToggleFinished(object sender, RoutedEventArgs e)
    {
        if (Book is not { } b) return;
        AppServices.Library.SetStatus(b, b.Status == ReadStatus.Finished ? ReadStatus.Unread : ReadStatus.Finished);
    }

    private void OnToggleFav(object sender, RoutedEventArgs e)
    {
        if (Book is { } b) AppServices.Library.ToggleFavorite(b);
    }
}
