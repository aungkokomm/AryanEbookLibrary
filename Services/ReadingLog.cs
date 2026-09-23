using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>One year of reading: books finished per month, newest first in the list.</summary>
public sealed class ReadingYear
{
    public int Year { get; init; }
    public int[] Months { get; } = new int[12];
    public List<Book> Finished { get; } = new();
    public int Total => Finished.Count;
}

/// <summary>A book to read next, and why it is here ("Reading, 40%", "Next in Amar Chitra Katha, #12").</summary>
public sealed record UpNextItem(Book Book, string Why);

/// <summary>
/// The reading log, worked out from each book's status and finish date. A finished book with no date (marked
/// finished "earlier", or before dates were kept) counts for all time but not for any month, the way
/// Goodreads leaves an undated read out of its yearly challenge.
/// </summary>
public static class ReadingLog
{
    /// <summary>When the book was finished, in the user's own time zone (a book finished late at night
    /// belongs to that day, not to the next one in UTC).</summary>
    public static DateTime? FinishedLocal(Book b) =>
        b.Status == ReadStatus.Finished && b.FinishedUtc is { } utc ? utc.ToLocalTime() : null;   // Unspecified counts as UTC

    /// <summary>
    /// Changes the book's status. A book that becomes finished gets <paramref name="finishedUtc"/> as its date
    /// (null: finished some time ago, date not known); one that was already finished keeps its date. Unread
    /// forgets the date and the progress.
    /// </summary>
    public static void SetStatus(Book book, ReadStatus status, DateTime? finishedUtc)
    {
        var wasFinished = book.Status == ReadStatus.Finished;
        book.Status = status;
        if (status == ReadStatus.Finished)
        {
            if (!wasFinished) book.FinishedUtc = finishedUtc;
            book.Progress = 100;
        }
        else if (status == ReadStatus.Unread)
        {
            book.Progress = 0;
            book.FinishedUtc = null;
        }
    }

    public static ReadingYear Year(IEnumerable<Book> books, int year)
    {
        var result = new ReadingYear { Year = year };
        foreach (var (book, when) in books.Select(b => (b, FinishedLocal(b)))
                     .Where(x => x.Item2?.Year == year)
                     .OrderByDescending(x => x.Item2))
        {
            result.Months[when!.Value.Month - 1]++;
            result.Finished.Add(book);
        }
        return result;
    }

    /// <summary>Every year with a finished book in it, and this year even when it has none yet. Newest first.</summary>
    public static List<int> Years(IEnumerable<Book> books, int thisYear) =>
        books.Select(FinishedLocal).Where(d => d is not null).Select(d => d!.Value.Year)
            .Append(thisYear).Distinct().OrderByDescending(y => y).ToList();

    public static int AllTime(IEnumerable<Book> books) => books.Count(b => b.Status == ReadStatus.Finished);

    public static int Undated(IEnumerable<Book> books) =>
        books.Count(b => b.Status == ReadStatus.Finished && b.FinishedUtc is null);

    /// <summary>
    /// What to read next: the books being read (last opened first), then, for each series with a finished
    /// book and nothing being read, the first unread volume after the furthest one finished.
    /// </summary>
    public static List<UpNextItem> UpNext(IEnumerable<Book> books, int max = 12)
    {
        var all = books.ToList();
        var list = all.Where(b => b.Status == ReadStatus.Reading)
            .OrderByDescending(b => b.LastOpenedUtc ?? b.StateUpdatedUtc)
            .Select(b => new UpNextItem(b, b.Progress > 0 ? $"Reading, {b.Progress}%" : "Reading"))
            .ToList();

        var next = new List<(UpNextItem Item, DateTime Last)>();
        foreach (var series in all.Where(b => b.Series.Length > 0 && b.SeriesIndex is not null)
                     .GroupBy(b => b.Series, StringComparer.CurrentCultureIgnoreCase))
        {
            if (series.Any(b => b.Status == ReadStatus.Reading)) continue;   // that book is already up next
            var done = series.Where(b => b.Status == ReadStatus.Finished).ToList();
            if (done.Count == 0) continue;

            var furthest = done.Max(b => b.SeriesIndex!.Value);
            var book = series.Where(b => b.Status == ReadStatus.Unread && b.SeriesIndex > furthest)
                .OrderBy(b => b.SeriesIndex).ThenBy(b => b.IsAvailable ? 0 : 1)
                .FirstOrDefault();
            if (book is null) continue;

            var last = done.Max(b => b.FinishedUtc ?? DateTime.MinValue);
            next.Add((new UpNextItem(book, $"Next in {book.Series}, #{book.SeriesIndex:0.##}"), last));
        }

        list.AddRange(next.OrderByDescending(x => x.Last).Select(x => x.Item));
        return list.Take(max).ToList();
    }
}
