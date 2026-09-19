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
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}
