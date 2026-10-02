using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.Services.Metadata;
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
/// Reads an EPUB, MOBI or AZW3 book, and a KFX book through its EPUB copy (KfxBook). foliate-js (the engine of the Foliate reader) lays out and turns the pages in a
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
    public event Action? ShortcutsRequested;
    public event Action? CloseRequested;

    public event Action? LibraryRequested;

    private void OnLibraryClick(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke();

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
    private bool _settingUp = true;   // the text choices are being shown, not made
    private string _searchQuery = "";
    private double _fraction;
    private readonly ToolbarReveal _reveal;
    private bool _fullScreen;

    public EpubReaderView()
    {
        InitializeComponent();
        var settings = AppServices.Settings;
        _theme = settings.ReaderPageTheme is "Sepia" or "Night" ? settings.ReaderPageTheme : "Paper";
        (_theme switch { "Sepia" => SepiaItem, "Night" => NightItem, _ => PaperItem }).IsChecked = true;
        _fontSize = Math.Clamp(settings.ReaderFontSize, MinFont, MaxFont);
        ZoomButton.Configure(MinFont, MaxFont, FontStep, "Smaller text", "Larger text", ("usual", "Usual size"));
        ZoomButton.Stepped += step => SetFontSize(_fontSize + step * FontStep);
        ZoomButton.SliderMoved += percent => SetFontSize((int)Math.Round(percent / FontStep) * FontStep);
        ZoomButton.FitClicked += _ => SetFontSize(100);
        ShowFontSize();
        _flow = settings.ReaderFlow == "scrolled" ? "scrolled" : "paginated";
        (_flow == "scrolled" ? ScrollRadio : PagesRadio).IsChecked = true;
        Select(FontBox, settings.ReaderFont, "book");
        Select(SpacingBox, settings.ReaderLineSpacing, "normal");
        Select(WidthBox, settings.ReaderTextWidth, "medium");
        JustifySwitch.IsOn = settings.ReaderJustify;
        _settingUp = false;
        Web.DefaultBackgroundColor = PageColor();
        SetContentsOpen(settings.ReaderContentsOpen, remember: false);
        ChooseTab(TabNamed(settings.ReaderPaneTab));
        _reveal = new ToolbarReveal(Root, ToolBar, ToolBarBack, ContentsPane, FocusPages);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSideButton), true);
        AddAccelerators();
    }

    // ================================================================ opening

    public int PageCount => _total;

    /// <summary>Whether reaching the last page offers "Mark as finished". The window says, from the book's status.</summary>
    public bool OfferFinish { get; set; } = true;

    public async Task OpenAsync(string path, ReadingPosition? position)
    {
        _pending = position;
        _openClock.Restart();
        // A KFX book is read through its EPUB copy, made now if the scan has not made it yet.
        if (FormatHelper.FromPath(path) == BookFormat.Kfx)
        {
            ShowMessage("Opening the book...", "", ring: true);
            var (epub, problem) = await KfxBook.EpubAsync(path);
            if (epub is null)
            {
                ShowMessage(problem!.Locked ? "This book is locked" : "This book cannot be opened here", problem.Text, ring: false, external: true);
                return;
            }
            path = epub;
        }
        _path = path;
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
                Post(new JsonObject { ["type"] = "open", ["url"] = _bookUrl, ["lastLocation"] = cfi, ["prefs"] = Prefs(), ["marks"] = Marks() });
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
            case "top": _reveal.PointerAtTop(Flag(m, "near")); break;
            case "selection": OnSelection(m); break;
            case "mark": OnMarkClicked(m); break;
            case "tap": _reveal.Show(ToolbarReveal.TapHold); break;
            case "readText": OnReadText(Str(m, "text")); break;
            case "readEnd": StopReading(); break;
        }
    }

    private void Post(JsonObject message) => Web.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());

    private JsonObject Prefs()
    {
        var s = AppServices.Settings;
        return new()
        {
            ["theme"] = _theme.ToLowerInvariant(), ["fontSize"] = _fontSize, ["flow"] = _flow,
            ["font"] = s.ReaderFont, ["spacing"] = s.ReaderLineSpacing, ["width"] = s.ReaderTextWidth, ["justify"] = s.ReaderJustify,
        };
    }

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
        _reveal.Show(ToolbarReveal.Glimpse);
        var rows = new List<TocRow>();
        if (m.TryGetProperty("toc", out var toc) && toc.ValueKind == JsonValueKind.Array)
            foreach (var item in toc.EnumerateArray())
                rows.Add(new TocRow(Str(item, "label"), Str(item, "href"), (int)Num(item, "depth")));
        TocList.ItemsSource = rows;
        ShowPaneTab();
        Log.Write($"reader: opened {Path.GetFileName(_path)}, {rows.Count} contents entries, in {_openClock.ElapsedMilliseconds} ms");
        ProbeDefine();
        ProbeHighlights();
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
        if (_notes?.BarOpen == true && !_barStays) _notes.HideBar();
        _barStays = false;
        var current = (int)Num(m, "current");
        var next = Math.Max(current, (int)Num(m, "next"));
        _current = current;
        _total = (int)Num(m, "total");
        _cfi = Str(m, "cfi");

        _fraction = Num(m, "fraction");
        ShowProgress();
        ToolTipService.SetToolTip(ProgressBox, _total > 0
            ? $"Page {current + 1:N0} of {_total:N0}. Type a percentage to go there (Ctrl+G)"
            : "Type a percentage to go there (Ctrl+G)");
        var chapter = Str(m, "chapter");
        _chapter = chapter;
        ShowBookmark();
        ChapterText.Text = chapter;
        ToolTipService.SetToolTip(ChapterText, chapter.Length > 0 ? chapter : null);

        PageChanged?.Invoke(current, next);
        if (_pendingReveal is { } reveal)
        {
            _pendingReveal = null;
            Reveal(reveal);
        }
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
            case "F1": ShortcutsRequested?.Invoke(); break;
            case "Alt": FocusToolbar(); break;
            case "F3": StepSearch(Flag(m, "shift") ? -1 : 1); break;
            case "f" or "F": FocusSearch(); break;
            case "g" or "G": FocusProgress(); break;
            case "w" or "W": CloseRequested?.Invoke(); break;
            case "d" or "D": ToggleBookmark(); break;
            case "u" or "U" when Flag(m, "shift"): ToggleReading(); break;
            case "F2": StepBookmark(Flag(m, "shift") ? -1 : 1); break;
            case "h" or "H" when !Flag(m, "shift"): _notes?.KeepOffered(); break;
            case "c" or "C": TogglePaneTab("Contents"); break;
            case "p" or "P": TogglePaneTab("Pages"); break;
            case "h" or "H": TogglePaneTab("Highlights"); break;
            case "t" or "T": NextPageTheme(); break;
            case "l" or "L" when !Flag(m, "shift"): ToggleFlow(); break;
            case "ArrowRight" or "ArrowLeft":
                var step = Str(m, "key") == "ArrowRight" ? 1 : -1;
                if (!Flag(m, "shift")) StepChapter(step);
                else SkipReading(step);
                break;
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
        var selected = Str(m, "selection");
        var menu = WordMenu.Build(selected, Str(m, "word"),
            define: w => ShowDefinition(w, box),
            copy: WordMenu.CopyText,
            find: text =>
            {
                SearchBox.Text = text;
                StartSearch();
            }) ?? new MenuFlyout();
        if (_notes is not null)
        {
            var canHighlight = selected.Length > 0 && Str(m, "cfi").Length > 0;
            WordMenu.AddNoteItems(menu,
                canHighlight ? color => { OnSelection(m); _notes.Choose(color); } : null,
                canHighlight ? () => { OnSelection(m); _notes.StartNote(); } : null,
                _cfi.Length > 0 ? NewPageNote : null);
        }
        if (menu.Items.Count == 0) return;
        menu.ShowAt(Web, new FlyoutShowOptions { Position = new Point(Num(m, "x"), Num(m, "y")) });
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

    /// <summary>
    /// For checking highlights without a mouse, each only when its variable is set: ARYAN_READER_SELECT=words selects
    /// the first place those words are on the page the book opens at, as a drag would, and the bar shows for UI
    /// Automation to pick a colour; ARYAN_READER_PAGENOTE=1 starts a note on the page.
    /// </summary>
    private async void ProbeHighlights()
    {
        var select = Environment.GetEnvironmentVariable("ARYAN_READER_SELECT");
        var note = Environment.GetEnvironmentVariable("ARYAN_READER_PAGENOTE");
        if (string.IsNullOrWhiteSpace(select) && string.IsNullOrWhiteSpace(note)) return;
        await Task.Delay(2500);
        if (!string.IsNullOrWhiteSpace(select)) Post(new JsonObject { ["type"] = "probeSelect", ["word"] = select });
        else NewPageNote();
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

    /// <summary>How far through the book, unless the reader is typing a place to go to.</summary>
    private void ShowProgress()
    {
        if (XamlRoot is not null && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), ProgressBox)) return;
        ProgressBox.Text = $"{Math.Floor(_fraction * 100):0}%";
    }

    private void FocusProgress()
    {
        ProgressBox.Focus(FocusState.Keyboard);
        ProgressBox.SelectAll();
    }

    private void OnProgressGotFocus(object sender, RoutedEventArgs e) => ProgressBox.SelectAll();

    private void OnProgressLostFocus(object sender, RoutedEventArgs e) => ShowProgress();

    /// <summary>Enter goes to the percentage typed; Escape goes back to the page without moving.</summary>
    private void OnProgressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Escape)) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Enter
            && double.TryParse(ProgressBox.Text.Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var percent))
            Post(new JsonObject { ["type"] = "fraction", ["fraction"] = Math.Clamp(percent, 0, 100) / 100 });
        FocusPages();
    }

    /// <summary>The mouse's side buttons over the toolbar or the contents; on the page, reader.js hears them.</summary>
    private void OnSideButton(object sender, PointerRoutedEventArgs e)
    {
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsXButton1Pressed) Post(new JsonObject { ["type"] = "back" });
        else if (buttons.IsXButton2Pressed) Post(new JsonObject { ["type"] = "forward" });
        else return;
        e.Handled = true;
    }

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

    private void SetFontSize(int size)
    {
        size = Math.Clamp(size, MinFont, MaxFont);
        if (size == _fontSize) return;
        _fontSize = size;
        ShowFontSize();
        AppServices.Settings.ReaderFontSize = size;
        AppServices.Settings.Save();
        Post(new JsonObject { ["type"] = "prefs", ["prefs"] = Prefs() });
    }

    private void ShowFontSize() => ZoomButton.Show(_fontSize == 100 ? "Usual size" : $"Text {_fontSize}%", _fontSize);

    /// <summary>Font, line spacing, text width or justify: kept for every book, and the open one follows at once.</summary>
    private void OnTextStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        var s = AppServices.Settings;
        s.ReaderFont = Picked(FontBox, "book");
        s.ReaderLineSpacing = Picked(SpacingBox, "normal");
        s.ReaderTextWidth = Picked(WidthBox, "medium");
        s.ReaderJustify = JustifySwitch.IsOn;
        s.Save();
        Post(new JsonObject { ["type"] = "prefs", ["prefs"] = Prefs() });
    }

    private static void Select(ComboBox box, string value, string fallback)
    {
        var items = box.Items.OfType<ComboBoxItem>().ToList();
        box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == value) ?? items.First(i => (string)i.Tag == fallback);
    }

    private static string Picked(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void OnFlowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag is string flow) SetFlow(flow);
    }

    /// <summary>Ctrl+L: Pages or Scroll, whichever is not being used.</summary>
    private void ToggleFlow()
    {
        var radio = _flow == "scrolled" ? PagesRadio : ScrollRadio;
        radio.IsChecked = true;
        SetFlow((string)radio.Tag);
    }

    private void SetFlow(string flow)
    {
        if (flow == _flow) return;
        _flow = flow;
        AppServices.Settings.ReaderFlow = flow;
        AppServices.Settings.Save();
        Post(new JsonObject { ["type"] = "prefs", ["prefs"] = Prefs() });
    }

    private void OnPageTheme(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioMenuFlyoutItem)?.Tag is string tag) SetPageTheme(tag);
    }

    /// <summary>Ctrl+Shift+T: Paper, Sepia, Night, and round again.</summary>
    private void NextPageTheme()
    {
        var item = _theme switch { "Paper" => SepiaItem, "Sepia" => NightItem, _ => PaperItem };
        item.IsChecked = true;
        SetPageTheme((string)item.Tag);
    }

    private void SetPageTheme(string tag)
    {
        if (tag == _theme) return;
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

    private void OnShortcuts(object sender, RoutedEventArgs e) => ShortcutsRequested?.Invoke();

    /// <summary>
    /// Narrow windows lose the least-needed parts first. The chapter takes whatever room is left, cut short with an
    /// ellipsis; it is kept while there is room for this much of it.
    /// </summary>
    private void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double ChapterRoom = 160;
        ToolbarFit.Fit(ToolBar, Root.ActualWidth,
            () =>
            {
                TimeLeftText.Visibility = ChapterText.Visibility = SearchCountText.Visibility = ZoomButton.Visibility =
                    ShortcutsButton.Visibility = LibraryLabel.Visibility = BookmarkButton.Visibility = ReadAloudButton.Visibility =
                    Visibility.Visible;
                SearchBox.Width = 200;
                ChapterText.Width = ChapterRoom;
            },
            () => TimeLeftText.Visibility = Visibility.Collapsed,
            () => ChapterText.Visibility = Visibility.Collapsed,
            () => SearchBox.Width = 130,
            () => LibraryLabel.Visibility = Visibility.Collapsed,
            () => SearchCountText.Visibility = Visibility.Collapsed,
            () => ZoomButton.Visibility = Visibility.Collapsed,
            () => ShortcutsButton.Visibility = Visibility.Collapsed,
            () => ReadAloudButton.Visibility = Visibility.Collapsed,   // Ctrl+Shift+U still reads
            () => BookmarkButton.Visibility = Visibility.Collapsed);   // Ctrl+D still marks the page
        ChapterText.Width = double.NaN;
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

    /// <summary>Ctrl+Right and Ctrl+Left: the next chapter, or the one before the chapter being read.</summary>
    private void StepChapter(int step) => Post(new JsonObject { ["type"] = "chapter", ["step"] = step });

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

        // In a text box these move and select by words: they are the box's there.
        bool OffText(Action action)
        {
            if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox) return false;
            action();
            return true;
        }

        Add(VirtualKey.F, VirtualKeyModifiers.Control, FocusSearch);
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize + FontStep));
        Add((VirtualKey)187, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize + FontStep));     // the = + key
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize - FontStep));
        Add((VirtualKey)189, VirtualKeyModifiers.Control, () => SetFontSize(_fontSize - FontStep));     // the - key
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetFontSize(100));
        Add(VirtualKey.F3, VirtualKeyModifiers.None, () => StepSearch(1));
        Add(VirtualKey.F3, VirtualKeyModifiers.Shift, () => StepSearch(-1));
        Add(VirtualKey.G, VirtualKeyModifiers.Control, FocusProgress);
        Add(VirtualKey.D, VirtualKeyModifiers.Control, ToggleBookmark);
        Add(VirtualKey.U, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ToggleReading);
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => Post(new JsonObject { ["type"] = "back" }));
        Add(VirtualKey.Right, VirtualKeyModifiers.Menu, () => Post(new JsonObject { ["type"] = "forward" }));
        AddIf(VirtualKey.Right, VirtualKeyModifiers.Control, () => OffText(() => StepChapter(1)));
        AddIf(VirtualKey.Left, VirtualKeyModifiers.Control, () => OffText(() => StepChapter(-1)));
        AddIf(VirtualKey.Right, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _reading && OffText(() => SkipReading(1)));
        AddIf(VirtualKey.Left, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _reading && OffText(() => SkipReading(-1)));
        Add(VirtualKey.F2, VirtualKeyModifiers.None, () => StepBookmark(1));
        Add(VirtualKey.F2, VirtualKeyModifiers.Shift, () => StepBookmark(-1));
        AddIf(VirtualKey.H, VirtualKeyModifiers.Control, () => _notes?.KeepOffered() == true);
        Add(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => TogglePaneTab("Contents"));
        Add(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => TogglePaneTab("Pages"));
        Add(VirtualKey.H, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => TogglePaneTab("Highlights"));
        Add(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, NextPageTheme);
        AddIf(VirtualKey.L, VirtualKeyModifiers.Control, () => OffText(ToggleFlow));
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // ================================================================ highlights and notes

    private ReaderAnnotations? _notes;
    private Annotation? _pendingReveal;
    /// <summary>A jump to one turns the page; the bar opened for it stays.</summary>
    private bool _barStays;

    public void UseAnnotations(ReaderAnnotations notes)
    {
        _notes = notes;
        notes.Attach(Bar);
        notes.Drawn += _ => SendMarks();
        notes.Removed += _ => SendMarks();
        notes.Reloaded += SendMarks;
        notes.ListChanged += ShowBookmark;
        HighlightsList.CountChanged += n => HighlightsTab.Text = n > 0 ? $"Highlights ({n:N0})" : "Highlights";
        HighlightsList.OpenRequested += a =>
        {
            Reveal(a);
            FocusPages();
        };
        HighlightsList.NoteRequested += a =>
        {
            _barStays = true;
            Reveal(a);
            notes.EditNote(a, null, SurfaceSize);
        };
        HighlightsList.Bind(notes);
    }

    private Size SurfaceSize => new(Surface.ActualWidth, Surface.ActualHeight);

    private const string AnchorTag = "epub1:";
    private const string BookmarkTag = "epubmark1:";
    private string _chapter = "";

    // ---- read aloud ----

    private ReadAloud? _speech;
    private bool _reading;
    private bool _showingReading;
    private bool _spoke;    // something was spoken since reading began
    private int _silent;    // paragraphs in a row with no voice for them

    /// <summary>The button turned on or off, by a click, Space or a screen reader.</summary>
    private void OnReadAloudToggled(object sender, RoutedEventArgs e)
    {
        if (_showingReading) return;
        if (ReadAloudButton.IsChecked == true) StartReading();
        else StopReading();
    }

    private void ToggleReading()
    {
        if (_reading) StopReading();
        else StartReading();
    }

    /// <summary>From the place shown on: the page script hands over a paragraph at a time.</summary>
    private void StartReading()
    {
        if (_current < 0) return;
        if (_speech is null)
        {
            _speech = new ReadAloud();
            _speech.Ended += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (_reading) Post(new JsonObject { ["type"] = "readNext" });
            });
            _speech.Word += (at, length) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_reading) Post(new JsonObject { ["type"] = "readWord", ["at"] = at, ["length"] = length });
            });
        }
        (_spoke, _silent) = (false, 0);
        SpeechBar.IsOpen = false;
        ShowReading(true);
        Post(new JsonObject { ["type"] = "readStart" });
    }

    private void StopReading()
    {
        if (!_reading) return;
        ShowReading(false);
        _speech?.Stop();
        Post(new JsonObject { ["type"] = "readStop" });
    }

    /// <summary>Ctrl+Shift+Right and Left while reading aloud: on to the next paragraph, or back to the one before.</summary>
    private void SkipReading(int step)
    {
        if (!_reading || _speech is null) return;
        _speech.Stop();
        Post(new JsonObject { ["type"] = step > 0 ? "readNext" : "readPrevious" });
    }

    private void ShowReading(bool on)
    {
        _reading = on;
        _showingReading = true;
        ReadAloudButton.IsChecked = on;
        _showingReading = false;
        ToolTipService.SetToolTip(ReadAloudButton, on ? "Stop reading aloud (Ctrl+Shift+U)" : "Read aloud (Ctrl+Shift+U)");
    }

    /// <summary>
    /// A paragraph to speak. One with no voice is passed over; when the first few all have none (a Burmese book), reading
    /// stops and says why rather than racing silently through the book.
    /// </summary>
    private async void OnReadText(string text)
    {
        if (!_reading || _speech is null) return;
        Activity?.Invoke();   // listening is reading: the reading log counts it
        try
        {
            if (await _speech.SpeakAsync(text, AppServices.Settings.ReadAloudRate))
            {
                (_spoke, _silent) = (true, 0);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("read aloud: " + ex.Message);
        }
        if (!_reading) return;
        if (!_spoke && ++_silent >= 3)
        {
            StopReading();
            SpeechBar.IsOpen = true;
            return;
        }
        Post(new JsonObject { ["type"] = "readNext" });
    }

    // ---- bookmarks ----

    private bool _showingBookmark;

    /// <summary>The button turned on or off, by a click, Space or a screen reader: the bookmark follows.</summary>
    private void OnBookmarkToggled(object sender, RoutedEventArgs e)
    {
        if (!_showingBookmark) ToggleBookmark();
    }

    /// <summary>Ctrl+D: the place being read (the reader's location) is bookmarked, or its bookmark taken off.</summary>
    private void ToggleBookmark()
    {
        if (_notes is null || _current < 0 || _cfi.Length == 0) return;
        _notes.ToggleBookmark(_current, BookmarkTag + _cfi, _fraction, _chapter);
        ShowBookmark();
    }

    /// <summary>The button is lit while the place being read has a bookmark.</summary>
    /// <summary>F2 and Shift+F2: the next bookmark after the place being read, or the one before it.</summary>
    private void StepBookmark(int step)
    {
        if (_current >= 0 && _notes?.NextBookmark(_current, step) is { } mark) Reveal(mark);
    }

    private void ShowBookmark()
    {
        var marked = _current >= 0 && _notes?.BookmarkAt(_current) is not null;
        _showingBookmark = true;
        BookmarkButton.IsChecked = marked;
        _showingBookmark = false;
        ToolTipService.SetToolTip(BookmarkButton, marked ? "Take the bookmark off this page (Ctrl+D)" : "Bookmark this page (Ctrl+D)");
    }

    /// <summary>The highlights for the page to draw: where each is and its colour.</summary>
    private JsonArray Marks()
    {
        var marks = new JsonArray();
        foreach (var a in _notes?.Items ?? [])
            if (a.Kind == AnnotationKind.Highlight && a.Anchor.StartsWith(AnchorTag, StringComparison.Ordinal))
                marks.Add(new JsonObject { ["cfi"] = a.Anchor[AnchorTag.Length..], ["color"] = HighlightColors.Hex(a.Color) });
        return marks;
    }

    private void SendMarks() => Post(new JsonObject { ["type"] = "marks", ["marks"] = Marks() });

    /// <summary>Words just selected on the page: the bar offers the colours, a note and copy.</summary>
    private void OnSelection(JsonElement m)
    {
        if (_notes is null) return;
        var cfi = Str(m, "cfi");
        var words = (m.TryGetProperty("text", out _) ? Str(m, "text") : Str(m, "selection")).Trim();
        if (cfi.Length == 0 || words.Length == 0) return;
        var before = Str(m, "before");
        var after = Str(m, "after");
        Definition.Hide();
        _notes.Offer(() =>
        {
            Post(new JsonObject { ["type"] = "deselect" });
            return new Annotation
            {
                Kind = AnnotationKind.Highlight,
                Anchor = AnchorTag + cfi,
                Page = Math.Max(0, _current),
                Position = _fraction,
                Chapter = ChapterText.Text.Trim(),
                Quote = words,
                Before = before,
                After = after,
            };
        }, words, RectOf(m), SurfaceSize);
    }

    /// <summary>A highlight clicked on the page: its colours, note, copy and delete.</summary>
    private void OnMarkClicked(JsonElement m)
    {
        var anchor = AnchorTag + Str(m, "cfi");
        if (_notes?.Items.FirstOrDefault(a => a.Anchor == anchor && a.Kind == AnnotationKind.Highlight) is not { } a) return;
        Definition.Hide();
        _notes.Edit(a, RectOf(m), SurfaceSize);
    }

    /// <summary>A note on the page being read: it keeps the page's place.</summary>
    private void NewPageNote()
    {
        if (_notes is null || _cfi.Length == 0) return;
        var cfi = _cfi;
        _notes.NewPageNote(() => new Annotation
        {
            Kind = AnnotationKind.PageNote,
            Anchor = AnchorTag + cfi,
            Page = Math.Max(0, _current),
            Position = _fraction,
            Chapter = ChapterText.Text.Trim(),
        }, null, SurfaceSize);
    }

    /// <summary>Goes to it (a jump, which Back returns from), outlined for a moment; waits for the book to open.</summary>
    public void Reveal(Annotation a)
    {
        var mark = a.Anchor.StartsWith(BookmarkTag, StringComparison.Ordinal);
        if (!mark && !a.Anchor.StartsWith(AnchorTag, StringComparison.Ordinal)) return;
        if (_current < 0)
        {
            _pendingReveal = a;
            return;
        }
        // A bookmark is a place, not words: the page is turned to, with nothing outlined.
        if (mark) Post(new JsonObject { ["type"] = "goTo", ["href"] = a.Anchor[BookmarkTag.Length..] });
        else Post(new JsonObject { ["type"] = "reveal", ["cfi"] = a.Anchor[AnchorTag.Length..] });
    }

    private bool _choosingTab;

    private void OnPaneTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ShowPaneTab();
        // Picked by hand: the next book opens on it too.
        if (_choosingTab || PaneTabs.SelectedItem?.Tag is not string name) return;
        AppServices.Settings.ReaderPaneTab = name;
        AppServices.Settings.Save();
    }

    /// <summary>A tab picked for the reader, not by them: not remembered.</summary>
    private void ChooseTab(SelectorBarItem tab)
    {
        _choosingTab = true;
        PaneTabs.SelectedItem = tab;
        _choosingTab = false;
    }

    /// <summary>The tab by its name. A book's pages are made by the window, so its way round is the contents.</summary>
    private SelectorBarItem TabNamed(string name) => name == "Highlights" ? HighlightsTab : ContentsTab;

    /// <summary>Ctrl+Shift+C, P and H: the side pane open on that tab, or put away when it is showing it already.</summary>
    private void TogglePaneTab(string name)
    {
        var tab = TabNamed(name);
        if (ContentsPane.Visibility == Visibility.Visible && ReferenceEquals(PaneTabs.SelectedItem, tab))
        {
            SetContentsOpen(false, remember: true);
            return;
        }
        if (Equals(tab.Tag, name)) PaneTabs.SelectedItem = tab;   // remembered, as a click on it is
        else ChooseTab(tab);
        SetContentsOpen(true, remember: true);
    }

    private void ShowPaneTab()
    {
        var highlights = ReferenceEquals(PaneTabs.SelectedItem, HighlightsTab);
        HighlightsList.Visibility = highlights ? Visibility.Visible : Visibility.Collapsed;
        TocList.Visibility = highlights ? Visibility.Collapsed : Visibility.Visible;
        NoTocText.Visibility = !highlights && TocList.ItemsSource is List<TocRow> { Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================ IReaderView

    public ReadingPosition? Position() =>
        _cfi.Length == 0 || _current < 0 ? null : new ReadingPosition(_current, _total, PositionPrefix + _cfi);

    /// <summary>Escape: takes the keyboard from the toolbar back to the page, or puts away the definition.</summary>
    public bool HandleEscape()
    {
        if (_reveal.HasFocus)
        {
            FocusPages();
            return true;
        }
        if (_reading)
        {
            StopReading();
            return true;
        }
        if (_notes?.BarOpen == true)
        {
            _notes.HideBar();
            FocusPages();
            return true;
        }
        if (!Definition.IsOpen) return false;
        Definition.Hide();
        return true;
    }

    public void FocusPages() => Web.Focus(FocusState.Programmatic);

    public void SetToolbar(bool hides, bool fullScreen)
    {
        _reveal.SetHides(hides);
        if (fullScreen == _fullScreen) return;
        _fullScreen = fullScreen;
        SetContentsOpen(!fullScreen && AppServices.Settings.ReaderContentsOpen, remember: false);
    }

    public void FocusToolbar() => _reveal.FocusFirst();

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _reading = false;
        _speech?.Dispose();
        _reveal.Close();
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
