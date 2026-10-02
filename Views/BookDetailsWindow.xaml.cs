using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace AryanEbookLibrary.Views;

/// <summary>
/// A book's details in a window of its own (CineLibrary's movie details window): it opens maximized over the
/// library, remembers its size, closes with Esc, follows the app's theme, and saves each change as it is made.
/// One window per book: asking for a book already open brings its window forward.
/// </summary>
public sealed partial class BookDetailsWindow : Window
{
    private static readonly Dictionary<string, BookDetailsWindow> Open_ = new(StringComparer.Ordinal);

    private readonly Book _book;
    private readonly LibraryViewModel _vm = AppServices.Library;
    private readonly Action? _onClosed;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _progressTimer;
    private bool _closed;
    private bool _changed;        // something the library's order or filter may depend on: it refreshes on close
    private bool _populating;     // controls being set from the book, not by the user
    private bool _dialogOpen;
    private bool _editingNote;
    // The shown title and author before any edit, as the window opened: typing them back means "no edit",
    // even if online details are added or removed while it is open.
    private readonly string _openBaseTitle;
    private readonly string _openBaseAuthor;

    /// <summary>Shows the book's details, or brings its window forward when it is already open.</summary>
    public static void Show(Book book, Action? onClosed = null)
    {
        if (Open_.TryGetValue(book.StateKey, out var open))
        {
            if (open.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
            open.Activate();
            return;
        }
        var window = new BookDetailsWindow(book, onClosed);
        Open_[book.StateKey] = window;
        window.Activate();
    }

    /// <summary>Closes every details window, saving what is being typed. For when the library itself closes.</summary>
    public static void CloseAll()
    {
        foreach (var window in Open_.Values.ToList()) window.Close();
    }

    private BookDetailsWindow(Book book, Action? onClosed)
    {
        _book = book;
        _onClosed = onClosed;
        _openBaseTitle = book.BaseTitle;
        _openBaseAuthor = book.BaseAuthor;
        InitializeComponent();

        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        else RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        // The size the user last left it at, then maximized, as CineLibrary opens its details: restoring it
        // brings that size back.
        var settings = AppServices.Settings;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(700, settings.DetailsWidth), Math.Max(500, settings.DetailsHeight)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.Maximize();
        }
        AppWindow.Changed += (sender, e) =>
        {
            if (!e.DidSizeChange) return;
            SyncCaptionColumn();
            if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                settings.DetailsWidth = sender.Size.Width;
                settings.DetailsHeight = sender.Size.Height;
            }
        };

