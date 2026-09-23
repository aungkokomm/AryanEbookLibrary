using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// A book open in the app's own reader: one window per book, so the library stays where it was. The window keeps
/// the record: how long the book was actually read (while the window is in front and the reader has done something
/// in the last few minutes), which pages were looked at, and where the reader left off.
/// </summary>
public sealed partial class ReaderWindow : Window
{
    /// <summary>No input for this long and the clock stops: the reader has gone to make tea.</summary>
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    /// <summary>A sitting shorter than this was a glance, not reading, and is not kept.</summary>
    private const int MinSeconds = 20;
    /// <summary>A page counts as read after this many seconds on it; paging through a book is not reading it.</summary>
    private const int SecondsPerPageSeen = 3;

    private static readonly Dictionary<string, ReaderWindow> Open_ = new(StringComparer.Ordinal);

    private readonly Book _book;
    private readonly IReaderView _view;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private DateTime _lastInput = DateTime.UtcNow;
    private bool _inFront = true;
    private bool _closed;

    private ReadingSession? _session;
    private int _seconds;
    private readonly HashSet<int> _pagesRead = new();
    private int _page = -1;
    private int _lastOnScreen = -1;
    private int _secondsOnPage;
    private int _firstPage = -1;
    private double? _secondsPerPage;

    /// <summary>Opens the book in its own reader window, or brings its window forward. Returns an error, or null.</summary>
    public static string? Open(Book book)
    {
        if (Open_.TryGetValue(book.StateKey, out var existing))
        {
            existing.Activate();
            return null;
        }
        var path = book.FullPath;
        if (path is null || !File.Exists(path)) return "The file is not there any more.";
        if (book.Format is BookFormat.Unknown) return "The reader cannot open " + book.FormatLabel + " books.";

        var window = new ReaderWindow(book, path);
        Open_[book.StateKey] = window;
        window.Activate();
        return null;
    }

    /// <summary>Closes every reader, saving each one's place and time. For when the library itself closes.</summary>
    public static void CloseAll()
    {
        foreach (var window in Open_.Values.ToList()) window.Close();
    }

