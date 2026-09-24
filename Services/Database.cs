using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Thin thread-safe wrapper around one SQLite connection (WAL mode).
/// All access is serialized by a lock, which is plenty for a personal library index.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Database(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        _conn = new SqliteConnection(cs);
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Make(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public long Insert(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using (var cmd = Make(sql, args)) cmd.ExecuteNonQuery();
            using var idCmd = _conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(idCmd.ExecuteScalar());
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Make(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Make(sql, args);
            var v = cmd.ExecuteScalar();
            if (v is null || v is DBNull) return default;
            return (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }
    }

    /// <summary>Runs <paramref name="work"/> inside BEGIN/COMMIT while holding the DB lock.</summary>
    public void Transaction(Action work)
    {
        lock (_gate)
        {
            Exec("BEGIN");
            try
            {
                work();
                Exec("COMMIT");
            }
            catch
            {
                Exec("ROLLBACK");
                throw;
            }
        }
    }

    private SqliteCommand Make(string sql, (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Migrate()
    {
        var version = Scalar<long>("PRAGMA user_version;");
        if (version < 1)
        {
            Exec("""
                CREATE TABLE drives (
                    id            TEXT PRIMARY KEY,
                    label         TEXT NOT NULL,
                    last_root     TEXT,
                    last_seen_utc TEXT
                );
                """);
            Exec("""
                CREATE TABLE folders (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    drive_id TEXT NOT NULL,
                    rel_path TEXT NOT NULL,
                    UNIQUE (drive_id, rel_path)
                );
                """);
            Exec("""
                CREATE TABLE books (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    folder_id      INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                    drive_id       TEXT NOT NULL,
                    rel_path       TEXT NOT NULL,
                    state_key      TEXT NOT NULL,
                    format         INTEGER NOT NULL,
                    title          TEXT,
                    author         TEXT,
                    series         TEXT,
                    series_index   REAL,
                    publisher      TEXT,
                    year           INTEGER,
                    language       TEXT,
                    description    TEXT,
                    isbn           TEXT,
                    subjects       TEXT,
                    cover_file     TEXT,
                    file_size      INTEGER,
                    modified_ticks INTEGER,
                    added_utc      TEXT,
                    UNIQUE (drive_id, rel_path)
                );
                """);
            Exec("CREATE INDEX idx_books_folder ON books(folder_id);");
            Exec("CREATE INDEX idx_books_state_key ON books(state_key);");
            Exec("""
                CREATE TABLE book_state (
                    key              TEXT PRIMARY KEY,
                    drive_id         TEXT NOT NULL,
                    rel_path         TEXT NOT NULL,
                    is_favorite      INTEGER NOT NULL DEFAULT 0,
                    status           INTEGER NOT NULL DEFAULT 0,
                    rating           INTEGER NOT NULL DEFAULT 0,
                    progress         INTEGER NOT NULL DEFAULT 0,
                    notes            TEXT,
                    user_tags        TEXT,
                    last_opened_utc  TEXT,
                    finished_utc     TEXT,
                    updated_utc      TEXT
                );
                """);
            Exec("PRAGMA user_version = 1;");
        }
        if (version < 2)
        {
            // Books that vanish from a connected folder are flagged, not deleted (CineLibrary's
            // mark-missing-then-clear), so the user can review them on the Drives page.
            Exec("ALTER TABLE books ADD COLUMN is_missing INTEGER NOT NULL DEFAULT 0;");
            Exec("PRAGMA user_version = 2;");
        }
        if (version < 3)
        {
            // The user's own title/author/series. In book_state, not books, so a rescan never overwrites them.
            Exec("ALTER TABLE book_state ADD COLUMN custom_title TEXT;");
            Exec("ALTER TABLE book_state ADD COLUMN custom_author TEXT;");
            Exec("ALTER TABLE book_state ADD COLUMN custom_series TEXT;");
            Exec("PRAGMA user_version = 3;");
        }
        if (version < 4)
        {
            // meta_version: which metadata reader read the book (an older one means read it again).
            // name_fields: which details came from the file name, not the book (bits, see BookMetadata.NameField).
            // cover_weak: the "cover" is a text page (a PDF's first page full of text), so a real cover is better.
            Exec("ALTER TABLE books ADD COLUMN meta_version INTEGER NOT NULL DEFAULT 0;");
            Exec("ALTER TABLE books ADD COLUMN name_fields INTEGER NOT NULL DEFAULT 0;");
            Exec("ALTER TABLE books ADD COLUMN cover_weak INTEGER NOT NULL DEFAULT 0;");
            // What Open Library says about a book, kept apart from the file's own details so a rescan never
            // loses it. Keyed like book_state (drive + path), so it follows the book, not the row.
            Exec("""
                CREATE TABLE book_online (
                    key          TEXT PRIMARY KEY,
                    status       TEXT NOT NULL,              -- found | suggested | none | error
                    how          TEXT,                       -- isbn | match | picked
                    source_key   TEXT,                       -- Open Library work or edition key
                    isbn         TEXT,
                    title        TEXT,
                    author       TEXT,
                    publisher    TEXT,
                    year         INTEGER,
                    language     TEXT,
                    description  TEXT,
                    subjects     TEXT,
                    cover_id     INTEGER,                    -- Open Library cover id, fetched when used
                    cover_file   TEXT,
                    use_cover    INTEGER NOT NULL DEFAULT 0,
                    tries        INTEGER NOT NULL DEFAULT 0,
                    updated_utc  TEXT NOT NULL
                );
                """);
            Exec("PRAGMA user_version = 4;");
        }
        if (version < 5)
        {
            // More than one place can know about a book (Open Library, Wikidata, Wikipedia), so the table
            // holds one row per book PER SOURCE and the display picks the best value per field.
            Exec("""
                CREATE TABLE book_online_new (
                    key          TEXT NOT NULL,
                    source       TEXT NOT NULL,              -- openlibrary | wikidata | wikipedia
                    status       TEXT NOT NULL,              -- found | suggested | none | error
                    how          TEXT,                       -- isbn | match | picked
                    source_key   TEXT,
                    isbn         TEXT,
                    title        TEXT,
                    author       TEXT,
                    publisher    TEXT,
                    year         INTEGER,
                    series       TEXT,
                    series_index REAL,
                    description  TEXT,
                    subjects     TEXT,
                    page_title   TEXT,                       -- the Wikipedia article, when there is one
                    cover_id     INTEGER,
                    cover_url    TEXT,
                    cover_file   TEXT,
                    use_cover    INTEGER NOT NULL DEFAULT 0,
                    tries        INTEGER NOT NULL DEFAULT 0,
                    updated_utc  TEXT NOT NULL,
                    PRIMARY KEY (key, source)
                );
                """);
            Exec("""
                INSERT INTO book_online_new (key, source, status, how, source_key, isbn, title, author, publisher,
                                             year, description, subjects, cover_id, cover_file, use_cover, tries, updated_utc)
                SELECT key, 'openlibrary', status, how, source_key, isbn, title, author, publisher,
                       year, description, subjects, cover_id, cover_file, use_cover, tries, updated_utc
                FROM book_online;
                """);
            Exec("DROP TABLE book_online;");
            Exec("ALTER TABLE book_online_new RENAME TO book_online;");
            // One author looked up once: their Wikidata id and their catalogue, which many books share.
            Exec("""
                CREATE TABLE author_online (
                    name_key    TEXT PRIMARY KEY,
                    name        TEXT NOT NULL,
                    qid         TEXT,
                    status      TEXT NOT NULL,               -- found | none | error
                    works_json  TEXT,
                    tries       INTEGER NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL
                );
                """);
            Exec("PRAGMA user_version = 5;");
        }
        if (version < 6)
        {
            // What the app's own reader records. Keyed like book_state (drive + path), so it follows the book.
            // One row per sitting: when, for how long (seconds actually spent reading, not the time the window
            // was open), and which pages. Only on this computer; the sidecars do not carry it.
            Exec("""
                CREATE TABLE reading_sessions (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    book_key     TEXT NOT NULL,
                    started_utc  TEXT NOT NULL,
                    ended_utc    TEXT NOT NULL,
                    seconds      INTEGER NOT NULL DEFAULT 0,
                    pages        INTEGER NOT NULL DEFAULT 0,      -- different pages looked at
                    first_page   INTEGER NOT NULL DEFAULT 0,
                    last_page    INTEGER NOT NULL DEFAULT 0,
                    page_count   INTEGER NOT NULL DEFAULT 0
                );
                """);
            Exec("CREATE INDEX idx_sessions_book ON reading_sessions(book_key);");
            Exec("CREATE INDEX idx_sessions_started ON reading_sessions(started_utc);");
            // Where the reader was in each book, so it opens there again.
            Exec("""
                CREATE TABLE reading_positions (
                    book_key     TEXT PRIMARY KEY,
                    page         INTEGER NOT NULL,
                    page_count   INTEGER NOT NULL,
                    position     TEXT,                            -- the reader's own detail (offset, zoom)
                    updated_utc  TEXT NOT NULL
                );
                """);
            Exec("PRAGMA user_version = 6;");
        }
        if (version < 7)
        {
            // My lists: which lists a book is on lives with its personal state (names, one per line), so it goes
            // wherever the state goes. The lists themselves are named here, so an empty list still exists.
            Exec("ALTER TABLE book_state ADD COLUMN lists TEXT;");
            Exec("""
                CREATE TABLE user_lists (
                    name         TEXT PRIMARY KEY COLLATE NOCASE,
                    created_utc  TEXT NOT NULL,
                    sort         INTEGER NOT NULL DEFAULT 0
                );
                """);
            Exec("PRAGMA user_version = 7;");
        }
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}
