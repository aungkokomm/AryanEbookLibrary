using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Highlights, clipped areas and notes on pages as the app uses them. A save goes to the index at once and to the
/// file beside the book's folder a moment later, like a book's personal state, and every open window hears of it.
/// </summary>
public sealed class AnnotationService
{
    private readonly AnnotationStore _store;

    public AnnotationService(AnnotationStore store) => _store = store;

    /// <summary>One book's annotations changed: its key.</summary>
    public event Action<string>? Changed;

    public List<Annotation> ForBook(Book book) => _store.ForBook(book.StateKey);

    public List<AnnotationRow> All() => _store.All();

    public void Save(Book book, Annotation a)
    {
        a.UpdatedUtc = DateTime.UtcNow;
        _store.Upsert(book.StateKey, book.DriveId, book.RelPath, a);
        AppServices.Sync.Schedule(book.FolderId);
        Changed?.Invoke(book.StateKey);
    }

    /// <summary>Deletes it, leaving the marker that keeps other copies from bringing it back, and its picture goes.</summary>
    public void Delete(Book book, Annotation a)
    {
        a.Deleted = true;
        Save(book, a);
        ClipStore.Delete(a.Id);
    }

    /// <summary>Deletes one whose book is not in the library now, found by the key it was kept under.</summary>
    public void Delete(AnnotationRow row)
    {
        row.Annotation.Deleted = true;
        row.Annotation.UpdatedUtc = DateTime.UtcNow;
        _store.Upsert(row.BookKey, row.DriveId, row.RelPath, row.Annotation);
        ClipStore.Delete(row.Annotation.Id);
        Changed?.Invoke(row.BookKey);
    }

    /// <summary>For the pane: highlights and clips, and the notes written on them or on pages.</summary>
    public (int Highlights, int Notes) Counts()
    {
        var all = _store.All();
        return (all.Count(r => r.Annotation.IsMark),
                all.Count(r => r.Annotation.HasNote || r.Annotation.Kind == AnnotationKind.PageNote));
    }

    /// <summary>Annotations came in from the drives or a backup: every window reads them again.</summary>
    public void RaiseChanged(string bookKey = "") => Changed?.Invoke(bookKey);
}

/// <summary>
/// Pictures of clipped areas and of highlighted PDF lines, one PNG per annotation in AryanLibrary-Data\Clips. They
/// can always be made again from the book, so they stay on this computer; the reader makes a missing one when it
/// opens the book.
/// </summary>
public static class ClipStore
{
    /// <summary>A picture was made (its annotation's id): the cards showing it read it again.</summary>
    public static event Action<string>? Saved;

    public static string PathFor(string id) => System.IO.Path.Combine(AppPaths.Clips, id + ".png");

    public static bool Exists(string id) => File.Exists(PathFor(id));

    public static void Save(string id, byte[] png)
    {
        try
        {
            var target = PathFor(id);
            File.WriteAllBytes(target + ".tmp", png);
            File.Move(target + ".tmp", target, overwrite: true);
            Saved?.Invoke(id);
        }
        catch (Exception ex)
        {
            Log.Write($"clips: saving {id} failed: {ex.Message}");
        }
    }

    public static void Delete(string id)
    {
        try
        {
            File.Delete(PathFor(id));
        }
        catch (Exception ex)
        {
            Log.Write($"clips: deleting {id} failed: {ex.Message}");
        }
    }
}
