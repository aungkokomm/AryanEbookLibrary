using System.Globalization;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AryanEbookLibrary.Views;

/// <summary>
/// One highlight or note as a card shows it, in the reader's side panel and on the Highlights and My Notes pages:
/// its colour, its words or its picture, the note, where it is, and the book. A book's own note (from its details)
/// is shown the same way, with no colour and no place.
/// </summary>
public sealed class AnnotationItem
{
    private BitmapImage? _clip;
    private BitmapImage? _cover;
    private readonly string _fallbackTitle;

    /// <param name="fallbackTitle">The name to show when the book is no longer in the library: its file's.</param>
    public AnnotationItem(Annotation annotation, Book? book, string fallbackTitle = "")
    {
        Annotation = annotation;
        Book = book;
        _fallbackTitle = fallbackTitle;
    }

    /// <summary>A book's own note, the one written in its details.</summary>
    public static AnnotationItem ForBookNote(Book book) => new(new Annotation
    {
        Id = "book:" + book.StateKey,
        Kind = AnnotationKind.PageNote,
        Note = book.Notes.Trim(),
        Page = -1,
        CreatedUtc = book.StateUpdatedUtc,
        UpdatedUtc = book.StateUpdatedUtc,
    }, book) { IsBookNote = true };

    public Annotation Annotation { get; }
    public Book? Book { get; }
    public bool IsBookNote { get; private init; }

    /// <summary>Whether the card names its book: not when the list is already grouped under the book.</summary>
    public bool ShowsBook { get; set; } = true;
    public Visibility BookVisibility => ShowsBook ? Visibility.Visible : Visibility.Collapsed;

    public SolidColorBrush ColorBrush => new(IsBookNote || Annotation.Kind == AnnotationKind.PageNote
        ? Colors.Transparent : HighlightColors.Of(Annotation.Color));

    public string ColorName => IsBookNote || Annotation.Kind == AnnotationKind.PageNote ? "" : HighlightColors.Name(Annotation.Color);

    /// <summary>The words, when they are shown as words.</summary>
    public string QuoteText => Annotation.ShowsPicture ? "" : Annotation.Quote.Trim();
    public Visibility QuoteVisibility => QuoteText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The picture, when it is shown as one and has been made (the reader makes it the first time the book is open).</summary>
    public ImageSource? Clip
    {
        get
        {
            if (!Annotation.ShowsPicture || !ClipStore.Exists(Annotation.Id)) return null;
            return _clip ??= new BitmapImage(new Uri(ClipStore.PathFor(Annotation.Id))) { DecodePixelWidth = 720 };
        }
    }
    public Visibility ClipVisibility => Clip is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A picture is wanted and not made yet: it came from another computer, or was never opened here.</summary>
    public Visibility ClipMissingVisibility =>
        Annotation.ShowsPicture && !ClipStore.Exists(Annotation.Id) ? Visibility.Visible : Visibility.Collapsed;

    public string NoteText => Annotation.Note.Trim();
    public Visibility NoteVisibility => NoteText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Page 12  ·  Chapter One", "Note on page 5", "Note on the book".</summary>
    public string Where
    {
        get
        {
            if (IsBookNote) return "Note on the book";
            var page = Annotation.Kind == AnnotationKind.PageNote ? $"Note on page {Annotation.PageNumber:N0}" : $"Page {Annotation.PageNumber:N0}";
            return Annotation.Chapter.Trim().Length > 0 ? page + Dot + Annotation.Chapter.Trim() : page;
        }
    }

    /// <summary>When it was made: a colour or a note changed later does not move it.</summary>
    public string When => Annotation.CreatedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public string BookTitle => Book?.Title ?? _fallbackTitle;
    public string BookAuthor => Book?.DisplayAuthor ?? "";
    public string BookLine => BookAuthor.Length > 0 ? BookTitle + Dot + BookAuthor : BookTitle;

    /// <summary>"Page 12  ·  Chapter One  ·  24 Sep 2026", under a card in the library.</summary>
    public string Footer => IsBookNote ? When : Where + Dot + When;

    /// <summary>What an export writes for the card: what it shows, words or a picture.</summary>
    public ExportRow ToExportRow() => new(
        BookTitle,
        Book?.Author.Trim() ?? "",
        Book?.RelPath is { } rel ? Path.GetFileName(rel) : _fallbackTitle,
        AnnotationExport.KindOf(Annotation, IsBookNote),
        Annotation.Color,
        QuoteText,
        NoteText,
        IsBookNote ? 0 : Annotation.PageNumber,
        IsBookNote ? "" : Annotation.Chapter.Trim(),
        Annotation.CreatedUtc,
        Annotation.UpdatedUtc,
        Annotation.ShowsPicture && ClipStore.Exists(Annotation.Id) ? ClipStore.PathFor(Annotation.Id) : null);

    public ImageSource? Cover
    {
        get
        {
            if (Book?.CoverPath is not { } path || !File.Exists(path)) return null;
            return _cover ??= new BitmapImage(new Uri(path)) { DecodePixelWidth = 64 };
        }
    }

    /// <summary>What a screen reader says for the card.</summary>
    public string Spoken
    {
        get
        {
            var what = IsBookNote ? "Note" : Annotation.Kind switch
            {
                AnnotationKind.Area => ColorName + " clip",
                AnnotationKind.PageNote => "Note",
                _ => ColorName + " highlight",
            };
            var words = QuoteText.Length > 0 ? ": " + QuoteText : "";
            var note = NoteText.Length > 0 ? ". Note: " + NoteText : "";
            return $"{what}{words}{note}. {Where}. {BookTitle}";
        }
    }

    private static readonly string Dot = "  " + (char)0x00B7 + "  ";
}