    private ReaderWindow(Book book, string path)
    {
        _book = book;
        InitializeComponent();

        Title = book.Title;
        TitleText.Text = string.IsNullOrWhiteSpace(book.Author) ? book.Title : $"{book.Title}  \u00B7  {book.Author}";
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        else RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        var settings = AppServices.Settings;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(640, settings.ReaderWidth), Math.Max(480, settings.ReaderHeight)));

        RootGrid.RequestedTheme = AppServices.Theme;
        AppServices.ThemeChanged += OnThemeChanged;
        RootGrid.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        PaintCaptionButtons();
        TitleBar.Loaded += (_, _) => SyncCaptionColumn();
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange) SyncCaptionColumn();
        };

        // Anything the reader does counts as reading, including input the pages handle themselves.
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => Touch()), true);
        RootGrid.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => Touch()), true);
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Activated += (_, e) =>
        {
            _inFront = e.WindowActivationState != WindowActivationState.Deactivated;
            if (_inFront) Touch();
        };

        _view = book.Format switch
        {
            BookFormat.Pdf => new PdfReaderView(),
            BookFormat.Cbz or BookFormat.Cbr => new ComicReaderView(),
            _ => new EpubReaderView(),
        };
        var element = (FrameworkElement)_view;
        Grid.SetRow(element, 1);
        RootGrid.Children.Add(element);
        _view.OfferFinish = book.Status != ReadStatus.Finished;
        _view.PageChanged += OnPageChanged;
        _view.Activity += Touch;
        _view.FullScreenRequested += ToggleFullScreen;
        _view.EscapeRequested += () =>
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ToggleFullScreen();
        };
        _view.FinishedRequested += OnFinished;
        _view.OpenExternallyRequested += () =>
        {
            BookLauncher.Open(_book, withDefaultApp: true);
            Close();
        };

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => Tick();
        _clock.Start();

        Closed += OnClosed;

        var position = AppServices.Sessions.GetPosition(book.StateKey);
        _ = _view.OpenAsync(path, position);
        _ = LoadPaceAsync();
        element.Loaded += (_, _) => _view.FocusPages();
    }

    // ---- title bar ----

    private void OnThemeChanged()
    {
        RootGrid.RequestedTheme = AppServices.Theme;
    }

    private void PaintCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var text = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = text;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E);
        bar.ButtonHoverForegroundColor = text;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = text;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00);
    }

    private void SyncCaptionColumn()
    {
        var scale = TitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;
        CaptionColumn.Width = new GridLength(Math.Max(AppWindow.TitleBar.RightInset / scale + 8, 56));
    }

    // ---- keys and full screen ----

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        Touch();
        if (e.Handled) return;
        if (e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (_view.HandleEscape()) e.Handled = true;
            else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        }
    }

    private void ToggleFullScreen()
    {
        var full = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TitleBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        _view.SetChromeVisible(!full);
        _view.FocusPages();
    }

    // ---- the record of reading ----

    private void Touch() => _lastInput = DateTime.UtcNow;

    private bool Reading => _inFront && !_closed && _page >= 0 && DateTime.UtcNow - _lastInput < IdleLimit;

    private void Tick()
    {
        if (!Reading) return;
        _seconds++;
        _secondsOnPage++;
        if (_secondsOnPage == SecondsPerPageSeen)
            for (var p = _page; p <= _lastOnScreen; p++) _pagesRead.Add(p);
        if (_seconds % 30 == 0) SaveSession();
    }

    private void OnPageChanged(int first, int last)
    {
        Touch();
        if (_firstPage < 0) _firstPage = first;
        _page = first;
        _lastOnScreen = Math.Max(first, last);
        _secondsOnPage = 0;
        ShowTimeLeft();
    }

    /// <summary>Writes the sitting so far: created once it is long enough to be reading, then kept up to date.</summary>
    private void SaveSession()
    {
        if (_seconds < MinSeconds || _page < 0) return;
        var now = DateTime.UtcNow;
        try
        {
            if (_session is null)
            {
                var started = now.AddSeconds(-_seconds);
                var s = new ReadingSession(0, _book.StateKey, started, now, _seconds, _pagesRead.Count,
                    Math.Max(0, _firstPage), _page, _view.PageCount);
                _session = s with { Id = AppServices.Sessions.Add(s) };
            }
            else
            {
                _session = _session with { EndedUtc = now, Seconds = _seconds, Pages = _pagesRead.Count, LastPage = _page, PageCount = _view.PageCount };
                AppServices.Sessions.Update(_session);
            }
            if (_view.Position() is { } position) AppServices.Sessions.SavePosition(_book.StateKey, position);
        }
        catch (Exception ex)
        {
            Log.Write("reader: saving the session failed: " + ex.Message);
        }
    }

    // ---- time left ----

    private async Task LoadPaceAsync()
    {
        var key = _book.StateKey;
        _secondsPerPage = await Task.Run(() =>
        {
            try
            {
                var all = AppServices.Sessions.All();
                return ReadingTime.SecondsPerPage(all.Where(s => s.BookKey == key).ToList(), all);
            }
            catch (Exception ex)
            {
                Log.Write("reader: reading the pace failed: " + ex.Message);
                return (double?)null;
            }
        });
        ShowTimeLeft();
    }

    private void ShowTimeLeft()
    {
        var left = _view.PageCount - _page - 1;
        if (_secondsPerPage is not { } pace || _page < 0 || left <= 0)
        {
            _view.SetTimeLeft("");
            return;
        }
        _view.SetTimeLeft($"About {ReadingTime.Format((int)Math.Round(left * pace))} left");
    }

    // ---- finishing and closing ----

    private void OnFinished()
    {
        AppServices.Library.SetStatus(_book, ReadStatus.Finished);
        _view.OfferFinish = false;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        SaveSession();
        _closed = true;
        _clock.Stop();
        AppServices.ThemeChanged -= OnThemeChanged;
        Open_.Remove(_book.StateKey);

        try
        {
            // How far through the book, for the library's progress bar, while it is being read.
            if (_book.Status == ReadStatus.Reading && _view.PageCount > 0 && _page >= 0)
            {
                var percent = (int)Math.Round(100.0 * (_page + 1) / _view.PageCount);
                if (percent != _book.Progress)
                {
                    _book.Progress = Math.Clamp(percent, 1, 99);
                    AppServices.Library.SaveState(_book);
                }
            }

            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
            {
                AppServices.Settings.ReaderWidth = AppWindow.Size.Width;
                AppServices.Settings.ReaderHeight = AppWindow.Size.Height;
                AppServices.Settings.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Write("reader: closing: " + ex.Message);
        }

        Log.Write($"reader: closed {Path.GetFileName(_book.RelPath)} at page {_page + 1}, {_seconds} s read, {_pagesRead.Count} pages");
        _view.Close();
    }
}
