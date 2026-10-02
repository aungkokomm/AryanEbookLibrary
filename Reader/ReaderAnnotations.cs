using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Windows.Foundation;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// A book's highlights and notes while it is open in the reader, and the part of making and changing them that is the
/// same in every reader: the bar and its note, saving, and telling the pages and the side panel. The reader supplies
/// what only it knows: how to make one from what is selected, how to draw one and where one is on screen.
/// </summary>
public sealed class ReaderAnnotations
{
    private readonly Book _book;
    private HighlightBar? _bar;
    private Func<Annotation?>? _make;     // words or an area waiting for a colour or a note
    private string _words = "";
    private Annotation? _editing;         // the one the bar is about
    private bool _offered;                // the bar offers words or a box just marked out, to keep in a colour
    private bool _saving;

    public ReaderAnnotations(Book book)
    {
        _book = book;
        Items = AppServices.Annotations.ForBook(book);
        AppServices.Annotations.Changed += OnChanged;
        ClipStore.Saved += OnClipSaved;
    }

    public Book Book => _book;

    /// <summary>The book's annotations, in the order they come in it.</summary>
    public List<Annotation> Items { get; private set; }

    /// <summary>One was made, or its colour or note changed: the pages draw it again.</summary>
    public event Action<Annotation>? Drawn;
    /// <summary>One was made: the reader keeps a picture of it where it needs one.</summary>
    public event Action<Annotation>? Created;
    public event Action<Annotation>? Removed;
    /// <summary>The library changed them from another window: every one is drawn again.</summary>
    public event Action? Reloaded;
    /// <summary>Anything about the list changed: the side panel reads it again.</summary>
    public event Action? ListChanged;

    public void Attach(HighlightBar bar)
    {
        _bar = bar;
        bar.ColorPicked += Choose;
        bar.NoteRequested += StartNote;
        bar.NoteSaved += OnNoteSaved;
        bar.CopyRequested += OnCopy;
        bar.DeleteRequested += OnDelete;
    }

    public void Close()
    {
        AppServices.Annotations.Changed -= OnChanged;
        ClipStore.Saved -= OnClipSaved;
    }

    /// <summary>A picture of one of this book's was made: the side panel shows it.</summary>
    private void OnClipSaved(string id)
    {
        if (Items.Any(a => a.Id == id)) ListChanged?.Invoke();
    }

    public bool BarOpen => _bar?.IsOpen == true;

    /// <summary>The one the bar is about, if it is about one that exists.</summary>
    public Annotation? Editing => BarOpen ? _editing : null;

    public static int LastColor
    {
        get => HighlightColors.Clamp(AppServices.Settings.LastHighlightColor);
        set
        {
            if (AppServices.Settings.LastHighlightColor == value) return;
            AppServices.Settings.LastHighlightColor = value;
            AppServices.Settings.Save();
        }
    }

    // ---- the bar ----

    /// <summary>Words or an area were just marked out: the bar offers to keep them.</summary>
    public void Offer(Func<Annotation?> make, string words, Rect? box, Size area)
    {
        _make = make;
        _words = words;
        _editing = null;
        _offered = true;
        _bar?.PlaceNear(box, area);
        _bar?.Show(null, canCopy: words.Length > 0, canDelete: false);
    }

    /// <summary>Ctrl+H: what the bar offers (words just selected, a box just drawn) is kept in the last colour used.</summary>
    public bool KeepOffered()
    {
        if (!BarOpen || !_offered || _make is null) return false;
        Choose(LastColor);
        return true;
    }

    /// <summary>A highlight or a clipped area was clicked: its colour, note, copy and delete.</summary>
    public void Edit(Annotation a, Rect? box, Size area)
    {
        _make = null;
        _editing = a;
        _offered = false;
        _bar?.PlaceNear(box, area);
        _bar?.Show(a.IsMark ? a.Color : null, canCopy: a.Quote.Length > 0, canDelete: true, colors: a.IsMark);
    }

    /// <summary>Straight to the note box: the side panel's "Edit note", or a note on a page.</summary>
    public void EditNote(Annotation a, Rect? box, Size area)
    {
        Edit(a, box, area);
        _bar?.EditNote(a.Note);
    }

    /// <summary>A note on the page, no words: kept once there is something written.</summary>
    public void NewPageNote(Func<Annotation?> make, Rect? box, Size area)
    {
        _make = make;
        _words = "";
        _editing = null;
        _offered = false;
        _bar?.PlaceNear(box, area);
        _bar?.Show(null, canCopy: false, canDelete: false, colors: false);
        _bar?.EditNote("");
    }

