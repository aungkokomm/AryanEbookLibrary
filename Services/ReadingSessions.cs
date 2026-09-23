using System.Globalization;

namespace AryanEbookLibrary.Services;

/// <summary>One sitting with a book in the app's own reader.</summary>
public sealed record ReadingSession(
    long Id, string BookKey, DateTime StartedUtc, DateTime EndedUtc, int Seconds, int Pages, int FirstPage, int LastPage, int PageCount);

/// <summary>Where the reader was in a book: the page, and the reader's own detail (offset, zoom).</summary>
public sealed record ReadingPosition(int Page, int PageCount, string Position);

/// <summary>
/// The reader's record of reading, in the reading_sessions and reading_positions tables. Kept on this computer
/// only: unlike a book's status or rating it is not written to the sidecar beside the books.
/// </summary>
public sealed class ReadingSessions
{
    private readonly Database _db;

    public ReadingSessions(Database db) => _db = db;

    public long Add(ReadingSession s) => _db.Insert("""
        INSERT INTO reading_sessions (book_key, started_utc, ended_utc, seconds, pages, first_page, last_page, page_count)
        VALUES ($k, $s, $e, $sec, $p, $fp, $lp, $pc)
        """,
        ("$k", s.BookKey), ("$s", Stamp(s.StartedUtc)), ("$e", Stamp(s.EndedUtc)), ("$sec", s.Seconds), ("$p", s.Pages),
        ("$fp", s.FirstPage), ("$lp", s.LastPage), ("$pc", s.PageCount));

    public void Update(ReadingSession s) => _db.Exec("""
        UPDATE reading_sessions SET ended_utc=$e, seconds=$sec, pages=$p, last_page=$lp, page_count=$pc WHERE id=$id
        """,
        ("$e", Stamp(s.EndedUtc)), ("$sec", s.Seconds), ("$p", s.Pages), ("$lp", s.LastPage), ("$pc", s.PageCount), ("$id", s.Id));

    /// <summary>Every sitting, oldest first; only those started on or after <paramref name="sinceUtc"/> when given.</summary>
    public List<ReadingSession> All(DateTime? sinceUtc = null) => _db.Query(
        "SELECT id, book_key, started_utc, ended_utc, seconds, pages, first_page, last_page, page_count FROM reading_sessions " +
        (sinceUtc is null ? "" : "WHERE started_utc >= $since ") + "ORDER BY started_utc",
        r => new ReadingSession(r.GetInt64(0), r.GetString(1), Parse(r.GetString(2)), Parse(r.GetString(3)),
            r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)),
        sinceUtc is null ? Array.Empty<(string, object?)>() : new (string, object?)[] { ("$since", Stamp(sinceUtc.Value)) });

    public List<ReadingSession> ForBook(string bookKey) => _db.Query(
        "SELECT id, book_key, started_utc, ended_utc, seconds, pages, first_page, last_page, page_count FROM reading_sessions " +
        "WHERE book_key=$k ORDER BY started_utc",
        r => new ReadingSession(r.GetInt64(0), r.GetString(1), Parse(r.GetString(2)), Parse(r.GetString(3)),
            r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)),
        ("$k", bookKey));

    public ReadingPosition? GetPosition(string bookKey) => _db.Query(
        "SELECT page, page_count, position FROM reading_positions WHERE book_key=$k",
        r => new ReadingPosition(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2)),
        ("$k", bookKey)).FirstOrDefault();

    public void SavePosition(string bookKey, ReadingPosition p) => _db.Exec("""
        INSERT INTO reading_positions (book_key, page, page_count, position, updated_utc) VALUES ($k, $p, $pc, $pos, $u)
        ON CONFLICT(book_key) DO UPDATE SET page=$p, page_count=$pc, position=$pos, updated_utc=$u
        """,
        ("$k", bookKey), ("$p", p.Page), ("$pc", p.PageCount), ("$pos", p.Position), ("$u", Stamp(DateTime.UtcNow)));

    private static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime Parse(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

/// <summary>
/// Time spent reading, worked out from the sittings: by day, this week and year, the streak of days in a row, and
/// how long a book should take to finish at the reader's own pace. A sitting belongs to the local day it started on.
/// </summary>
public static class ReadingTime
{
    /// <summary>Seconds read on each local day.</summary>
    public static Dictionary<DateTime, int> ByDay(IEnumerable<ReadingSession> sessions)
    {
        var days = new Dictionary<DateTime, int>();
        foreach (var s in sessions)
        {
            var day = s.StartedUtc.ToLocalTime().Date;
            days[day] = days.GetValueOrDefault(day) + s.Seconds;
        }
        return days;
    }

    /// <summary>Seconds read from <paramref name="fromDay"/> to <paramref name="toDay"/>, both local days, inclusive.</summary>
    public static int Between(Dictionary<DateTime, int> byDay, DateTime fromDay, DateTime toDay) =>
        byDay.Where(d => d.Key >= fromDay.Date && d.Key <= toDay.Date).Sum(d => d.Value);

    /// <summary>The first day of the week <paramref name="day"/> is in, by the user's own calendar.</summary>
    public static DateTime WeekStart(DateTime day)
    {
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var back = ((int)day.DayOfWeek - (int)first + 7) % 7;
        return day.Date.AddDays(-back);
    }

    /// <summary>
    /// Days in a row with some reading, ending today, or yesterday when nothing has been read yet today (a streak
    /// is not broken by a day that is not over).
    /// </summary>
    public static int Streak(Dictionary<DateTime, int> byDay, DateTime today)
    {
        var day = today.Date;
        if (byDay.GetValueOrDefault(day) <= 0) day = day.AddDays(-1);
        var streak = 0;
        while (byDay.GetValueOrDefault(day) > 0)
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>
    /// Seconds a page takes this reader: from this book's own sittings once there are enough of them (a picture
    /// book and a textbook go at very different speeds), otherwise from all of them. Null when too little is known.
    /// </summary>
    public static double? SecondsPerPage(IReadOnlyCollection<ReadingSession> book, IReadOnlyCollection<ReadingSession> all)
    {
        static double? Pace(IEnumerable<ReadingSession> ss)
        {
            var (seconds, pages) = ss.Aggregate((0L, 0L), (t, s) => (t.Item1 + s.Seconds, t.Item2 + s.Pages));
            return seconds >= 300 && pages >= 5 ? (double)seconds / pages : null;
        }
        return Pace(book) ?? Pace(all);
    }

    /// <summary>"2 h 5 min", "12 min", "under a minute".</summary>
    public static string Format(int seconds)
    {
        if (seconds < 60) return seconds <= 0 ? "0 min" : "under a minute";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>A short form for a narrow tile: "2:05" hours and minutes, "12m" under an hour.</summary>
    public static string Short(int seconds)
    {
        var minutes = seconds / 60;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}:{minutes % 60:00}";
    }
}