        RootGrid.RequestedTheme = AppServices.Theme;
        AppServices.ThemeChanged += OnThemeChanged;
        RootGrid.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        PaintCaptionButtons();
        ColorTheme.Follow(this, RootGrid, PageTint);
        TitleBar.Loaded += (_, _) => SyncCaptionColumn();

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            OnEscape();
        };
        RootGrid.KeyboardAccelerators.Add(escape);
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;   // no "Esc" tooltip everywhere

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(120);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += (_, _) => { if (!_closed) Populate(); };
        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(400);
        _progressTimer.IsRepeating = false;
        _progressTimer.Tick += (_, _) => _vm.SaveState(_book);

        _book.PropertyChanged += OnBookChanged;
        Closed += OnClosed;

        FinishedPicker.MaxDate = DateTimeOffset.Now;
        RatingBox.RegisterPropertyChangedCallback(RatingControl.ValueProperty, OnRatingChanged);
        Populate();
        ShowNote();
        ShowHighlights();
        AppServices.Annotations.Changed += OnAnnotationsChanged;
        ClipStore.Saved += OnClipSaved;
        _ = LoadImagesAsync();
        // Read first, so Enter reads the book and never removes details or opens an editor.
        RootGrid.Loaded += (_, _) => (ReadBtn.IsEnabled ? ReadBtn : (Control)FavBtn).Focus(FocusState.Pointer);
    }

    // ------------------------------------------------------------ highlights

    private void OnAnnotationsChanged(string key)
    {
        if (key.Length == 0 || key == _book.StateKey) ShowHighlights();
    }

    private void OnClipSaved(string id)
    {
        if (HighlightsRows.Children.OfType<FrameworkElement>().Any(r => (r.Tag as Annotation)?.Id == id)) ShowHighlights();
    }

    /// <summary>How many, and the three made last, each going to its place in the reader when clicked.</summary>
    private void ShowHighlights()
    {
        var all = AppServices.Annotations.ForBook(_book);
        HighlightsCard.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightsRows.Children.Clear();
        if (all.Count == 0) return;
        var marks = all.Count(a => a.Kind != AnnotationKind.PageNote);
        var notes = all.Count - marks;
        var parts = new List<string>();
        if (marks > 0) parts.Add(marks == 1 ? "1 highlight" : $"{marks:N0} highlights");
        if (notes > 0) parts.Add(notes == 1 ? "1 note on a page" : $"{notes:N0} notes on pages");
        HighlightsHeader.Text = string.Join(", ", parts);
        HighlightsAllBtn.Visibility = marks > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in all.OrderByDescending(a => a.CreatedUtc).Take(3))
            HighlightsRows.Children.Add(HighlightRow(new AnnotationItem(a, _book)));
    }

    private Button HighlightRow(AnnotationItem item)
    {
        var body = new StackPanel { Spacing = 3 };
        var words = item.QuoteText.Length > 0 ? item.QuoteText : item.Annotation.Kind == AnnotationKind.PageNote ? item.NoteText : "";
        if (words.Length > 0)
            body.Children.Add(new TextBlock { Text = words, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Clip is { } clip)
            body.Children.Add(new Image { Source = clip, MaxHeight = 90, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
        // The hint style, not the brush itself: a brush looked up in code is the app's theme's, not this window's.
        body.Children.Add(new TextBlock
        {
            Text = item.Where,
            Style = (Style)Application.Current.Resources["PageHintStyle"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Fill = item.ColorBrush, RadiusX = 2, RadiusY = 2 });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var row = new Button
        {
            Content = grid,
            Tag = item.Annotation,
            Padding = new Thickness(6, 6, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, item.Spoken);
        ToolTipService.SetToolTip(row, "Open the book here");
        row.Click += (_, _) => AnnotationsPage.OpenInReader(_book, item.Annotation);
        return row;
    }

    private void OnSeeAllHighlights(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.ShowAnnotations(notes: false, _book.StateKey);
        App.MainWindow?.Activate();
    }

    // ------------------------------------------------------------ window

    private void OnThemeChanged() => RootGrid.RequestedTheme = AppServices.Theme;

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

    /// <summary>Esc backs out of whatever is being typed first (a tag, the note, the title), then closes the window.</summary>
    private void OnEscape()
    {
        if (AddTagBox.Visibility == Visibility.Visible)
        {
            CloseTagBox();
            return;
        }
        if (_editingNote)
        {
            OnNoteCancel(this, new RoutedEventArgs());
            return;
        }
        if (EditPanel.Visibility == Visibility.Visible)
        {
            OnCancelDetails(this, new RoutedEventArgs());
            return;
        }
        Close();
    }

    /// <summary>What is still being typed is kept: closing is not cancelling (Cancel and Esc are).</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        if (_editingNote && (NoteEditor.Text ?? "").Trim() != _book.Notes.Trim()) SaveNote();
        if (EditPanel.Visibility == Visibility.Visible) SaveDetails();
        if (_progressTimer.IsRunning)
        {
            _progressTimer.Stop();
            _vm.SaveState(_book);
        }
        _refreshTimer.Stop();
        _book.PropertyChanged -= OnBookChanged;
        AppServices.ThemeChanged -= OnThemeChanged;
        AppServices.Annotations.Changed -= OnAnnotationsChanged;
        ClipStore.Saved -= OnClipSaved;
        Open_.Remove(_book.StateKey);
        AppServices.Settings.Save();
        if (_changed) _vm.ApplyFilter();
        _onClosed?.Invoke();
    }

    /// <summary>Details that arrive while the window is open (a picked online record's cover and description) show at once.</summary>
    private void OnBookChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_populating || _closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
            if (e.PropertyName is nameof(Book.CoverFile) or nameof(Book.CoverPath)) _ = LoadImagesAsync();
        });
    }

    private bool? _compact;

    /// <summary>
    /// A narrow window gets a smaller banner, cover and title: beside a 200 px cover a 34 pt title had about
    /// 300 px and broke "Mahabharata" in the middle of the word.
    /// </summary>
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 900;
        if (compact == _compact) return;
        _compact = compact;
        HeroBorder.Height = compact ? 220 : 300;
        CoverRow.Margin = new Thickness(0, compact ? -90 : -120, 0, 0);
        CoverBorder.Width = compact ? 140 : 200;
        CoverBorder.Height = compact ? 203 : 290;
        CoverBorder.Margin = compact ? new Thickness(8, 0, 16, 0) : new Thickness(16, 0, 24, 0);
        MetaPanel.Margin = new Thickness(0, compact ? 104 : 140, 0, 0);
        BookTitle.FontSize = compact ? 26 : 34;
        BookTitle.LineHeight = compact ? 32 : 40;
    }

    private void OnContentScrolled(object? sender, ScrollViewerViewChangedEventArgs e) =>
        StickyBar.Visibility = ContentScroller.VerticalOffset > 260 ? Visibility.Visible : Visibility.Collapsed;

    private void OnStickyClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>A link to more books (an author, a series, a tag, a list, a subject): the library shows them, and this window goes.</summary>
    private void NavigateAndClose(Action<MainWindow> show)
    {
        if (App.MainWindow is { } main)
        {
            show(main);
            main.Activate();
        }
        Close();
    }

    // ------------------------------------------------------------ filling in

    private void Populate()
    {
        _populating = true;
        try
        {
            var b = _book;
            Title = b.Title;
            TitleText.Text = string.IsNullOrWhiteSpace(b.Author) ? b.Title : $"{b.Title}  ·  {b.Author}";
            BookTitle.Text = b.Title;
            StickyTitle.Text = b.Title;
            CoverTitle.Text = b.Title;

            BuildPeople();
            BuildChips();
            ShowDrive();
            ShowActions();
            ShowOnlineState();
            BuildListChips();
            BuildTagChips();
            BuildFacts();

            DescriptionText.Text = b.Description;
            var described = b.Description.Trim().Length > 0;
            DescriptionSection.Visibility = DescriptionDivider.Visibility = Vis(described);

            if (!_editingNote) ShowNote();
            BuildSubjects();
        }
        finally
        {
            _populating = false;
        }
    }

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The author or authors and the series under the title, each a link to their books.</summary>
    private void BuildPeople()
    {
        PeopleLinks.Children.Clear();
        var people = AuthorIndex.Split(_book.Author).ToList();
        if (people.Count == 0)
            PeopleLinks.Children.Add(new TextBlock { Text = "Unknown author", FontSize = 16, Opacity = 0.7 });
        for (var i = 0; i < people.Count; i++)
        {
            var person = people[i];
            var link = new HyperlinkButton { Content = person, FontSize = 16, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(link, $"Every book by {person}");
            link.Click += (_, _) => NavigateAndClose(m => m.ShowBooksBy(person));
            PeopleLinks.Children.Add(link);
            if (i < people.Count - 1) PeopleLinks.Children.Add(new TextBlock { Text = ",", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        }
        if (_book.Series.Length > 0)
        {
            PeopleLinks.Children.Add(new TextBlock { Text = "·", FontSize = 16, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
            var series = _book.Series;
            var link = new HyperlinkButton { Content = _book.SeriesLine, FontSize = 16, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(link, "The whole series, in order");
            link.Click += (_, _) => NavigateAndClose(m => m.ShowSeries(series));
            PeopleLinks.Children.Add(link);
        }
    }

    /// <summary>The rating to set, then year, format, pages and language as chips, and a warning chip when it cannot open.</summary>
    private void BuildChips()
    {
        while (ChipsPanel.Children.Count > 1) ChipsPanel.Children.RemoveAt(1);   // the rating stays first
        RatingBox.Value = _book.Rating > 0 ? _book.Rating : -1;

        if (_book.Year is { } year) ChipsPanel.Children.Add(Chip(year.ToString()));
        ChipsPanel.Children.Add(Chip(_book.FormatLabel));
        if (Pages() is { } pages) ChipsPanel.Children.Add(Chip($"{pages:N0} pages"));
        var language = ShelfFilter.LanguageKey(_book);
        if (language != ShelfFilter.NotStated) ChipsPanel.Children.Add(Chip(ShelfFilter.LanguageName(language)));
        if (!_book.IsAvailable) ChipsPanel.Children.Add(Chip("OFFLINE", (Brush)Application.Current.Resources["OfflineBrush"]));
        else if (_fileMissing) ChipsPanel.Children.Add(Chip("MISSING", new SolidColorBrush(Color.FromArgb(0xFF, 0xB4, 0x23, 0x18))));
    }

    private bool _fileMissing;

    private static Border Chip(string text, Brush? background = null)
    {
        var chip = new Border { Style = (Style)Application.Current.Resources["ChipStyle"], CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 12 };
        if (background is not null)
        {
            chip.Background = background;
            chip.BorderThickness = new Thickness(0);
            label.Foreground = new SolidColorBrush(Colors.White);
            label.FontWeight = FontWeights.SemiBold;
        }
        chip.Child = label;
        return chip;
    }

    /// <summary>Pages as the app's reader counted them: known once a PDF or comic has been opened in it. An EPUB's
    /// count there is of the reader's own screens, not the book's pages, so it is not shown.</summary>
    private int? Pages() =>
        _book.Format is BookFormat.Pdf or BookFormat.Cbz or BookFormat.Cbr &&
        AppServices.Sessions.GetPosition(_book.StateKey) is { PageCount: > 0 } p ? p.PageCount : null;

    private void ShowDrive()
    {
        var online = _book.IsAvailable;
        DriveDot.Fill = new SolidColorBrush(online ? Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E) : Color.FromArgb(0xFF, 0x6B, 0x72, 0x80));
        var letter = DriveRegistry.GetRoot(_book.DriveId)?.TrimEnd('\\');
        // The drive's own name often has its letter in it already ("Windows (C:)").
        DriveText.Text = _book.DriveLabel +
                         (!online ? "  ·  not plugged in" : letter is null || _book.DriveLabel.Contains(letter) ? "" : $" ({letter})");
        PathText.Text = _book.FullPath ?? _book.RelPath;
    }

    private void ShowActions()
    {
        var b = _book;
        var position = AppServices.Sessions.GetPosition(b.StateKey);
        var resume = position is not null && b.Status != ReadStatus.Finished || b.Status == ReadStatus.Reading;
        ReadText.Text = StickyReadText.Text = resume ? "▶ Continue reading" : "▶ Read";
        ReadBtn.IsEnabled = StickyReadBtn.IsEnabled = b.IsAvailable;
        ToolTipService.SetToolTip(ReadBtn, b.IsAvailable
            ? BookLauncher.ReadsInApp(b) ? "Open it in the app's reader" : "Open it in its default app"
            : $"Plug in “{b.DriveLabel}” to read it");
        FolderBtn.IsEnabled = b.IsAvailable;
        FavBtn.Content = b.IsFavorite ? "★ Favorite" : "☆ Favorite";
        ToolTipService.SetToolTip(FavBtn, b.IsFavorite ? "In your favorites. Click to take it out." : "Add it to your favorites");

        StatusText.Text = b.Status switch
        {
            ReadStatus.Reading => "📖 Reading",
            ReadStatus.Finished => b.FinishedUtc is { } done ? $"✓ Finished  ·  {done.ToLocalTime():d MMM yyyy}" : "✓ Finished",
            _ => "○ Unread"
        };
        StatusUnread.IsChecked = b.Status == ReadStatus.Unread;
        StatusReading.IsChecked = b.Status == ReadStatus.Reading;
        StatusFinished.IsChecked = b.Status == ReadStatus.Finished;
        StatusFinished.Text = b.Status == ReadStatus.Finished ? "Finished" : "Finished today";

        ProgressRow.Visibility = Vis(b.Status == ReadStatus.Reading);
        ProgressSlider.Value = b.Progress;
        ProgressText.Text = $"{b.Progress}%";
        FinishedRow.Visibility = Vis(b.Status == ReadStatus.Finished);
        FinishedPicker.Date = b.FinishedUtc is { } f ? new DateTimeOffset(f.ToLocalTime().Date) : null;
    }

    /// <summary>The facts in one strip (CineLibrary's file info panel): each shown only when known.</summary>
    private void BuildFacts()
    {
        var b = _book;
        FactsWrap.Children.Clear();
        Fact("FORMAT", b.FormatLabel);
        if (Pages() is { } pages) Fact("PAGES", pages.ToString("N0"));
        if (b.FileSize > 0) Fact("FILE SIZE", Size(b.FileSize));
        if (b.Publisher.Trim().Length > 0)
        {
            var publisher = b.Publisher.Trim();
            var link = new HyperlinkButton { Content = publisher, Padding = new Thickness(0), FontSize = 13, FontWeight = FontWeights.SemiBold, MinHeight = 0 };
            ToolTipService.SetToolTip(link, "Every book from this publisher");
            link.Click += (_, _) => NavigateAndClose(m => m.ShowPublisher(publisher));
            Fact("PUBLISHER", null, link);
        }
        if (b.Year is { } year) Fact("YEAR", year.ToString());
        var language = ShelfFilter.LanguageKey(b);
        if (language != ShelfFilter.NotStated) Fact("LANGUAGE", ShelfFilter.LanguageName(language));
        if (b.Isbn.Trim().Length > 0) Fact("ISBN", b.Isbn.Trim());
        Fact("ADDED", b.AddedUtc.ToLocalTime().ToString("d MMM yyyy"));
        Fact("LAST OPENED", b.LastOpenedUtc is { } opened ? opened.ToLocalTime().ToString("d MMM yyyy") : "Never");
        var seconds = AppServices.Sessions.ForBook(b.StateKey).Sum(s => s.Seconds);
        if (seconds > 0) Fact("TIME READ", ReadingTime.Format(seconds));

        void Fact(string label, string? value, FrameworkElement? valueElement = null)
        {
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 150,
                Opacity = 0.65
            });
            block.Children.Add(valueElement ?? new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true });
            FactsWrap.Children.Add(block);
        }
    }

    private static string Size(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.00} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }

    /// <summary>
    /// The book's subjects, each a search of the library. Files put odd things there (web addresses, codes), and
    /// those are left out rather than offered as subjects.
    /// </summary>
    private void BuildSubjects()
    {
        SubjectChips.Children.Clear();
        var subjects = _book.Subjects.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(LooksLikeSubject).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(24).ToList();
        foreach (var subject in subjects)
        {
            var link = new HyperlinkButton { Content = subject, FontSize = 12, Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(12), MinHeight = 0 };
            ToolTipService.SetToolTip(link, $"Find “{subject}” in the library");
            link.Click += (_, _) => NavigateAndClose(m => m.ShowSearch(subject));
            SubjectChips.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["ChipStyle"],
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(0),
                Child = link
            });
        }
        SubjectsSection.Visibility = SubjectsDivider.Visibility = Vis(subjects.Count > 0);

        static bool LooksLikeSubject(string s) =>
            s.Length is > 1 and <= 60 && s.Any(char.IsLetter) &&
            !s.Contains("://") && !s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            !(s.Length >= 8 && !s.Contains(' ') && s.Any(char.IsUpper) && s.Any(char.IsLower) && s.Any(c => char.IsDigit(c) || c is '_' or '-'));
    }

    // ------------------------------------------------------------ cover and banner

    /// <summary>
    /// The cover, and the same cover decoded tiny for the banner: stretched to the banner's width it comes out soft,
    /// which is the blur (the app has no blur effect to hand, and this costs nothing).
    /// </summary>
    private async Task LoadImagesAsync()
    {
        var path = _book.CoverPath;
        var full = _book.FullPath;
        var (bytes, missing) = await Task.Run(() =>
        {
            byte[]? data = null;
            try
            {
                if (path is not null && File.Exists(path)) data = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                // unreadable cover: the placeholder stays
            }
            return (data, full is not null && !File.Exists(full));
        });
        if (_closed) return;
        if (missing != _fileMissing)
        {
            _fileMissing = missing;
            BuildChips();
        }
        if (bytes is null)
        {
            CoverImage.Source = null;
            HeroImage.Source = null;
            return;
        }
        try
        {
            CoverImage.Source = await Decode(bytes, 420);
            HeroImage.Source = await Decode(bytes, 40);
            CoverTitle.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // a cover the decoder refuses: the title on the placeholder shows instead
        }
    }

    private static async Task<BitmapImage> Decode(byte[] bytes, int width)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage { DecodePixelWidth = width };
        await image.SetSourceAsync(stream);
        return image;
    }

    private void OnOpenLibraryLink(object sender, RoutedEventArgs e)
    {
        if (_book.Online is { IsApplied: true, SourceKey: { } key } && key.StartsWith('/'))
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://openlibrary.org" + key));
    }

    private void OnWikipediaLink(object sender, RoutedEventArgs e)
    {
        if (WikipediaTitle() is { } title)
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'))));
    }

    private string? WikipediaTitle() =>
        _book.OnlineFrom(OnlineSource.Wikipedia) is { IsApplied: true, PageTitle: { Length: > 0 } article } ? article
        : _book.OnlineFrom(OnlineSource.Wikidata) is { IsApplied: true, PageTitle: { Length: > 0 } linked } ? linked
        : null;

    // ------------------------------------------------------------ actions

    private async void OnRead(object sender, RoutedEventArgs e)
    {
        if (_vm.OpenBook(_book) is not { } error)
        {
            ShowActions();
            return;
        }
        await SayAsync("Cannot open this book", error);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e) => BookLauncher.ShowInFolder(_book);

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        _vm.ToggleFavorite(_book);
        ShowActions();
    }

    private void OnStatusChoice(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var value)) return;
        var status = (ReadStatus)value;
        if (status != _book.Status) _vm.SetStatus(_book, status);
        ShowActions();
    }

    /// <summary>Finished long ago: it counts as read, but in no month of the reading log.</summary>
    private void OnFinishedUndated(object sender, RoutedEventArgs e)
    {
        if (_book.Status == ReadStatus.Finished)
        {
            _book.FinishedUtc = null;
            _vm.SaveState(_book);
        }
        else
        {
            _vm.SetStatus(_book, ReadStatus.Finished, stampDate: false);
        }
        ShowActions();
    }

    /// <summary>Only the day counts; it is kept at noon so it stays that day in any time zone change.</summary>
    private void OnFinishedDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_populating) return;
        var picked = sender.Date?.Date;
        if (picked == _book.FinishedUtc?.ToLocalTime().Date) return;
        _book.FinishedUtc = picked is { } day ? DateTime.SpecifyKind(day.AddHours(12), DateTimeKind.Local).ToUniversalTime() : null;
        _vm.SaveState(_book);
        _changed = true;
        ShowActions();
    }

    /// <summary>
    /// Watches the stars' value itself: ValueChanged does not come when a screen reader sets it through UI
    /// Automation, and then the rating was shown but never saved.
    /// </summary>
    private void OnRatingChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_populating) return;
        var rating = (int)Math.Max(0, RatingBox.Value);
        if (rating == _book.Rating) return;
        _book.Rating = rating;
        _vm.SaveState(_book);
        _changed = true;
    }

    private void OnProgressChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_populating) return;
        var progress = (int)e.NewValue;
        ProgressText.Text = $"{progress}%";
        if (progress == _book.Progress) return;
        _book.Progress = progress;
        _progressTimer.Stop();   // a drag saves once, when it stops
        _progressTimer.Start();
    }

    // ------------------------------------------------------------ lists and tags

    private void OnListsOpening(object sender, object e) => BookMenu.FillLists(ListsFlyout.Items, _book, () => RootGrid.XamlRoot);

    /// <summary>A chip per list the book is on: its name opens the list, its ✕ takes the book off it.</summary>
    private void BuildListChips()
    {
        ListChips.Children.Clear();
        foreach (var name in _book.Lists.Where(n => _vm.FindList(n) is not null))
        {
            var list = name;
            var open = new HyperlinkButton { Content = "📑 " + list, FontSize = 12, Padding = new Thickness(0), MinHeight = 0 };
            ToolTipService.SetToolTip(open, $"Show the books on “{list}”");
            open.Click += (_, _) => NavigateAndClose(m => m.ShowList(list));
            ListChips.Children.Add(RemovableChip(open, $"Take it off “{list}”", () =>
            {
                _vm.SetInList(_book, list, false);
                BuildListChips();
                ListsBtn.Focus(FocusState.Programmatic);   // the chip that had it is gone
            }));
        }
        ListChips.Visibility = Vis(ListChips.Children.Count > 0);
    }

    /// <summary>The tags, each a link to its books with a ✕, before the "+ Add tag" button.</summary>
    private void BuildTagChips()
    {
        while (TagsRow.Children.Count > 3) TagsRow.Children.RemoveAt(1);   // keep the icon, the button and the box
        var at = 1;
        foreach (var name in Tags.Split(_book.UserTags))
        {
            var tag = name;
            var open = new HyperlinkButton { Content = tag, FontSize = 12, Padding = new Thickness(0), MinHeight = 0 };
            ToolTipService.SetToolTip(open, $"Every book tagged “{tag}”");
            open.Click += (_, _) => NavigateAndClose(m => m.ShowTag(tag));
            TagsRow.Children.Insert(at++, RemovableChip(open, $"Remove the tag “{tag}”", () =>
            {
                _vm.SetTag(_book, tag, false);
                _changed = true;
                BuildTagChips();
                AddTagBtn.Focus(FocusState.Programmatic);   // the chip that had it is gone
            }));
        }
    }

    private static Border RemovableChip(FrameworkElement content, string removeTip, Action remove)
    {
        var x = new Button
        {
            Content = "✕",
            FontSize = 10,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 18,
            MinHeight = 18,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(x, removeTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(x, removeTip);
        x.Click += (_, _) => remove();
        content.VerticalAlignment = VerticalAlignment.Center;
        // The chip style's brushes follow this window's theme; brushes taken from the app's resources in code would not.
        return new Border
        {
            Style = (Style)Application.Current.Resources["ChipStyle"],
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 4, 3),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { content, x } }
        };
    }

    private void OnAddTagClick(object sender, RoutedEventArgs e)
    {
        AddTagBox.Text = "";
        AddTagBox.Visibility = Visibility.Visible;
        FocusSoon(AddTagBox);
        AddTagBtn.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Focus for a box that has only just been made visible. It cannot take focus until it has been laid out, and
    /// then Enter went to whatever had focus before: a tag's link, which closed the window. Call it before hiding
    /// the button that was clicked: a focused element that goes away passes focus on by itself, later, and that
    /// took it back from the box.
    /// </summary>
    private void FocusSoon(Control control)
    {
        control.UpdateLayout();
        if (!control.Focus(FocusState.Programmatic))
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => control.Focus(FocusState.Programmatic));
    }

    private void CloseTagBox()
    {
        AddTagBox.Text = "";
        AddTagBox.ItemsSource = null;
        AddTagBox.Visibility = Visibility.Collapsed;
        AddTagBtn.Visibility = Visibility.Visible;
    }

    /// <summary>The box goes when the keyboard leaves it, unless it went to one of its own suggestions.</summary>
    private void OnAddTagBlur(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        for (var d = focused; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == AddTagBox) return;
        if (focused is ListViewItem or ListView) return;   // a suggestion in the box's popup
        CloseTagBox();
    });

    private void OnAddTagTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var typed = (sender.Text ?? "").Trim();
        sender.ItemsSource = typed.Length == 0
            ? null
            : _vm.GetTags().Select(t => t.Name)
                .Where(n => n.Contains(typed, StringComparison.CurrentCultureIgnoreCase) && !Tags.Has(_book, n))
                .Take(8).ToList();
    }

    private void OnAddTagSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var name = ((args.ChosenSuggestion as string) ?? sender.Text ?? "").Trim();
        if (name.Length > 0)
        {
            _vm.SetTag(_book, name, true);
            _changed = true;
            BuildTagChips();
        }
        CloseTagBox();
        AddTagBtn.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------ note

    /// <summary>The note as text with Edit, or "+ Add note" when there is none (CineLibrary's notes card).</summary>
    private void ShowNote()
    {
        _editingNote = false;
        var has = _book.HasNote;
        NoteText.Text = _book.Notes;
        NoteText.Visibility = Vis(has);
        NoteEditor.Visibility = Visibility.Collapsed;
        NoteAddBtn.Visibility = Vis(!has);
        NoteEditBtn.Visibility = Vis(has);
        NoteSaveBtn.Visibility = NoteCancelBtn.Visibility = Visibility.Collapsed;
        NoteOfflineHint.Visibility = Visibility.Collapsed;
    }

    private void OnNoteEditStart(object sender, RoutedEventArgs e)
    {
        _editingNote = true;
        NoteEditor.Text = _book.Notes;
        NoteText.Visibility = Visibility.Collapsed;
        NoteEditor.Visibility = Visibility.Visible;
        NoteSaveBtn.Visibility = NoteCancelBtn.Visibility = Visibility.Visible;
        NoteOfflineHint.Visibility = Vis(!_book.IsAvailable);
        NoteEditor.SelectionStart = NoteEditor.Text.Length;
        FocusSoon(NoteEditor);
        NoteAddBtn.Visibility = NoteEditBtn.Visibility = Visibility.Collapsed;
    }

    private void OnNoteCancel(object sender, RoutedEventArgs e) => ShowNote();

    private void OnNoteSave(object sender, RoutedEventArgs e)
    {
        SaveNote();
        ShowNote();
    }

    /// <summary>
    /// The note goes into the index at once, and into the sidecar on the drive a moment later; with the drive
    /// unplugged it is written there when the drive comes back.
    /// </summary>
    private void SaveNote()
    {
        var note = (NoteEditor.Text ?? "").Trim();
        if (note == _book.Notes.Trim()) return;
        _book.Notes = note;
        _vm.SaveState(_book);
        if (_vm.Filter == LibraryFilter.Notes) _changed = true;
    }

    // ------------------------------------------------------------ the user's own title, author and series

    private void OnEditDetails(object sender, RoutedEventArgs e)
    {
        TitleBox.Text = _book.Title;
        TitleBox.PlaceholderText = _book.BaseTitle;
        AuthorBox.Text = _book.Author;
        SeriesBox.Text = _book.Series;
        EditPanel.Visibility = Visibility.Visible;
        FocusSoon(TitleBox);
        EditDetailsLink.Visibility = Visibility.Collapsed;
    }

    private void OnUseFileDetails(object sender, RoutedEventArgs e)
    {
        TitleBox.Text = _book.FileTitle;
        AuthorBox.Text = _book.FileAuthor;
        SeriesBox.Text = _book.FileSeries;
    }

    private void OnSaveDetails(object sender, RoutedEventArgs e)
    {
        SaveDetails();
        CloseEditPanel();
        Populate();
    }

    private void OnCancelDetails(object sender, RoutedEventArgs e) => CloseEditPanel();

    private void CloseEditPanel()
    {
        EditPanel.Visibility = Visibility.Collapsed;
        EditDetailsLink.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// An edit equal to the file's value is no edit (null), so a later rescan's better value shows through. An
    /// empty title is not allowed; an empty author or series means "none".
    /// </summary>
    private void SaveDetails()
    {
        var title = (TitleBox.Text ?? "").Trim();
        var customTitle = title.Length == 0 || title == _openBaseTitle ? null : title;
        var author = (AuthorBox.Text ?? "").Trim();
        var customAuthor = author == _openBaseAuthor ? null : author;
        var series = (SeriesBox.Text ?? "").Trim();
        var customSeries = series == _book.FileSeries ? null : series;
        if (customTitle == _book.CustomTitle && customAuthor == _book.CustomAuthor && customSeries == _book.CustomSeries) return;
        _book.SetCustomDetails(customTitle, customAuthor, customSeries);
        _vm.SaveState(_book);
        _changed = true;
    }

    // ------------------------------------------------------------ online details

    private void ShowOnlineState()
    {
        var o = _book.Online;
        if (o is { Status: OnlineDetails.Suggested })
        {
            var by = string.IsNullOrWhiteSpace(o.Author) ? "" : " by " + o.Author;
            var year = o.Year is { } y ? $" ({y})" : "";
            SuggestionText.Text = $"“{o.Title}”{by}{year}" +
                                  (o.How == OnlineDetails.ByIsbn ? ", found by the ISBN printed in the book." : ".");
            SuggestionBar.IsOpen = true;
        }
        else
        {
            SuggestionBar.IsOpen = false;
        }

        // What came from where: Open Library's record, plus anything Wikidata or Wikipedia filled in.
        var from = new List<string>();
        if (o is { IsApplied: true })
            from.Add(o.How switch
            {
                OnlineDetails.ByIsbn => "Open Library, found by the ISBN in the book",
                OnlineDetails.ByMatch => "Open Library, matched by title and author",
                _ => "Open Library, chosen by you"
            });
        if (_book.DescriptionSource is { } d && d != OnlineSource.OpenLibrary) from.Add("the description from " + OnlineSource.Name(d));
        if (_book.SeriesSource is { } s) from.Add("the series from " + OnlineSource.Name(s));
        if (_book.CoverSource is { } c && c != OnlineSource.OpenLibrary) from.Add("the cover from " + OnlineSource.Name(c));
        if (_book.YearSource is { } y2 && from.Count == 0) from.Add("the year from " + OnlineSource.Name(y2));
        OnlineNoteText.Text = from.Count > 0 ? "Details from " + Join(from) + "." : "";
        OnlineNote.Visibility = Vis(from.Count > 0);

        OpenLibraryLink.Visibility = Vis(o is { IsApplied: true, SourceKey: { } key } && key.StartsWith('/'));
        WikipediaLink.Visibility = Vis(WikipediaTitle() is not null);
    }

    private static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>The suggestion is this book: its description and cover are fetched, and show here when they arrive.</summary>
    private void OnUseSuggestion(object sender, RoutedEventArgs e)
    {
        if (_book.Online is not { Status: OnlineDetails.Suggested } s) return;
        var o = s.Copy();
        o.Status = OnlineDetails.Found;
        o.How = OnlineDetails.ByPick;
        o.UseCover = _book.NeedsCover;
        AppServices.Online.ApplyPicked(_book, o);
        _changed = true;
        Populate();
    }

    private void OnRejectSuggestion(object sender, RoutedEventArgs e)
    {
        var o = new OnlineDetails { Status = OnlineDetails.None };   // remembered, so it is not suggested again
        AppServices.Repo.UpsertOnline(_book.StateKey, o);
        _book.SetOnline(o);
        SuggestionBar.IsOpen = false;
    }

    /// <summary>Drops every online detail for this book, and remembers not to look it up again by itself.</summary>
    private void OnRemoveOnline(object sender, RoutedEventArgs e)
    {
        foreach (var source in new[] { OnlineSource.OpenLibrary, OnlineSource.Wikidata, OnlineSource.Wikipedia })
        {
            var o = new OnlineDetails { Source = source, Status = OnlineDetails.None };
            AppServices.Repo.UpsertOnline(_book.StateKey, o);
            _book.SetOnline(o);
        }
        _changed = true;
        Populate();
        _ = LoadImagesAsync();
    }

    private async void OnFindOnline(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || RootGrid.XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            await new FindOnlineDialog(_book) { XamlRoot = RootGrid.XamlRoot }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        if (_closed) return;
        _changed = true;
        Populate();
        _ = LoadImagesAsync();
    }

    private async Task SayAsync(string title, string text)
    {
        if (_dialogOpen || RootGrid.XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            await new ContentDialog { Title = title, Content = text, CloseButtonText = "OK", XamlRoot = RootGrid.XamlRoot }.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
