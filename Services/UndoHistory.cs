using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Ctrl+Z for what the reader changes on books: a mark, a rating, a favorite, a list, tags, a note, their own details.
/// Each save is recorded as the book's state before and after; the saves made in one go (forty books marked finished
/// at once) are one step. Undoing a step puts back only what it changed, so a page read since is kept. Opening a book
/// and the reader's progress are not recorded (their callers say so).
/// </summary>
public sealed class UndoHistory
{
    public const int Depth = 20;

    private sealed class Step
    {
        public readonly Dictionary<string, (BookState Before, BookState After)> Books = new();
    }

    private readonly List<Step> _steps = new();
    private Step? _open;

    public bool CanUndo => _steps.Count > 0;

    /// <summary>A book was saved, from <paramref name="before"/> to <paramref name="after"/>. True when this began a new step.</summary>
    public bool Saved(string key, BookState before, BookState after)
    {
        if (Changes(before, after).Count == 0) return false;
        var began = _open is null;
        if (_open is null)
        {
            _open = new Step();
            _steps.Add(_open);
            if (_steps.Count > Depth) _steps.RemoveAt(0);
        }
        _open.Books[key] = _open.Books.TryGetValue(key, out var was) ? (was.Before, after) : (before, after);
        return began;
    }

    /// <summary>What was saved so far is one step; the next save begins another.</summary>
    public void EndStep() => _open = null;

    public void Clear()
    {
        _steps.Clear();
        _open = null;
    }

    /// <summary>
    /// Puts back the last step on the books it changed, found by their key (a book no longer in the library is
    /// skipped), and returns them with a line saying what was undone. Null when there is nothing to undo.
    /// </summary>
    public (List<Book> Books, string Text)? Undo(Func<string, Book?> find)
    {
        _open = null;
        if (_steps.Count == 0) return null;
        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);

        var books = new List<Book>();
        var what = new List<string>();
        foreach (var (key, (before, after)) in step.Books)
        {
            foreach (var change in Changes(before, after))
                if (!what.Contains(change)) what.Add(change);
            if (find(key) is not { } book) continue;
            Revert(book, before, after);
            books.Add(book);
        }
        var target = books.Count == 1 ? books[0].Title : $"{books.Count:N0} books";
        return (books, $"Undone ({string.Join(", ", what)}): {target}");
    }

    /// <summary>Each field the step changed goes back to what it was; the others keep what they have now.</summary>
    private static void Revert(Book b, BookState before, BookState after)
    {
        if (before.IsFavorite != after.IsFavorite) b.IsFavorite = before.IsFavorite;
        if (before.Status != after.Status) b.Status = before.Status;
        if (before.FinishedUtc != after.FinishedUtc) b.FinishedUtc = before.FinishedUtc;
        if (before.Progress != after.Progress) b.Progress = before.Progress;
        if (before.Rating != after.Rating) b.Rating = before.Rating;
        if (before.Notes != after.Notes) b.Notes = before.Notes;
        if (before.UserTags != after.UserTags) b.UserTags = before.UserTags;
        if (!before.Lists.SequenceEqual(after.Lists)) b.Lists = before.Lists.ToList();
        if (before.LastOpenedUtc != after.LastOpenedUtc) b.LastOpenedUtc = before.LastOpenedUtc;
        if (before.CustomTitle != after.CustomTitle || before.CustomAuthor != after.CustomAuthor || before.CustomSeries != after.CustomSeries)
            b.SetCustomDetails(
                before.CustomTitle != after.CustomTitle ? before.CustomTitle : b.CustomTitle,
                before.CustomAuthor != after.CustomAuthor ? before.CustomAuthor : b.CustomAuthor,
                before.CustomSeries != after.CustomSeries ? before.CustomSeries : b.CustomSeries);
    }

    /// <summary>What changed, in words for the status bar. A new reading status brings its own progress and date with it.</summary>
    private static List<string> Changes(BookState before, BookState after)
    {
        var changes = new List<string>();
        if (before.Status != after.Status) changes.Add("reading status");
        else if (before.Progress != after.Progress || before.FinishedUtc != after.FinishedUtc) changes.Add("progress");
        if (before.Rating != after.Rating) changes.Add("rating");
        if (before.IsFavorite != after.IsFavorite) changes.Add("favorite");
        if (!before.Lists.SequenceEqual(after.Lists)) changes.Add("lists");
        if (before.UserTags != after.UserTags) changes.Add("tags");
        if (before.Notes != after.Notes) changes.Add("note");
        if (before.CustomTitle != after.CustomTitle || before.CustomAuthor != after.CustomAuthor || before.CustomSeries != after.CustomSeries)
            changes.Add("details");
        if (changes.Count == 0 && before.LastOpenedUtc != after.LastOpenedUtc) changes.Add("last opened");
        return changes;
    }
}
