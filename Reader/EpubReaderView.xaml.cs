using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AryanEbookLibrary.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.System;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Reads an EPUB, MOBI or AZW3 book. foliate-js (the engine of the Foliate reader) lays out and turns the pages in a
/// web view; the app keeps everything around them, as for a PDF: contents, find in book, page colour, Define and the
/// reading record. The page is served from the app's own files under a made-up host and the book from its file, so
/// nothing is fetched from the internet. A "page" here is a foliate location, about 1,500 bytes of the book's text.
/// </summary>
public sealed partial class EpubReaderView : UserControl, IReaderView
{
    private const string Origin = "https://reader.aryan/";
    private const string BookHost = "book.aryan";
    private const string PositionPrefix = "epub1;";
    private const int MinFont = 60, MaxFont = 240, FontStep = 10;
    private static readonly string AssetRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Epub");

    public event Action<int, int>? PageChanged;
    public event Action? Activity;
    public event Action? FullScreenRequested;
    public event Action? EscapeRequested;
    public event Action? FinishedRequested;
    public event Action? OpenExternallyRequested;

    private string _path = "";
    private string _bookUrl = "";
    private ReadingPosition? _pending;
    private readonly Stopwatch _openClock = new();
    private bool _closed;
    private bool _endOffered;
    private int _current = -1;
    private int _total;
    private string _cfi = "";
    private string _theme;
    private int _fontSize;
    private string _flow;
    private string _searchQuery = "";

    public EpubReaderView()
    {
        InitializeComponent();
        var settings = AppServices.Settings;
        _theme = settings.ReaderPageTheme is "Sepia" or "Night" ? settings.ReaderPageTheme : "Paper";
        (_theme switch { "Sepia" => SepiaItem, "Night" => NightItem, _ => PaperItem }).IsChecked = true;
        _fontSize = Math.Clamp(settings.ReaderFontSize, MinFont, MaxFont);
        FontSizeText.Text = $"{_fontSize}%";
        _flow = settings.ReaderFlow == "scrolled" ? "scrolled" : "paginated";
        (_flow == "scrolled" ? ScrollRadio : PagesRadio).IsChecked = true;
        Web.DefaultBackgroundColor = PageColor();
        SetContentsOpen(settings.ReaderContentsOpen, remember: false);
        AddAccelerators();
    }

    // ================================================================ opening

    public int PageCount => _total;

    /// <summary>Whether reaching the last page offers "Mark as finished". The window says, from the book's status.</summary>
    public bool OfferFinish { get; set; } = true;

