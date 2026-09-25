using AryanEbookLibrary.Services;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Tests;

/// <summary>A library at an older layout is backed up whole before it is upgraded; a new or current one is not.</summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aryan-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private string Db => Path.Combine(_dir, "library.db");
    private string Backups => Path.Combine(_dir, "Backups");

    public DatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private static long Scalar(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A library as Aryan 0.17 left it: layout 7, with a drive in it.</summary>
    private void MakeLayout7Library()
    {
        new Database(Db).Dispose();
        SqliteConnection.ClearAllPools();
        Exec(Db, "DROP INDEX idx_annotations_book; DROP TABLE annotations; PRAGMA user_version = 7;");
        Exec(Db, "INSERT INTO drives (id, label) VALUES ('D6412CBB', 'Misc (E:)');");
    }

    [Fact]
    public void A_new_library_gets_the_latest_layout_and_no_backup()
    {
        new Database(Db).Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Equal(Database.LatestVersion, Scalar(Db, "PRAGMA user_version"));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void An_older_library_is_backed_up_as_it_was_then_upgraded()
    {
        MakeLayout7Library();

        new Database(Db).Dispose();
        SqliteConnection.ClearAllPools();

        var backup = Assert.Single(Directory.GetFiles(Backups));
        Assert.StartsWith("library-v7-", Path.GetFileName(backup));
        Assert.Equal(7, Scalar(backup, "PRAGMA user_version"));
        Assert.Equal(0, Scalar(backup, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'annotations'"));
        Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM drives WHERE id = 'D6412CBB'"));

        Assert.Equal(Database.LatestVersion, Scalar(Db, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(Db, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'annotations'"));
        Assert.Equal(1, Scalar(Db, "SELECT COUNT(*) FROM drives WHERE id = 'D6412CBB'"));
    }

    [Fact]
    public void An_upgraded_library_is_not_backed_up_again()
    {
        MakeLayout7Library();
        new Database(Db).Dispose();
        new Database(Db).Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Single(Directory.GetFiles(Backups));
    }
}