    public void Place(Rect? box, Size area) => _bar?.PlaceNear(box, area);

    public void HideBar()
    {
        _bar?.Hide();
        _make = null;
        _editing = null;
        _offered = false;
    }

    /// <summary>A colour for what the bar is about: what was offered is kept in it, or the highlight takes it.</summary>
    public void Choose(int color)
    {
        if (_editing is { } a)
        {
            a.Color = color;
            Save(a);
        }
        else if (_make?.Invoke() is { } made)
        {
            made.Color = color;
            Add(made);
        }
        LastColor = color;
        HideBar();
    }

    /// <summary>The note button: on words just selected it makes the highlight first, in the last colour used.</summary>
    public void StartNote()
    {
        if (_editing is null && _make?.Invoke() is { } made)
        {
            made.Color = LastColor;
            Add(made);
            _editing = made;
            _make = null;
        }
        if (_editing is { } a) _bar?.EditNote(a.Note);
    }

    private void OnNoteSaved(string note)
    {
        if (_editing is null && _make?.Invoke() is { } made)
        {
            // A new note on a page: nothing written, nothing kept.
            if (note.Length > 0)
            {
                made.Note = note;
                made.Color = LastColor;
                Add(made);
            }
        }
        else if (_editing is { } a)
        {
            // A note on a page that is emptied is gone; a highlight keeps its colour without one.
            if (a.Kind == AnnotationKind.PageNote && note.Length == 0) Delete(a);
            else if (a.Note != note)
            {
                a.Note = note;
                Save(a);
            }
        }
        HideBar();
    }

    private void OnCopy()
    {
        var text = _editing?.Quote is { Length: > 0 } quote ? quote : _words;
        if (text.Length > 0) WordMenu.CopyText(text);
        HideBar();
    }

    private void OnDelete()
    {
        if (_editing is { } a) Delete(a);
        HideBar();
    }

    // ---- bookmarks ----

    /// <summary>The bookmark on a page (a PDF or comic page, or the EPUB reader's location), if there is one.</summary>
    public Annotation? BookmarkAt(int page) =>
        Items.FirstOrDefault(a => a.Kind == AnnotationKind.Bookmark && a.Page == page);

    /// <summary>F2 and Shift+F2: the nearest bookmark after the page, or before it. Null when there is none that way.</summary>
    public Annotation? NextBookmark(int page, int step)
    {
        var marks = Items.Where(a => a.Kind == AnnotationKind.Bookmark);
        return step > 0 ? marks.Where(a => a.Page > page).MinBy(a => a.Page) : marks.Where(a => a.Page < page).MaxBy(a => a.Page);
    }

    /// <summary>Ctrl+D and the bookmark button: marks the page, or takes its mark off. True when it is marked now.</summary>
    public bool ToggleBookmark(int page, string anchor, double position, string chapter)
    {
        if (BookmarkAt(page) is { } mark)
        {
            Delete(mark);
            return false;
        }
        Add(new Annotation
        {
            Kind = AnnotationKind.Bookmark,
            Anchor = anchor,
            Page = page,
            Position = position,
            Chapter = chapter,
        });
        return true;
    }

    // ---- saving ----

    public void Add(Annotation a)
    {
        Persist(() => AppServices.Annotations.Save(_book, a));
        Items.Add(a);
        Items.Sort((x, y) => x.Position != y.Position ? x.Position.CompareTo(y.Position)
            : x.Page != y.Page ? x.Page.CompareTo(y.Page) : x.CreatedUtc.CompareTo(y.CreatedUtc));
        Drawn?.Invoke(a);
        Created?.Invoke(a);
        ListChanged?.Invoke();
    }

    public void Save(Annotation a)
    {
        Persist(() => AppServices.Annotations.Save(_book, a));
        Drawn?.Invoke(a);
        ListChanged?.Invoke();
    }

    public void Delete(Annotation a)
    {
        Persist(() => AppServices.Annotations.Delete(_book, a));
        Items.Remove(a);
        Removed?.Invoke(a);
        ListChanged?.Invoke();
    }

    private void Persist(Action save)
    {
        _saving = true;
        try
        {
            save();
        }
        catch (Exception ex)
        {
            Log.Write("reader: saving a highlight failed: " + ex.Message);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Changed somewhere else (the Highlights page, a drive coming back): read again and draw again.</summary>
    private void OnChanged(string key)
    {
        if (_saving || (key.Length > 0 && key != _book.StateKey)) return;
        HideBar();
        Items = AppServices.Annotations.ForBook(_book);
        Reloaded?.Invoke();
        ListChanged?.Invoke();
    }
}