    public async Task OpenAsync(string path, ReadingPosition? position)
    {
        _path = path;
        _pending = position;
        _openClock.Restart();
        // By its own name, as foliate tells some formats apart by the extension. The browser reads files by path
        // and stops at Windows' 260-character limit, so a longer path is served by the handler below instead.
        _bookUrl = path.Length < 260
            ? $"https://{BookHost}/{Uri.EscapeDataString(Path.GetFileName(path))}"
            : Origin + "book/book" + Path.GetExtension(path).ToLowerInvariant();
        ShowMessage("Opening the book...", "", ring: true);

        if (await Task.Run(() => LockedReason(path)) is { } locked)
        {
            Log.Write($"reader: {Path.GetFileName(path)} is locked (DRM)");
            ShowMessage("This book is locked", locked, ring: false, external: true);
            return;
        }

        try
        {
            if (!IsLoaded)
            {
                var loaded = new TaskCompletionSource();
                Loaded += (_, _) => loaded.TrySetResult();
                await loaded.Task;
            }
            // The web view's own files (cache, settings) live with the library's data, like everything else.
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("",
                Path.Combine(AppPaths.DataDir, "WebView2"), new CoreWebView2EnvironmentOptions());
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Log.Write("reader: the web view could not start: " + ex.Message);
            ShowMessage("The reader could not start",
                "It needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and can be installed on Windows 10.",
                ring: false, external: true);
            return;
        }
        if (_closed) return;

        var core = Web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        // SmartScreen checked every page the book loads, 300 to 400 ms each: a Kindle comic took a second to turn.
        // Only the reader's own page and the book's own pages are ever shown here.
        settings.IsReputationCheckingRequired = false;
        // The book's folder, read by the browser itself: through the handler below, a 140 MB book took seven
        // seconds to arrive. Only this window's page is ever loaded, so only it can ask for anything there.
        if (path.Length < 260)
            core.SetVirtualHostNameToFolderMapping(BookHost, Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"reader: the web view failed ({e.ProcessFailedKind})");
            ShowMessage("The reader stopped", "Close this window and open the book again.", ring: false, external: true);
        };
        core.Navigate(Origin + "reader.html");
    }

    /// <summary>
    /// Why the book cannot be read here, or null. Books sold with DRM are encrypted, and foliate would show them as
    /// garbled text rather than refuse them: a Kindle book says so in its MOBI header, an Adobe one has rights.xml.
    /// </summary>
    private static string? LockedReason(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[4];
            if (file.Read(head) < 4) return null;
            if (head[0] == (byte)'P' && head[1] == (byte)'K')
            {
                file.Position = 0;
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                return zip.GetEntry("META-INF/rights.xml") is null ? null
                    : "It is protected with Adobe DRM, which only the shop's own reading apps can open.";
            }

            // PalmDB: the first record's offset is at byte 78; the MOBI header's encryption type is 12 bytes into it.
            var header = new byte[82];
            file.Position = 0;
            if (file.Read(header) < 82 || System.Text.Encoding.ASCII.GetString(header, 60, 8) != "BOOKMOBI") return null;
            file.Position = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(78)) + 12;
            var encryption = new byte[2];
            return file.Read(encryption) == 2 && BinaryPrimitives.ReadUInt16BigEndian(encryption) != 0
                ? "It is a Kindle book protected with DRM, which only Amazon's own Kindle apps can open."
                : null;
        }
        catch (Exception ex)
        {
            Log.Write($"reader: checking {Path.GetFileName(path)} for DRM: {ex.Message}");
            return null;
        }
    }

    /// <summary>The reader's own files, and a book whose path is too long for the browser at /book/.</summary>
    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = sender.Environment;
        var file = FileFor(e.Request.Uri);
        if (file is null || !File.Exists(file))
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        try
        {
            var type = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "application/octet-stream",
            };
            var stream = File.OpenRead(file).AsRandomAccessStream();
            e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}\nCache-Control: no-store");
        }
        catch (Exception ex)
        {
            Log.Write($"reader: serving {e.Request.Uri}: {ex.Message}");
            e.Response = environment.CreateWebResourceResponse(null, 500, "Error", "");
        }
    }

    /// <summary>The file behind an address: the book, or one of the reader's own files and nothing outside them.</summary>
    private string? FileFor(string uri)
    {
        if (!uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = Uri.UnescapeDataString(new Uri(uri).AbsolutePath.TrimStart('/'));
        if (relative.StartsWith("book/", StringComparison.Ordinal)) return _path;
        var full = Path.GetFullPath(Path.Combine(AssetRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(AssetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ================================================================ messages from the page

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed) return;
        JsonElement m;
        try
        {
            m = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        switch (Str(m, "type"))
        {
            case "ready":
                var cfi = _pending?.Position is { } p && p.StartsWith(PositionPrefix, StringComparison.Ordinal) ? p[PositionPrefix.Length..] : null;
                Post(new JsonObject { ["type"] = "open", ["url"] = _bookUrl, ["lastLocation"] = cfi, ["prefs"] = Prefs() });
                break;
            case "opened": OnOpened(m); break;
            case "error": OnOpenFailed(Str(m, "message")); break;
            case "relocate": OnRelocate(m); break;
            case "activity": Activity?.Invoke(); break;
            case "key": OnPageKey(m); break;
            case "context": OnContext(m); break;
            case "external": OpenLink(Str(m, "href")); break;
            case "search": OnSearchProgress(m); break;
            case "probe": ShowDefinition(Str(m, "word"), RectOf(m)); break;
        }
    }

    private void Post(JsonObject message) => Web.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());

    private JsonObject Prefs() => new() { ["theme"] = _theme.ToLowerInvariant(), ["fontSize"] = _fontSize, ["flow"] = _flow };

    private static string Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Flag(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static Rect? RectOf(JsonElement m) =>
        m.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object
            ? new Rect(Num(r, "x"), Num(r, "y"), Math.Max(0, Num(r, "width")), Math.Max(0, Num(r, "height")))
            : null;

    private void OnOpened(JsonElement m)
    {
        HideMessage();
        var rows = new List<TocRow>();
        if (m.TryGetProperty("toc", out var toc) && toc.ValueKind == JsonValueKind.Array)
            foreach (var item in toc.EnumerateArray())
                rows.Add(new TocRow(Str(item, "label"), Str(item, "href"), (int)Num(item, "depth")));
        TocList.ItemsSource = rows;
        NoTocText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Log.Write($"reader: opened {Path.GetFileName(_path)}, {rows.Count} contents entries, in {_openClock.ElapsedMilliseconds} ms");
        ProbeDefine();
    }

    private void OnOpenFailed(string message)
    {
        Log.Write($"reader: could not open {_path}: {message}");
        ShowMessage("This book could not be opened",
            "The file may be damaged, or not really the kind of book its name says. The app it was made with, or another reader, may still open it.",
            ring: false, external: true);
    }

    private void OnRelocate(JsonElement m)
    {
        Definition.Hide();
        var current = (int)Num(m, "current");
        var next = Math.Max(current, (int)Num(m, "next"));
        _current = current;
        _total = (int)Num(m, "total");
        _cfi = Str(m, "cfi");

        ProgressText.Text = $"{Math.Floor(Num(m, "fraction") * 100):0}%";
        ToolTipService.SetToolTip(ProgressText, _total > 0 ? $"Page {current + 1:N0} of {_total:N0}" : null);
        var chapter = Str(m, "chapter");
        ChapterText.Text = chapter;
        ToolTipService.SetToolTip(ChapterText, chapter.Length > 0 ? chapter : null);

        PageChanged?.Invoke(current, next);
        if (OfferFinish && !_endOffered && _total > 0 && next >= _total - 1)
        {
            _endOffered = true;
            EndBar.IsOpen = true;
        }
    }

    /// <summary>Keys pressed on the page: the web view keeps its keyboard, so the ones the app answers come here.</summary>
    private void OnPageKey(JsonElement m)
    {
        switch (Str(m, "key"))
        {
            case "F11": FullScreenRequested?.Invoke(); break;
            case "Escape":
                if (!HandleEscape()) EscapeRequested?.Invoke();
                break;
            case "F3": StepSearch(Flag(m, "shift") ? -1 : 1); break;
            case "f" or "F": FocusSearch(); break;
            case "=" or "+": SetFontSize(_fontSize + FontStep); break;
            case "-": SetFontSize(_fontSize - FontStep); break;
            case "0": SetFontSize(100); break;
        }
    }

    private static void OpenLink(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            _ = Launcher.LaunchUriAsync(uri);
    }

    // ================================================================ right-click and Define

    private void OnContext(JsonElement m)
    {
        var box = RectOf(m);
        var menu = WordMenu.Build(Str(m, "selection"), Str(m, "word"),
            define: w => ShowDefinition(w, box),
            copy: WordMenu.CopyText,
            find: text =>
            {
                SearchBox.Text = text;
                StartSearch();
            });
        menu?.ShowAt(Web, new FlyoutShowOptions { Position = new Point(Num(m, "x"), Num(m, "y")) });
    }

    private void ShowDefinition(string word, Rect? box)
    {
        if (word.Length == 0) return;
        Definition.Show(word);
        Definition.PlaceNear(box, new Size(Surface.ActualWidth, Surface.ActualHeight));
    }

    /// <summary>
    /// For checking Define without a mouse: ARYAN_READER_DEFINE=word shows the definition of the first place that
    /// word is on the page the book opens at, as a right-click and Define would. Nothing happens without it.
    /// </summary>
    private async void ProbeDefine()
    {
        var word = Environment.GetEnvironmentVariable("ARYAN_READER_DEFINE");
        if (string.IsNullOrWhiteSpace(word)) return;
        await Task.Delay(1500);
        Post(new JsonObject { ["type"] = "probe", ["word"] = word });
    }

    // ================================================================ find in book

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = "";
            FocusPages();
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        StepSearch(shift ? -1 : 1);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length == 0) StopSearch();
    }

    private void OnSearchNext(object sender, RoutedEventArgs e) => StepSearch(1);
    private void OnSearchPrevious(object sender, RoutedEventArgs e) => StepSearch(-1);

    private void StepSearch(int direction)
    {
        if (SearchBox.Text.Trim() != _searchQuery) StartSearch();
        else if (_searchQuery.Length > 0) Post(new JsonObject { ["type"] = "searchStep", ["direction"] = direction });
    }

    private void StartSearch()
    {
        _searchQuery = SearchBox.Text.Trim();
        if (_searchQuery.Length == 0)
        {
            StopSearch();
            return;
        }
        SearchCountText.Text = "Searching...";
        Post(new JsonObject { ["type"] = "search", ["query"] = _searchQuery });
    }

    private void StopSearch()
    {
        if (_searchQuery.Length == 0) return;
        _searchQuery = "";
        SearchCountText.Text = "";
        Post(new JsonObject { ["type"] = "clearSearch" });
    }

    private void OnSearchProgress(JsonElement m)
    {
        if (_searchQuery.Length == 0) return;
        var count = (int)Num(m, "count");
        var index = (int)Num(m, "index");
        if (Flag(m, "done"))
            SearchCountText.Text = count == 0 ? "No matches" : $"{index + 1:N0} of {count:N0}";
        else
            SearchCountText.Text = index >= 0 ? $"{index + 1:N0} of {count:N0}..." : $"Searching {Num(m, "progress"):P0}";
    }

    private void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    // ================================================================ toolbar

    private void OnPreviousPage(object sender, RoutedEventArgs e)
    {
        Post(new JsonObject { ["type"] = "prev" });
        FocusPages();
    }

    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        Post(new JsonObject { ["type"] = "next" });
        FocusPages();
    }

    private void OnSmallerText(object sender, RoutedEventArgs e) => SetFontSize(_fontSize - FontStep);
    private void OnLargerText(object sender, RoutedEventArgs e) => SetFontSize(_fontSize + FontStep);

    private void SetFontSize(int size)
    {
        size = Math.Clamp(size, MinFont, MaxFont);
        if (size == _fontSize) return;
        _fontSize = size;
        FontSizeText.Text = $"{size}%";
        AppServices.Settings.ReaderFontSize = size;
        AppServices.Settings.Save();
        Post(new JsonObject { ["type"] = "prefs", ["prefs"] = Prefs() });
    }

    private void OnFlowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag is not string flow || flow == _flow) return;
        _flow = flow;
        AppServices.Settings.ReaderFlow = flow;
        AppServices.Settings.Save();
        Post(new JsonObject { ["type"] = "prefs", ["prefs"] = Prefs() });
    }

    private void OnPageTheme(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag is not string tag || tag == _theme) return;
        _theme = tag;
        AppServices.Settings.ReaderPageTheme = tag;
        AppServices.Settings.Save();
        Web.DefaultBackgroundColor = PageColor();
        Post(new JsonObject { ["type"] = "prefs", ["prefs"] = Prefs() });
    }

    /// <summary>The same page colours as the PDF reader's, so a white page never flashes up in Night.</summary>
    private Windows.UI.Color PageColor() => _theme switch
    {
        "Sepia" => Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xEC, 0xD8),
        "Night" => Windows.UI.Color.FromArgb(0xFF, 0x12, 0x12, 0x12),
        _ => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
    };

    public void SetTimeLeft(string text) => TimeLeftText.Text = text;

    private void OnMarkFinished(object sender, RoutedEventArgs e)
    {
        EndBar.IsOpen = false;
        FinishedRequested?.Invoke();
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    /// <summary>Narrow windows lose the least-needed parts first.</summary>
    private void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        TimeLeftText.Visibility = w < 900 ? Visibility.Collapsed : Visibility.Visible;
        ChapterText.Visibility = w < 820 ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Width = w < 760 ? 130 : 200;
        SearchCountText.Visibility = w < 700 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnContentsClick(object sender, RoutedEventArgs e) => SetContentsOpen(ContentsButton.IsChecked == true, remember: true);

    private void SetContentsOpen(bool open, bool remember)
    {
        ContentsButton.IsChecked = open;
        ContentsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!remember) return;
        AppServices.Settings.ReaderContentsOpen = open;
        AppServices.Settings.Save();
    }

    private void OnTocClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TocRow row || row.Href.Length == 0) return;
        Post(new JsonObject { ["type"] = "goTo", ["href"] = row.Href });
        FocusPages();
    }

    private void OnOpenExternally(object sender, RoutedEventArgs e) => OpenExternallyRequested?.Invoke();

    private void ShowMessage(string title, string text, bool ring, bool external = false)
    {
        MessagePanel.Visibility = Visibility.Visible;
        MessageRing.IsActive = ring;
        MessageRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExternalButton.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        // The message sits over the page, which is empty or broken while it shows.
        Web.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideMessage()
    {
        MessagePanel.Visibility = Visibility.Collapsed;
        MessageRing.IsActive = false;
        Web.Visibility = Visibility.Visible;
    }

    /// <summary>For when the toolbar or the search box has the keyboard; on the page, reader.js sends these.</summary>
    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                action();
                e.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.F, VirtualKeyModifiers.Control, FocusSearch);
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize + FontStep));
        Add((VirtualKey)187, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize + FontStep));     // the = + key
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize - FontStep));
        Add((VirtualKey)189, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize - FontStep));     // the - key
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetFontSize(100));
        Add(VirtualKey.F3, VirtualKeyModifiers.None, () => StepSearch(1));
        Add(VirtualKey.F3, VirtualKeyModifiers.Shift, () => StepSearch(-1));
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // ================================================================ IReaderView

    public ReadingPosition? Position() =>
        _cfi.Length == 0 || _current < 0 ? null : new ReadingPosition(_current, _total, PositionPrefix + _cfi);

    public bool HandleEscape()
    {
        if (!Definition.IsOpen) return false;
        Definition.Hide();
        return true;
    }

    public void FocusPages() => Web.Focus(FocusState.Programmatic);

    /// <summary>Hides the toolbar for full screen, and brings it back.</summary>
    public void SetChromeVisible(bool visible)
    {
        ToolBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) ContentsPane.Visibility = Visibility.Collapsed;
        else SetContentsOpen(ContentsButton.IsChecked == true, remember: false);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Web.Close();
    }
}

/// <summary>One line of the contents pane: a table of contents entry, indented by its depth.</summary>
public sealed class TocRow
{
    public TocRow(string title, string href, int depth)
    {
        Title = title;
        Href = href;
        Indent = new Thickness(Math.Min(depth, 6) * 14, 0, 0, 0);
        Weight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public string Title { get; }
    public string Href { get; }
    public Thickness Indent { get; }
    public Windows.UI.Text.FontWeight Weight { get; }
}
