using System.Globalization;
using System.Text;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace AryanEbookLibrary.Views;

/// <summary>What the page shows: every highlight, or every note; and one book's only, when a book's details ask.</summary>
public sealed record AnnotationsView(bool Notes, string BookKey = "");

/// <summary>The cards of one book, under its cover and name, when the page is ordered by book.</summary>
public sealed class AnnotationGroup : List<AnnotationItem>
{
    private BitmapImage? _cover;

    public AnnotationGroup(Book? book, string title, IEnumerable<AnnotationItem> items, string what) : base(items)
    {
        Book = book;
        Title = title;
        var count = Count == 1 ? "1 " + what : $"{Count:N0} {what}s";
        Line = book is null ? count : book.DisplayAuthor + "  " + (char)0x00B7 + "  " + count;
    }

    public Book? Book { get; }
    public string Title { get; }
    public string Line { get; }

    public ImageSource? Cover
    {
        get
        {
            if (Book?.CoverPath is not { } path || !File.Exists(path)) return null;
            return _cover ??= new BitmapImage(new Uri(path)) { DecodePixelWidth = 64 };
        }
    }
}

/// <summary>
/// Highlights, or My Notes: every highlight, clipped picture and note from every book, as cards to search, sort and
/// go back to. A click opens the book at it; right-click edits its note or colour, copies or deletes it. What is
/// shown can be saved as Markdown or CSV.
/// </summary>
public sealed partial class AnnotationsPage : Page
{
    private AnnotationsView _view = new(false);
    private List<AnnotationItem> _all = new();
    private List<AnnotationItem> _shown = new();
    /// <summary>Annotations whose book is not in the library now, by id: they can still be copied and deleted.</summary>
    private readonly Dictionary<string, AnnotationRow> _orphans = new();
    private string _bookKey = "";
    private readonly HashSet<int> _colors = new();
    private bool _filling;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reloadTimer;

