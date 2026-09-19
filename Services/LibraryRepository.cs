using System.Globalization;
using AryanEbookLibrary.Models;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Services;

public sealed record ExistingBook(long Id, long Size, long ModifiedTicks);

/// <summary>All SQL lives here (CineLibrary keeps its SQLite queries in one service layer too).</summary>
public sealed class LibraryRepository
{
    private readonly Database _db;
    public LibraryRepository(Database db) => _db = db;

    // ------------------------------------------------------------ drives

    public List<DriveRecord> GetDrives() => _db.Query(
        "SELECT id, label, last_root, last_seen_utc FROM drives ORDER BY label COLLATE NOCASE",
        r => new DriveRecord
        {
            Id = r.GetString(0),
            Label = r.GetString(1),
            LastRoot = Str(r, 2),
            LastSeenUtc = Dt(r, 3)
        });

    public void UpsertDrive(string id, string defaultLabel, string root)
    {
        _db.Exec("""
            INSERT INTO drives (id, label, last_root, last_seen_utc) VALUES ($id, $label, $root, $seen)
            ON CONFLICT(id) DO UPDATE SET last_root = excluded.last_root, last_seen_utc = excluded.last_seen_utc;
            """,
            ("$id", id), ("$label", defaultLabel), ("$root", root), ("$seen", Iso(DateTime.UtcNow)));
    }

    public void TouchDrive(string id, string root) =>
        _db.Exec("UPDATE drives SET last_root=$root, last_seen_utc=$seen WHERE id=$id",
            ("$id", id), ("$root", root), ("$seen", Iso(DateTime.UtcNow)));

    public void RenameDrive(string id, string label) =>
        _db.Exec("UPDATE drives SET label=$label WHERE id=$id", ("$id", id), ("$label", label.Trim()));

    // ------------------------------------------------------------ folders

    public List<LibraryFolder> GetFolders() => _db.Query(
        "SELECT id, drive_id, rel_path FROM folders ORDER BY drive_id, rel_path",
        r => new LibraryFolder { Id = r.GetInt64(0), DriveId = r.GetString(1), RelPath = r.GetString(2) });

    public LibraryFolder? GetFolder(long id) => _db.Query(
        "SELECT id, drive_id, rel_path FROM folders WHERE id=$id",
        r => new LibraryFolder { Id = r.GetInt64(0), DriveId = r.GetString(1), RelPath = r.GetString(2) },
        ("$id", id)).FirstOrDefault();

    public LibraryFolder AddFolder(string driveId, string relPath)
    {
        _db.Exec("INSERT OR IGNORE INTO folders (drive_id, rel_path) VALUES ($d, $p)", ("$d", driveId), ("$p", relPath));
        return _db.Query(
            "SELECT id, drive_id, rel_path FROM folders WHERE drive_id=$d AND rel_path=$p",
            r => new LibraryFolder { Id = r.GetInt64(0), DriveId = r.GetString(1), RelPath = r.GetString(2) },
            ("$d", driveId), ("$p", relPath)).First();
    }

    /// <summary>Removes the folder and its indexed books. Personal state rows are kept on purpose.</summary>
    public void RemoveFolder(long id) => _db.Exec("DELETE FROM folders WHERE id=$id", ("$id", id));

    public Dictionary<long, int> CountBooksByFolder() =>
        _db.Query("SELECT folder_id, COUNT(*) FROM books GROUP BY folder_id",
            r => (r.GetInt64(0), r.GetInt32(1))).ToDictionary(x => x.Item1, x => x.Item2);

    // ------------------------------------------------------------ books

    private const string BookColumns = """
        b.id, b.folder_id, b.drive_id, b.rel_path, b.format, b.title, b.author, b.series, b.series_index,
        b.publisher, b.year, b.language, b.description, b.isbn, b.subjects, b.cover_file, b.file_size,
        b.modified_ticks, b.added_utc,
        s.is_favorite, s.status, s.rating, s.progress, s.notes, s.user_tags, s.last_opened_utc, s.finished_utc, s.updated_utc
        """;

    public List<Book> LoadAll()
    {
        var labels = GetDrives().ToDictionary(d => d.Id, d => d.Label, StringComparer.OrdinalIgnoreCase);

        return _db.Query(
            $"SELECT {BookColumns} FROM books b LEFT JOIN book_state s ON s.key = b.state_key",
            r =>
            {
                var b = new Book
                {
                    Id = r.GetInt64(0),
                    FolderId = r.GetInt64(1),
                    DriveId = r.GetString(2),
                    RelPath = r.GetString(3),
                    Format = (BookFormat)r.GetInt32(4),
                    Title = Str(r, 5),
                    Author = Str(r, 6),
                    Series = Str(r, 7),
                    SeriesIndex = r.IsDBNull(8) ? null : r.GetDouble(8),
                    Publisher = Str(r, 9),
                    Year = r.IsDBNull(10) ? null : r.GetInt32(10),
                    Language = Str(r, 11),
                    Description = Str(r, 12),
                    Isbn = Str(r, 13),
                    Subjects = Str(r, 14),
                    CoverFile = r.IsDBNull(15) ? null : r.GetString(15),
                    FileSize = r.IsDBNull(16) ? 0 : r.GetInt64(16),
                    ModifiedTicks = r.IsDBNull(17) ? 0 : r.GetInt64(17),
                    AddedUtc = Dt(r, 18) ?? DateTime.UtcNow
                };
                b.DriveLabel = labels.TryGetValue(b.DriveId, out var l) ? l : b.DriveId;

                if (!r.IsDBNull(19))
                {
                    b.ApplyState(new BookState
                    {
                        IsFavorite = r.GetInt32(19) != 0,
                        Status = (ReadStatus)r.GetInt32(20),
                        Rating = r.GetInt32(21),
                        Progress = r.GetInt32(22),
                        Notes = Str(r, 23),
                        UserTags = Str(r, 24),
                        LastOpenedUtc = Dt(r, 25),
                        FinishedUtc = Dt(r, 26),
                        UpdatedUtc = Dt(r, 27) ?? DateTime.MinValue
                    });
                }
                return b;
            });
    }

