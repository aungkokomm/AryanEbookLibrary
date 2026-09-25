using System.Globalization;
using AryanEbookLibrary.Models;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Services;

public sealed record ExistingBook(long Id, long Size, long ModifiedTicks, int MetaVersion, bool HasCover);

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

    /// <summary>Registers a drive under the name the user chose (Add Drive). No scan happens here.</summary>
    public void AddDrive(string id, string label, string root)
    {
        _db.Exec("""
            INSERT INTO drives (id, label, last_root, last_seen_utc) VALUES ($id, $label, $root, $seen)
            ON CONFLICT(id) DO UPDATE SET label = excluded.label, last_root = excluded.last_root,
                                          last_seen_utc = excluded.last_seen_utc;
            """,
            ("$id", id), ("$label", label.Trim()), ("$root", root), ("$seen", Iso(DateTime.UtcNow)));
    }

    /// <summary>Removes the drive, its folders and their indexed books. Personal state rows are kept on purpose.</summary>
    public void RemoveDrive(string id) => _db.Transaction(() =>
    {
        _db.Exec("DELETE FROM folders WHERE drive_id=$id", ("$id", id));
        _db.Exec("DELETE FROM drives WHERE id=$id", ("$id", id));
    });

    /// <summary>Per drive: books in the catalog and books the last scan could not find.</summary>
    public Dictionary<string, (int Books, int Missing)> CountBooksByDrive() =>
        _db.Query("SELECT drive_id, SUM(is_missing = 0), SUM(is_missing = 1) FROM books GROUP BY drive_id",
                r => (Id: r.GetString(0), Books: r.GetInt32(1), Missing: r.GetInt32(2)))
            .ToDictionary(x => x.Id, x => (x.Books, x.Missing), StringComparer.OrdinalIgnoreCase);

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
        _db.Query("SELECT folder_id, COUNT(*) FROM books WHERE is_missing = 0 GROUP BY folder_id",
            r => (r.GetInt64(0), r.GetInt32(1))).ToDictionary(x => x.Item1, x => x.Item2);

    public sealed record MissingBook(long Id, string Title, string Author, string RelPath);

    public List<MissingBook> GetMissingBooks(string driveId) => _db.Query(
        "SELECT id, title, author, rel_path FROM books WHERE drive_id=$d AND is_missing = 1 ORDER BY title COLLATE NOCASE",
        r => new MissingBook(r.GetInt64(0), Str(r, 1), Str(r, 2), r.GetString(3)),
        ("$d", driveId));

    /// <summary>
    /// End of a completed folder scan: every book of the folder is marked missing, then the ones the
    /// walk saw are cleared again (CineLibrary's mark-missing-then-clear, in one transaction).
    /// Returns how many books of the folder are now missing.
    /// </summary>
    public int MarkMissing(long folderId, string driveId, IEnumerable<string> seenRelPaths)
    {
        var missing = 0;
        _db.Transaction(() =>
        {
            _db.Exec("UPDATE books SET is_missing = 1 WHERE folder_id=$f", ("$f", folderId));
            foreach (var rel in seenRelPaths)
                _db.Exec("UPDATE books SET is_missing = 0 WHERE drive_id=$d AND rel_path=$r", ("$d", driveId), ("$r", rel));
            missing = (int)(_db.Scalar<long>("SELECT COUNT(*) FROM books WHERE folder_id=$f AND is_missing = 1", ("$f", folderId)));
        });
        return missing;
    }

    // ------------------------------------------------------------ books

    private const string BookColumns = """
        b.id, b.folder_id, b.drive_id, b.rel_path, b.format, b.title, b.author, b.series, b.series_index,
        b.publisher, b.year, b.language, b.description, b.isbn, b.subjects, b.cover_file, b.file_size,
        b.modified_ticks, b.added_utc,
        s.is_favorite, s.status, s.rating, s.progress, s.notes, s.user_tags, s.last_opened_utc, s.finished_utc, s.updated_utc,
        s.custom_title, s.custom_author, s.custom_series, s.lists
        """;

    public List<Book> LoadAll()
    {
        var labels = GetDrives().ToDictionary(d => d.Id, d => d.Label, StringComparer.OrdinalIgnoreCase);
        var online = GetAllOnline();

        return _db.Query(
            $"SELECT {BookColumns}, b.name_fields, b.cover_weak FROM books b LEFT JOIN book_state s ON s.key = b.state_key WHERE b.is_missing = 0",
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
                    AddedUtc = Dt(r, 18) ?? DateTime.UtcNow,
                    NameFields = r.GetInt32(32),
                    CoverWeak = r.GetInt32(33) != 0
                };
                b.DriveLabel = labels.TryGetValue(b.DriveId, out var l) ? l : b.DriveId;
                b.KeepFileDetails();
                if (online.TryGetValue(b.StateKey, out var sources)) b.SetOnline(sources);

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
                        UpdatedUtc = Dt(r, 27) ?? DateTime.MinValue,
                        CustomTitle = NullableStr(r, 28),
                        CustomAuthor = NullableStr(r, 29),
                        CustomSeries = NullableStr(r, 30),
                        Lists = ReadLists(r, 31)
                    });
                }
                return b;
            });
    }

    /// <summary>Every book the last scan could not find, on any drive.</summary>
    public List<MissingEntry> GetMissing() => _db.Query(
        "SELECT id, drive_id, rel_path, title, author, file_size, format, folder_id FROM books WHERE is_missing = 1",
        r => new MissingEntry(r.GetInt64(0), r.GetString(1), "", r.GetString(2), Str(r, 3), Str(r, 4),
            r.IsDBNull(5) ? 0 : r.GetInt64(5), (BookFormat)r.GetInt32(6), r.GetInt64(7)));

    public sealed record FreshBook(long Id, string DriveId, string RelPath, long FileSize, long FolderId);

    /// <summary>The newest catalogue row's id: a scan's new books all get higher ones.</summary>
    public long LastBookId() => _db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM books");

    /// <summary>
    /// Books on hand that carry nothing of the user's yet: no favorite, status, notes, tags, lists or own title, no
    /// highlight, no reading history. Only such a book may give way to a missing book that moved there, so nothing the
    /// user did is ever replaced.
    /// </summary>
    public List<FreshBook> GetFreshBooks()
    {
        var personal = GetAllStates().Where(s => !s.State.IsDefault).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        personal.UnionWith(_db.Query("""
            SELECT book_key FROM annotations UNION SELECT book_key FROM reading_positions
            UNION SELECT book_key FROM reading_sessions
            """, r => r.GetString(0)));
        return _db.Query("SELECT id, drive_id, rel_path, file_size, folder_id, state_key FROM books WHERE is_missing = 0",
                r => (Key: r.GetString(5), Book: new FreshBook(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? 0 : r.GetInt64(3), r.GetInt64(4))))
            .Where(x => !personal.Contains(x.Key))
            .Select(x => x.Book)
            .ToList();
    }

    public sealed record PlacedFile(string RelPath, long FileSize, long FolderId);

    /// <summary>Where this drive's books are now, for matching a missing book to the file that moved.</summary>
    public List<PlacedFile> GetPlaced(string driveId) => _db.Query(
        "SELECT rel_path, file_size, folder_id FROM books WHERE drive_id=$d AND is_missing = 0",
        r => new PlacedFile(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt64(1), r.GetInt64(2)),
        ("$d", driveId));

    /// <summary>
    /// The same book at a new path, on the same drive or another (<paramref name="newDriveId"/>): the catalogue row
    /// moves and so does the personal state row, so a file that was only moved keeps its favorite, notes, tags and the
    /// user's own title, its reading history, highlights and online details.
    /// </summary>
    public void MoveBook(long id, string driveId, string oldRelPath, string newRelPath, long folderId, string? newDriveId = null)
    {
        newDriveId ??= driveId;
        var oldKey = Book.MakeKey(driveId, oldRelPath);
        var newKey = Book.MakeKey(newDriveId, newRelPath);
        _db.Transaction(() =>
        {
            // A row may already sit at the new path (it was scanned there before): the moved book wins.
            _db.Exec("DELETE FROM books WHERE drive_id=$d AND rel_path=$new AND id<>$id",
                ("$d", newDriveId), ("$new", newRelPath), ("$id", id));
            _db.Exec("UPDATE books SET drive_id=$d, rel_path=$new, folder_id=$folder, state_key=$newkey, is_missing=0 WHERE id=$id",
                ("$d", newDriveId), ("$new", newRelPath), ("$folder", folderId), ("$newkey", newKey), ("$id", id));
            // The old state row is the one the user built up, so it takes the new key.
            _db.Exec("DELETE FROM book_state WHERE key=$newkey AND EXISTS (SELECT 1 FROM book_state WHERE key=$oldkey)",
                ("$newkey", newKey), ("$oldkey", oldKey));
            _db.Exec("UPDATE book_state SET key=$newkey, drive_id=$d, rel_path=$new WHERE key=$oldkey",
                ("$newkey", newKey), ("$d", newDriveId), ("$new", newRelPath), ("$oldkey", oldKey));
            // What was found online for it, source by source; the new path's own findings stay for the other sources.
            _db.Exec("DELETE FROM book_online WHERE key=$newkey AND source IN (SELECT source FROM book_online WHERE key=$oldkey)",
                ("$newkey", newKey), ("$oldkey", oldKey));
            _db.Exec("UPDATE book_online SET key=$newkey WHERE key=$oldkey", ("$newkey", newKey), ("$oldkey", oldKey));
            // Its reading history and place in it go with it.
            _db.Exec("UPDATE reading_sessions SET book_key=$newkey WHERE book_key=$oldkey",
                ("$newkey", newKey), ("$oldkey", oldKey));
            _db.Exec("DELETE FROM reading_positions WHERE book_key=$newkey AND EXISTS (SELECT 1 FROM reading_positions WHERE book_key=$oldkey)",
                ("$newkey", newKey), ("$oldkey", oldKey));
            _db.Exec("UPDATE reading_positions SET book_key=$newkey WHERE book_key=$oldkey",
                ("$newkey", newKey), ("$oldkey", oldKey));
            // And its highlights and notes.
            _db.Exec("UPDATE annotations SET book_key=$newkey, drive_id=$d, rel_path=$new WHERE book_key=$oldkey",
                ("$newkey", newKey), ("$d", newDriveId), ("$new", newRelPath), ("$oldkey", oldKey));
        });
    }

    public Dictionary<string, ExistingBook> GetExistingForFolder(long folderId) =>
        _db.Query("SELECT rel_path, id, file_size, modified_ticks, meta_version, cover_file FROM books WHERE folder_id=$f",
                r => (Rel: r.GetString(0), Row: new ExistingBook(r.GetInt64(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3), r.GetInt32(4), !r.IsDBNull(5))),
                ("$f", folderId))
            .GroupBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Row, StringComparer.OrdinalIgnoreCase);

    public void UpsertBook(Book b)
    {
        _db.Exec("""
            INSERT INTO books (folder_id, drive_id, rel_path, state_key, format, title, author, series, series_index,
                               publisher, year, language, description, isbn, subjects, cover_file, file_size,
                               modified_ticks, added_utc, meta_version, name_fields, cover_weak)
            VALUES ($folder, $drive, $rel, $key, $format, $title, $author, $series, $sindex,
                    $publisher, $year, $lang, $desc, $isbn, $subjects, $cover, $size, $ticks, $added,
                    $mver, $nfields, $cweak)
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
                modified_ticks = excluded.modified_ticks,
                meta_version = excluded.meta_version,
                name_fields = excluded.name_fields,
                cover_weak = excluded.cover_weak,
                is_missing = 0;
            """,
            ("$folder", b.FolderId), ("$drive", b.DriveId), ("$rel", b.RelPath), ("$key", b.StateKey),
            ("$format", (int)b.Format), ("$title", b.Title), ("$author", b.Author), ("$series", b.Series),
            ("$sindex", b.SeriesIndex), ("$publisher", b.Publisher), ("$year", b.Year), ("$lang", b.Language),
            ("$desc", b.Description), ("$isbn", b.Isbn), ("$subjects", b.Subjects), ("$cover", b.CoverFile),
            ("$size", b.FileSize), ("$ticks", b.ModifiedTicks), ("$added", Iso(b.AddedUtc)),
            ("$mver", b.MetaVersion), ("$nfields", b.NameFields), ("$cweak", b.CoverWeak ? 1 : 0));
    }

    // ------------------------------------------------------------ names from file names

    /// <summary>Authors the books themselves declare, plus the ones the user typed in: what the name parser trusts.</summary>
    public List<string> GetDeclaredAuthors() => _db.Query("""
        SELECT author FROM books WHERE is_missing = 0 AND (name_fields & 2) = 0 AND COALESCE(author, '') <> ''
        UNION SELECT custom_author FROM book_state WHERE COALESCE(custom_author, '') <> ''
        """, r => r.GetString(0));

    public List<string> GetAllRelPaths() => _db.Query("SELECT rel_path FROM books WHERE is_missing = 0", r => r.GetString(0));

    public sealed record NameDerivedBook(long Id, string RelPath, int NameFields, string Title, string Author, string Series,
        double? SeriesIndex, int? Year, string Publisher);

    /// <summary>Books with at least one detail taken from the file name, or with no author.</summary>
    public List<NameDerivedBook> GetNameDerivedBooks() => _db.Query(
        "SELECT id, rel_path, name_fields, title, author, series, series_index, year, publisher FROM books WHERE is_missing = 0 AND (name_fields <> 0 OR COALESCE(author, '') = '')",
        r => new NameDerivedBook(r.GetInt64(0), r.GetString(1), r.GetInt32(2), Str(r, 3), Str(r, 4), Str(r, 5),
            r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetInt32(7), Str(r, 8)));

    public void UpdateNameDerived(IEnumerable<NameDerivedBook> changed)
    {
        _db.Transaction(() =>
        {
            foreach (var b in changed)
                _db.Exec("""
                    UPDATE books SET name_fields=$nf, title=$t, author=$a, series=$s, series_index=$si, year=$y, publisher=$p
                    WHERE id=$id
                    """,
                    ("$nf", b.NameFields), ("$t", b.Title), ("$a", b.Author), ("$s", b.Series), ("$si", b.SeriesIndex),
                    ("$y", b.Year), ("$p", b.Publisher), ("$id", b.Id));
        });
    }

    // ------------------------------------------------------------ online details (one row per source)

    private const string OnlineColumns =
        "key, source, status, how, source_key, isbn, title, author, publisher, year, series, series_index, " +
        "description, subjects, page_title, cover_id, cover_url, cover_file, use_cover, tries, updated_utc";

    public Dictionary<string, List<OnlineDetails>> GetAllOnline() =>
        _db.Query($"SELECT {OnlineColumns} FROM book_online", r => (Key: r.GetString(0), Row: new OnlineDetails
            {
                Source = r.GetString(1),
                Status = r.GetString(2),
                How = NullableStr(r, 3),
                SourceKey = NullableStr(r, 4),
                Isbn = NullableStr(r, 5),
                Title = NullableStr(r, 6),
                Author = NullableStr(r, 7),
                Publisher = NullableStr(r, 8),
                Year = r.IsDBNull(9) ? null : r.GetInt32(9),
                Series = NullableStr(r, 10),
                SeriesIndex = r.IsDBNull(11) ? null : r.GetDouble(11),
                Description = NullableStr(r, 12),
                Subjects = NullableStr(r, 13),
                PageTitle = NullableStr(r, 14),
                CoverId = r.IsDBNull(15) ? null : r.GetInt64(15),
                CoverUrl = NullableStr(r, 16),
                CoverFile = NullableStr(r, 17),
                UseCover = r.GetInt32(18) != 0,
                Tries = r.GetInt32(19),
                UpdatedUtc = Dt(r, 20) ?? DateTime.MinValue
            }))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());

    public void UpsertOnline(string key, OnlineDetails o)
    {
        o.UpdatedUtc = DateTime.UtcNow;
        _db.Exec($"""
            INSERT OR REPLACE INTO book_online ({OnlineColumns})
            VALUES ($key, $source, $status, $how, $src, $isbn, $title, $author, $pub, $year, $series, $sindex,
                    $desc, $subjects, $page, $cid, $curl, $cfile, $use, $tries, $updated)
            """,
            ("$key", key), ("$source", o.Source), ("$status", o.Status), ("$how", o.How), ("$src", o.SourceKey),
            ("$isbn", o.Isbn), ("$title", o.Title), ("$author", o.Author), ("$pub", o.Publisher), ("$year", o.Year),
            ("$series", o.Series), ("$sindex", o.SeriesIndex), ("$desc", o.Description), ("$subjects", o.Subjects),
            ("$page", o.PageTitle), ("$cid", o.CoverId), ("$curl", o.CoverUrl), ("$cfile", o.CoverFile),
            ("$use", o.UseCover ? 1 : 0), ("$tries", o.Tries), ("$updated", Iso(o.UpdatedUtc)));
    }

    /// <summary>What Wikidata said about one author: their id and their catalogue, looked up once and kept.</summary>
    public sealed record AuthorOnline(string NameKey, string Name, string? Qid, string Status, string? WorksJson, int Tries, DateTime UpdatedUtc);

    public Dictionary<string, AuthorOnline> GetAllAuthorsOnline() =>
        _db.Query("SELECT name_key, name, qid, status, works_json, tries, updated_utc FROM author_online",
                r => new AuthorOnline(r.GetString(0), r.GetString(1), NullableStr(r, 2), r.GetString(3),
                    NullableStr(r, 4), r.GetInt32(5), Dt(r, 6) ?? DateTime.MinValue))
            .ToDictionary(a => a.NameKey, a => a, StringComparer.OrdinalIgnoreCase);

    public void UpsertAuthorOnline(AuthorOnline a) =>
        _db.Exec("""
            INSERT OR REPLACE INTO author_online (name_key, name, qid, status, works_json, tries, updated_utc)
            VALUES ($key, $name, $qid, $status, $works, $tries, $updated)
            """,
            ("$key", a.NameKey), ("$name", a.Name), ("$qid", a.Qid), ("$status", a.Status),
            ("$works", a.WorksJson), ("$tries", a.Tries), ("$updated", Iso(DateTime.UtcNow)));

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
                                    last_opened_utc, finished_utc, updated_utc, custom_title, custom_author, custom_series, lists)
            VALUES ($key, $drive, $rel, $fav, $status, $rating, $progress, $notes, $tags, $opened, $finished, $updated,
                    $ctitle, $cauthor, $cseries, $lists)
            ON CONFLICT(key) DO UPDATE SET
                is_favorite = excluded.is_favorite,
                status = excluded.status,
                rating = excluded.rating,
                progress = excluded.progress,
                notes = excluded.notes,
                user_tags = excluded.user_tags,
                last_opened_utc = excluded.last_opened_utc,
                finished_utc = excluded.finished_utc,
                updated_utc = excluded.updated_utc,
                custom_title = excluded.custom_title,
                custom_author = excluded.custom_author,
                custom_series = excluded.custom_series,
                lists = excluded.lists;
            """,
            ("$key", key), ("$drive", driveId), ("$rel", relPath),
            ("$fav", s.IsFavorite ? 1 : 0), ("$status", (int)s.Status), ("$rating", s.Rating),
            ("$progress", s.Progress), ("$notes", s.Notes), ("$tags", s.UserTags),
            ("$opened", s.LastOpenedUtc is { } o ? Iso(o) : null),
            ("$finished", s.FinishedUtc is { } f ? Iso(f) : null),
            ("$updated", Iso(s.UpdatedUtc)),
            ("$ctitle", s.CustomTitle), ("$cauthor", s.CustomAuthor), ("$cseries", s.CustomSeries),
            ("$lists", WriteLists(s.Lists)));
    }

    public sealed record StateRow(string DriveId, string RelPath, string Key, BookState State);

    public List<StateRow> GetAllStates() => _db.Query(
        "SELECT drive_id, rel_path, key, is_favorite, status, rating, progress, notes, user_tags, last_opened_utc, finished_utc, updated_utc, custom_title, custom_author, custom_series, lists FROM book_state",
        ReadStateRow);

    public List<StateRow> GetStatesForFolder(long folderId) => _db.Query(
        """
        SELECT s.drive_id, s.rel_path, s.key, s.is_favorite, s.status, s.rating, s.progress, s.notes, s.user_tags,
               s.last_opened_utc, s.finished_utc, s.updated_utc, s.custom_title, s.custom_author, s.custom_series, s.lists
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
            UpdatedUtc = Dt(r, 11) ?? DateTime.MinValue,
            CustomTitle = NullableStr(r, 12),
            CustomAuthor = NullableStr(r, 13),
            CustomSeries = NullableStr(r, 14),
            Lists = ReadLists(r, 15)
        });

    // ------------------------------------------------------------ my lists

    /// <summary>The user's lists, in the order they were made.</summary>
    public List<string> GetLists() =>
        _db.Query("SELECT name FROM user_lists ORDER BY sort, created_utc, name COLLATE NOCASE", r => r.GetString(0));

    /// <summary>Adds a list. False when there is one of that name already (in any case).</summary>
    public bool AddList(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        return _db.Exec("INSERT OR IGNORE INTO user_lists (name, created_utc, sort) VALUES ($n, $t, 0)",
            ("$n", name), ("$t", Iso(DateTime.UtcNow))) > 0;
    }

    /// <summary>Makes sure every one of these lists exists (lists named in a sidecar or a backup).</summary>
    public void EnsureLists(IEnumerable<string> names)
    {
        foreach (var name in names) AddList(name);
    }

    /// <summary>
    /// Renames a list, or (with <paramref name="to"/> null) deletes it, and takes every book off it or onto the
    /// new name. Every book is changed, found or not (a book on an unplugged drive, a file the last scan missed),
    /// and stamped with the time, so an older copy of its state in a sidecar does not bring the old name back.
    /// Returns the changed state rows.
    /// </summary>
    public List<StateRow> RenameList(string from, string? to)
    {
        var changed = new List<StateRow>();
        _db.Transaction(() =>
        {
            if (to is null)
                _db.Exec("DELETE FROM user_lists WHERE name=$n", ("$n", from));
            else
                _db.Exec("UPDATE user_lists SET name=$to WHERE name=$from", ("$to", to.Trim()), ("$from", from));

            var now = DateTime.UtcNow;
            foreach (var row in GetAllStates())
            {
                var at = row.State.Lists.FindIndex(n => string.Equals(n, from, StringComparison.CurrentCultureIgnoreCase));
                if (at < 0) continue;
                if (to is null) row.State.Lists.RemoveAt(at);
                else row.State.Lists[at] = to.Trim();
                row.State.Lists = row.State.Lists.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                row.State.UpdatedUtc = now;
                UpsertState(row.DriveId, row.RelPath, row.Key, row.State);
                changed.Add(row);
            }
        });
        return changed;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>A book's lists are kept one name per line.</summary>
    private const char ListBreak = (char)10;

    private static List<string> ReadLists(SqliteDataReader r, int i) =>
        r.IsDBNull(i)
            ? new List<string>()
            : r.GetString(i).Split(ListBreak, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    private static string? WriteLists(List<string> lists) =>
        lists.Count == 0 ? null : string.Join(ListBreak, lists.Select(n => n.Trim()).Where(n => n.Length > 0));

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private static string? NullableStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? Dt(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        // Values are written by Iso() as UTC "o" strings, so RoundtripKind alone yields Kind=Utc.
        // (Combining it with AdjustToUniversal throws ArgumentException on every call.)
        return DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d
            : null;
    }
}