    public AnnotationsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Disabled;
        for (var i = 1; i <= HighlightColors.Count; i++) ColorChips.Children.Add(ColorChip(i));
        SortBox.SelectedIndex = 0;
    }

    /// <summary>Whether this is My Notes, and whose.</summary>
    public AnnotationsView View => _view;

    private bool NotesMode => _view.Notes;
    private string What => NotesMode ? "note" : "highlight";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _view = e.Parameter as AnnotationsView ?? new AnnotationsView(false);
        _bookKey = _view.BookKey;
        TitleText.Text = NotesMode ? "My Notes" : "Highlights";
        ColorRule.Visibility = ColorChips.Visibility = NotesMode ? Visibility.Collapsed : Visibility.Visible;
        EmptyIcon.Glyph = ((char)(NotesMode ? 0xE70B : 0xE7E6)).ToString();
        AppServices.Annotations.Changed += OnAnnotationsChanged;
        ClipStore.Saved += OnClipSaved;
        AppServices.Library.StateChanged += OnStateChanged;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.Annotations.Changed -= OnAnnotationsChanged;
        ClipStore.Saved -= OnClipSaved;
        AppServices.Library.StateChanged -= OnStateChanged;
        _reloadTimer?.Stop();
    }

    /// <summary>Shows one book's only (a book's details "See all"), or all again with an empty key.</summary>
    public void ShowBook(string bookKey)
    {
        _bookKey = bookKey;
        FillBooks();
        Apply();
    }

    private void OnAnnotationsChanged(string key) => ReloadSoon();
    private void OnClipSaved(string id) => ReloadSoon();
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (NotesMode) ReloadSoon();   // a book's own note was written or cleared
    }

    /// <summary>Saves come in bursts (a sync of a folder, the pictures made as a book opens): read them once after.</summary>
    private void ReloadSoon()
    {
        if (_reloadTimer is null)
        {
            _reloadTimer = DispatcherQueue.CreateTimer();
            _reloadTimer.Interval = TimeSpan.FromMilliseconds(250);
            _reloadTimer.IsRepeating = false;
            _reloadTimer.Tick += (_, _) => Load();
        }
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    // ---- reading them ----

    private void Load()
    {
        var books = new Dictionary<string, Book>();
        foreach (var b in AppServices.Library.AllBooks) books.TryAdd(b.StateKey, b);

        _orphans.Clear();
        var items = new List<AnnotationItem>();
        foreach (var row in AppServices.Annotations.All())
        {
            var a = row.Annotation;
            var wanted = NotesMode ? a.HasNote || a.Kind == AnnotationKind.PageNote : a.Kind != AnnotationKind.PageNote;
            if (!wanted) continue;
            var book = books.GetValueOrDefault(row.BookKey);
            if (book is null) _orphans[a.Id] = row;
            items.Add(new AnnotationItem(a, book, Path.GetFileNameWithoutExtension(row.RelPath)));
        }
        if (NotesMode)
            items.AddRange(AppServices.Library.AllBooks.Where(b => b.HasNote).Select(AnnotationItem.ForBookNote));
        _all = items;
        FillBooks();
        Apply();
    }

    /// <summary>"All books", then each book with any, by title, with how many.</summary>
    private void FillBooks()
    {
        _filling = true;
        BookBox.Items.Clear();
        BookBox.Items.Add(new ComboBoxItem { Content = "All books", Tag = "" });
        var selected = 0;
        foreach (var g in _all.GroupBy(KeyOf).OrderBy(g => g.First().BookTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            BookBox.Items.Add(new ComboBoxItem { Content = $"{g.First().BookTitle} ({g.Count():N0})", Tag = g.Key });
            if (g.Key == _bookKey) selected = BookBox.Items.Count - 1;
        }
        if (selected == 0) _bookKey = "";
        BookBox.SelectedIndex = selected;
        _filling = false;
    }

    private string KeyOf(AnnotationItem item) =>
        item.Book?.StateKey ?? (_orphans.TryGetValue(item.Annotation.Id, out var row) ? row.BookKey : "");

    // ---- what is shown ----

    private void Apply()
    {
        var search = (SearchBox.Text ?? "").Trim();
        var shown = _all.Where(i =>
            (_bookKey.Length == 0 || KeyOf(i) == _bookKey) &&
            (NotesMode || _colors.Count == 0 || _colors.Contains(i.Annotation.Color)) &&
            (search.Length == 0 || Matches(i, search))).ToList();

        var byBook = SortTag == "book";
        shown = SortTag switch
        {
            "oldest" => shown.OrderBy(i => i.Annotation.CreatedUtc).ToList(),
            "book" => shown.OrderBy(i => i.BookTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(KeyOf)
                           .ThenBy(i => i.IsBookNote ? 0 : 1).ThenBy(i => i.Annotation.Position)
                           .ThenBy(i => i.Annotation.Page).ThenBy(i => i.Annotation.CreatedUtc).ToList(),
            _ => shown.OrderByDescending(i => i.Annotation.CreatedUtc).ToList(),
        };
        foreach (var i in shown) i.ShowsBook = !byBook;
        _shown = shown;

        if (byBook)
        {
            Groups.Source = shown.GroupBy(KeyOf)
                .Select(g => new AnnotationGroup(g.First().Book, g.First().BookTitle, g, What)).ToList();
            CardList.ItemsSource = Groups.View;
        }
        else CardList.ItemsSource = shown;

        var books = shown.Select(KeyOf).Distinct().Count();
        var filtered = shown.Count != _all.Count;
        SummaryText.Text = _all.Count == 0 ? ""
            : filtered ? $"{shown.Count:N0} of {Count(_all.Count, What)}"
            : $"{Count(_all.Count, What)} in {Count(books, "book")}";
        ExportButton.IsEnabled = shown.Count > 0;

        var none = shown.Count == 0;
        CardList.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = none && _all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_all.Count > 0)
        {
            EmptyTitle.Text = "Nothing matches";
            EmptyText.Text = "No " + What + " has these words, or is in this book and colour.";
        }
        else if (NotesMode)
        {
            EmptyTitle.Text = "No notes yet";
            EmptyText.Text = "Write a note on a book in its details, or in Aryan's reader: on a highlight, or on a page (right-click it). Every note shows here.";
        }
        else
        {
            EmptyTitle.Text = "No highlights yet";
            EmptyText.Text = "In Aryan's reader, select words and pick a colour. In a scanned PDF or a comic, drag a box round what you want to keep. Every highlight shows here.";
        }
    }

    private string SortTag => (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "newest";

    private static bool Matches(AnnotationItem i, string search)
    {
        bool Has(string text) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        return Has(i.Annotation.Quote) || Has(i.Annotation.Note) || Has(i.BookTitle) || Has(i.BookAuthor) || Has(i.Annotation.Chapter);
    }

    private static string Count(int n, string what) => n == 1 ? "1 " + what : $"{n:N0} {what}s";

    private ToggleButton ColorChip(int color)
    {
        var chip = new ToggleButton
        {
            Padding = new Thickness(10, 4, 12, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = ((char)0xEA3B).ToString(),
                        FontSize = 12,
                        Foreground = new SolidColorBrush(HighlightColors.Of(color)),
                    },
                    new TextBlock { Text = HighlightColors.Name(color) },
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, HighlightColors.Name(color) + " highlights");
        chip.Click += (_, _) =>
        {
            if (chip.IsChecked == true) _colors.Add(color);
            else _colors.Remove(color);
            Apply();
        };
        return chip;
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Apply();

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) Apply();
    }

    private void OnBookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        _bookKey = (BookBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        Apply();
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _colors.Clear();
        foreach (var chip in ColorChips.Children.OfType<ToggleButton>()) chip.IsChecked = false;
        _bookKey = "";
        FillBooks();
        Apply();
    }

    // ---- a card ----

    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnotationItem item) Open(item);
    }

    /// <summary>Opens the book in Aryan's reader at it; a book's own note opens its details.</summary>
    private void Open(AnnotationItem item)
    {
        var book = item.Book;
        if (book is null)
        {
            AppServices.Library.Tell("That book is not in the library now. Scan its drive to bring it back.");
            return;
        }
        if (item.IsBookNote) BookDetailsWindow.Show(book);
        else OpenInReader(book, item.Annotation);
    }

    /// <summary>Opens the book in Aryan's reader at a highlight or note, whatever Settings says opens it otherwise.</summary>
    internal static void OpenInReader(Book book, Annotation a)
    {
        var path = book.FullPath;
        var problem = path is null ? $"The drive \"{book.DriveLabel}\" is not plugged in."
            : !File.Exists(path) ? "The book's file is not where the last scan found it. Refresh the drive and try again."
            : Reader.ReaderWindow.Open(book, a);
        if (problem is not null) AppServices.Library.Tell(problem);
    }

    private void OnCardContext(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not AnnotationItem item) return;
        var target = (FrameworkElement)e.OriginalSource;
        var menu = BuildMenu(item);
        e.Handled = true;
        if (e.TryGetPosition(target, out var at)) menu.ShowAt(target, new FlyoutShowOptions { Position = at });
        else menu.ShowAt(target);
    }

    private MenuFlyout BuildMenu(AnnotationItem item)
    {
        var a = item.Annotation;
        var book = item.Book;
        var menu = new MenuFlyout();
        MenuFlyoutItem Add(string text, int glyph, Action act)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = ((char)glyph).ToString() } };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }

        if (book is not null)
        {
            if (item.IsBookNote) Add("Open the book's details", 0xE946, () => BookDetailsWindow.Show(book));
            else Add("Open the book here", 0xE8AD, () => Open(item));
            Add(a.HasNote ? "Edit note..." : "Add a note...", 0xE70B, () => _ = EditNoteAsync(item));
        }

        if (book is not null && !item.IsBookNote && a.Kind != AnnotationKind.PageNote)
        {
            var colours = new MenuFlyoutSubItem { Text = "Colour", Icon = new FontIcon { Glyph = ((char)0xE790).ToString() } };
            for (var i = 1; i <= HighlightColors.Count; i++)
            {
                var color = i;
                var choice = new RadioMenuFlyoutItem
                {
                    Text = HighlightColors.Name(color),
                    GroupName = "Colour",
                    IsChecked = a.Color == color,
                    Icon = Reader.HighlightsPanel.ColorDot(color),
                };
                choice.Click += (_, _) =>
                {
                    a.Color = color;
                    AppServices.Annotations.Save(book, a);
                };
                colours.Items.Add(choice);
            }
            menu.Items.Add(colours);
        }

        Add("Copy", 0xE8C8, () => Reader.WordMenu.CopyText(CopyText(item)));

        if (_bookKey.Length == 0 && KeyOf(item) is { Length: > 0 } key)
            Add("Show only this book's", 0xE71C, () => ShowBook(key));
        if (book is not null && !item.IsBookNote)
            Add("Book details", 0xE946, () => BookDetailsWindow.Show(book));

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Delete...", 0xE74D, () => _ = DeleteAsync(item));
        return menu;
    }

    /// <summary>The words, the note and where they come from, for pasting anywhere.</summary>
    private static string CopyText(AnnotationItem item)
    {
        var sb = new StringBuilder();
        if (item.QuoteText.Length > 0) sb.AppendLine(item.QuoteText);
        if (item.NoteText.Length > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(item.NoteText);
        }
        if (sb.Length > 0) sb.AppendLine();
        sb.Append(item.BookTitle);
        if (item.BookAuthor.Length > 0) sb.Append(", ").Append(item.BookAuthor);
        if (!item.IsBookNote && item.Annotation.PageNumber >= 1)
            sb.Append(", page ").Append(item.Annotation.PageNumber.ToString("N0", CultureInfo.CurrentCulture));
        return sb.ToString();
    }

    private async Task EditNoteAsync(AnnotationItem item)
    {
        if (item.Book is not { } book) return;
        var box = new TextBox
        {
            Text = item.IsBookNote ? book.Notes : item.Annotation.Note,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
            Width = 440,
            PlaceholderText = "Your note",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var header = new TextBlock
        {
            Text = item.QuoteText.Length > 0 ? item.QuoteText : item.BookTitle,
            MaxLines = 3,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 440,
            Style = (Style)Application.Current.Resources["PageHintStyle"],
        };
        var dialog = new ContentDialog
        {
            Title = item.IsBookNote ? "Note on the book" : item.Annotation.HasNote ? "Edit note" : "Add a note",
            Content = new StackPanel { Spacing = 10, Children = { header, box } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

        var note = (box.Text ?? "").Trim();
        if (item.IsBookNote)
        {
            if (note == book.Notes.Trim()) return;
            book.Notes = note;
            AppServices.Library.SaveState(book);
            return;
        }
        if (note == item.Annotation.Note.Trim()) return;
        // A note on a page with its words wiped goes altogether: an empty note on a page marks nothing.
        if (note.Length == 0 && item.Annotation.Kind == AnnotationKind.PageNote)
        {
            AppServices.Annotations.Delete(book, item.Annotation);
            return;
        }
        item.Annotation.Note = note;
        AppServices.Annotations.Save(book, item.Annotation);
    }

    private async Task DeleteAsync(AnnotationItem item)
    {
        var a = item.Annotation;
        var what = item.IsBookNote || a.Kind == AnnotationKind.PageNote ? "note"
            : a.Kind == AnnotationKind.Area ? "clip" : "highlight";
        var answer = await new ContentDialog
        {
            Title = $"Delete this {what}?",
            Content = item.IsBookNote ? "The note on the book goes. This cannot be undone."
                : a.HasNote && what != "note" ? $"The {what} and the note on it go. This cannot be undone."
                : "This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        }.ShowThemedAsync();
        if (answer != ContentDialogResult.Primary) return;

        if (item.IsBookNote && item.Book is { } owner)
        {
            owner.Notes = "";
            AppServices.Library.SaveState(owner);
        }
        else if (item.Book is { } book) AppServices.Annotations.Delete(book, a);
        else if (_orphans.TryGetValue(a.Id, out var row)) AppServices.Annotations.Delete(row);
    }

    // ---- export ----

    private void OnExportMarkdown(object sender, RoutedEventArgs e) => _ = ExportAsync(markdown: true);
    private void OnExportCsv(object sender, RoutedEventArgs e) => _ = ExportAsync(markdown: false);

    /// <summary>Saves what is shown, in the order shown: Markdown with the pictures in a folder beside it, or CSV.</summary>
    private async Task ExportAsync(bool markdown)
    {
        var rows = _shown.Select(i => i.ToExportRow()).ToList();
        if (rows.Count == 0) return;

        var name = NotesMode ? "My Notes" : "Highlights";
        var only = _bookKey.Length > 0 ? _shown[0].BookTitle : "";
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeName(only.Length > 0 ? $"{only} - {name}" : $"{name} {DateTime.Now:yyyy-MM-dd}"),
        };
        if (markdown) picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        else picker.FileTypeChoices.Add("CSV (comma separated)", new List<string> { ".csv" });
        if (App.MainWindow is not { } owner) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var file = await ListActions.PickAsync(XamlRoot, picker.PickSaveFileAsync);
        if (file is null) return;

        try
        {
            var copied = 0;
            if (markdown)
            {
                var folderName = Path.GetFileNameWithoutExtension(file.Path) + " pictures";
                var pictures = new List<string>();
                var heading = only.Length > 0 ? $"{only}: {name}" : name;
                var text = AnnotationExport.Markdown(rows, heading, folderName, pictures);
                await File.WriteAllTextAsync(file.Path, text, new UTF8Encoding(false));
                if (pictures.Count > 0)
                {
                    var folder = Path.Combine(Path.GetDirectoryName(file.Path)!, folderName);
                    copied = await Task.Run(() =>
                    {
                        Directory.CreateDirectory(folder);
                        var n = 0;
                        foreach (var picture in pictures)
                        {
                            if (!File.Exists(picture)) continue;
                            File.Copy(picture, Path.Combine(folder, Path.GetFileName(picture)), overwrite: true);
                            n++;
                        }
                        return n;
                    });
                }
            }
            // With a BOM, so a spreadsheet reads Burmese and Hindi as they are.
            else await File.WriteAllTextAsync(file.Path, AnnotationExport.Csv(rows), new UTF8Encoding(true));

            var said = $"Saved {Count(rows.Count, What)} as {file.Name}";
            if (copied > 0) said += copied == 1 ? ", with its picture beside it" : $", with {copied:N0} pictures beside it";
            AppServices.Library.Tell(said);
        }
        catch (Exception ex)
        {
            Log.Write($"annotations: export to {file.Path} failed: {ex.Message}");
            await ListActions.Say(XamlRoot, "Could not save", "The file could not be written: " + ex.Message);
        }
    }

    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim();
        return safe.Length > 80 ? safe[..80].Trim() : safe;
    }
}