    public Dictionary<string, ExistingBook> GetExistingForFolder(long folderId) =>
        _db.Query("SELECT rel_path, id, file_size, modified_ticks FROM books WHERE folder_id=$f",
                r => (Rel: r.GetString(0), Row: new ExistingBook(r.GetInt64(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3))),
                ("$f", folderId))
            .GroupBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Row, StringComparer.OrdinalIgnoreCase);

    public void UpsertBook(Book b)
    {
        _db.Exec("""
            INSERT INTO books (folder_id, drive_id, rel_path, state_key, format, title, author, series, series_index,
                               publisher, year, language, description, isbn, subjects, cover_file, file_size,
                               modified_ticks, added_utc)
            VALUES ($folder, $drive, $rel, $key, $format, $title, $author, $series, $sindex,
                    $publisher, $year, $lang, $desc, $isbn, $subjects, $cover, $size, $ticks, $added)
            ON CONFLICT(drive_id, rel_path) DO UPDATE SET
                folder_id = excluded.folder_id,
                state_key = excluded.state_key,
                format = excluded.format,
                title = excluded.title,
                author = excluded.author,
                series = excluded.series,
                series_index = excluded.series_index,
                publisher = excluded.publisher,
                year = excluded.year,
                language = excluded.language,
                description = excluded.description,
                isbn = excluded.isbn,
                subjects = excluded.subjects,
                cover_file = COALESCE(excluded.cover_file, books.cover_file),
                file_size = excluded.file_size,
                modified_ticks = excluded.modified_ticks;
            """,
            ("$folder", b.FolderId), ("$drive", b.DriveId), ("$rel", b.RelPath), ("$key", b.StateKey),
            ("$format", (int)b.Format), ("$title", b.Title), ("$author", b.Author), ("$series", b.Series),
            ("$sindex", b.SeriesIndex), ("$publisher", b.Publisher), ("$year", b.Year), ("$lang", b.Language),
            ("$desc", b.Description), ("$isbn", b.Isbn), ("$subjects", b.Subjects), ("$cover", b.CoverFile),
            ("$size", b.FileSize), ("$ticks", b.ModifiedTicks), ("$added", Iso(b.AddedUtc)));
    }

    public void DeleteBooks(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        _db.Transaction(() =>
        {
            foreach (var id in list) _db.Exec("DELETE FROM books WHERE id=$id", ("$id", id));
        });
    }

    // ------------------------------------------------------------ personal state

    public void UpsertState(string driveId, string relPath, string key, BookState s)
    {
        _db.Exec("""
            INSERT INTO book_state (key, drive_id, rel_path, is_favorite, status, rating, progress, notes, user_tags,
                                    last_opened_utc, finished_utc, updated_utc)
            VALUES ($key, $drive, $rel, $fav, $status, $rating, $progress, $notes, $tags, $opened, $finished, $updated)
            ON CONFLICT(key) DO UPDATE SET
                is_favorite = excluded.is_favorite,
                status = excluded.status,
                rating = excluded.rating,
                progress = excluded.progress,
                notes = excluded.notes,
                user_tags = excluded.user_tags,
                last_opened_utc = excluded.last_opened_utc,
                finished_utc = excluded.finished_utc,
                updated_utc = excluded.updated_utc;
            """,
            ("$key", key), ("$drive", driveId), ("$rel", relPath),
            ("$fav", s.IsFavorite ? 1 : 0), ("$status", (int)s.Status), ("$rating", s.Rating),
            ("$progress", s.Progress), ("$notes", s.Notes), ("$tags", s.UserTags),
            ("$opened", s.LastOpenedUtc is { } o ? Iso(o) : null),
            ("$finished", s.FinishedUtc is { } f ? Iso(f) : null),
            ("$updated", Iso(s.UpdatedUtc)));
    }

    public sealed record StateRow(string DriveId, string RelPath, string Key, BookState State);

    public List<StateRow> GetAllStates() => _db.Query(
        "SELECT drive_id, rel_path, key, is_favorite, status, rating, progress, notes, user_tags, last_opened_utc, finished_utc, updated_utc FROM book_state",
        ReadStateRow);

    public List<StateRow> GetStatesForFolder(long folderId) => _db.Query(
        """
        SELECT s.drive_id, s.rel_path, s.key, s.is_favorite, s.status, s.rating, s.progress, s.notes, s.user_tags,
               s.last_opened_utc, s.finished_utc, s.updated_utc
        FROM book_state s JOIN books b ON b.state_key = s.key
        WHERE b.folder_id = $f
        """,
        ReadStateRow, ("$f", folderId));

    public Dictionary<string, string> GetTitlesByKey() =>
        _db.Query("SELECT state_key, title FROM books", r => (Key: r.GetString(0), Title: Str(r, 1)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Title);

    private static StateRow ReadStateRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        new BookState
        {
            IsFavorite = r.GetInt32(3) != 0,
            Status = (ReadStatus)r.GetInt32(4),
            Rating = r.GetInt32(5),
            Progress = r.GetInt32(6),
            Notes = Str(r, 7),
            UserTags = Str(r, 8),
            LastOpenedUtc = Dt(r, 9),
            FinishedUtc = Dt(r, 10),
            UpdatedUtc = Dt(r, 11) ?? DateTime.MinValue
        });

    // ------------------------------------------------------------ helpers

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? Dt(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;
    }
}
