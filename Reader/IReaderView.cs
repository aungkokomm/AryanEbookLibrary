using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// What the reader window needs from a view of one kind of book (PDF, EPUB): where the reader is, when they did
/// something, and the requests only the window can carry out. A "page" is the view's own unit: a PDF page, or an
/// EPUB location of about 1,500 bytes of text, which is close to a printed page.
/// </summary>
public interface IReaderView
{
    /// <summary>The first and last page on screen.</summary>
    event Action<int, int>? PageChanged;

    /// <summary>Input the window cannot see for itself (a web view keeps its own keyboard and pointer).</summary>
    event Action? Activity;

    event Action? FullScreenRequested;

    /// <summary>Escape pressed where the window's own key handling cannot hear it, and the view had no use for it.</summary>
    event Action? EscapeRequested;

    event Action? FinishedRequested;
    event Action? OpenExternallyRequested;

    /// <summary>The toolbar's keyboard button, or F1 where the window's own key handling cannot hear it.</summary>
    event Action? ShortcutsRequested;

    /// <summary>Ctrl+W where the window's own key handling cannot hear it.</summary>
    event Action? CloseRequested;

    int PageCount { get; }
    bool OfferFinish { get; set; }

    Task OpenAsync(string path, ReadingPosition? position);

    /// <summary>Where the reader is, for the book to open there again; null before the book is shown.</summary>
    ReadingPosition? Position();

    void SetTimeLeft(string text);

    /// <summary>Escape from the window: puts away whatever the view has open. True when it did something.</summary>
    bool HandleEscape();

    void FocusPages();

    /// <summary>
    /// The toolbar in its own row, or floating over the page and hiding until it is wanted. Full screen always hides
    /// it, and puts the contents away until full screen ends.
    /// </summary>
    void SetToolbar(bool hides, bool fullScreen);

    /// <summary>Shows the toolbar and moves the keyboard into it, for Alt.</summary>
    void FocusToolbar();

    /// <summary>
    /// The book's highlights and notes: the view draws them on its pages, offers the bar over what is selected and
    /// lists them in its side panel. Given before <see cref="OpenAsync"/>.
    /// </summary>
    void UseAnnotations(ReaderAnnotations notes);

    /// <summary>Goes to a highlight or note and shows where it is; before the book is shown, as soon as it is.</summary>
    void Reveal(Models.Annotation annotation);

    void Close();
}
