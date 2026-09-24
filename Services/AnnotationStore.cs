using System.Globalization;
using AryanEbookLibrary.Models;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Services;

/// <summary>An annotation with the book it belongs to.</summary>
public sealed record AnnotationRow(string BookKey, string DriveId, string RelPath, Annotation Annotation);

/// <summary>
/// The annotations table: highlights, clipped areas and notes on pages. Reading and writing only; what an edit means
/// for the files beside the books is <see cref="AnnotationService"/>'s business.
/// </summary>
public sealed class AnnotationStore
{
    private const string Columns =
        "a.book_key, a.drive_id, a.rel_path, a.id, a.kind, a.anchor, a.page, a.position, a.chapter, a.quote, a.before_text, " +
        "a.after_text, a.color, a.note, a.created_utc, a.updated_utc, a.deleted";

    private readonly Database _db;

    public AnnotationStore(Database db) => _db = db;

    /// <summary>A book's annotations, in the order they come in the book.</summary>
    public List<Annotation> ForBook(string bookKey) => _db.Query(
        $"SELECT {Columns} FROM annotations a WHERE a.book_key=$k AND a.deleted=0 ORDER BY a.position, a.page, a.created_utc",
        r => Read(r).Annotation, ("$k", bookKey));

    /// <summary>Every annotation that has not been deleted, or every one, the deleted ones' markers too.</summary>
    public List<AnnotationRow> All(bool withDeleted = false) =>
        _db.Query($"SELECT {Columns} FROM annotations a" + (withDeleted ? "" : " WHERE a.deleted=0"), Read);

    /// <summary>
    /// The annotations of the books in one library folder, the deleted ones' markers too: what the file beside the
    /// books holds.
    /// </summary>
    public List<AnnotationRow> ForFolder(long folderId) => _db.Query(
        $"SELECT {Columns} FROM annotations a JOIN books b ON b.state_key = a.book_key WHERE b.folder_id = $f",
        Read, ("$f", folderId));

    /// <summary>When each annotation was last changed, the deleted ones too, for merging in another copy.</summary>
    public Dictionary<string, DateTime> UpdatedById() =>
        _db.Query("SELECT id, updated_utc FROM annotations", r => (Id: r.GetString(0), Updated: Parse(r.GetString(1))))
            .ToDictionary(x => x.Id, x => x.Updated, StringComparer.Ordinal);

    public void Upsert(string bookKey, string driveId, string relPath, Annotation a) => _db.Exec("""
        INSERT INTO annotations (id, book_key, drive_id, rel_path, kind, anchor, page, position, chapter, quote, before_text,
                                 after_text, color, note, created_utc, updated_utc, deleted)
        VALUES ($id, $k, $d, $p, $kind, $anchor, $page, $pos, $chapter, $quote, $before, $after, $color, $note, $c, $u, $del)
        ON CONFLICT(id) DO UPDATE SET book_key=$k, drive_id=$d, rel_path=$p, kind=$kind, anchor=$anchor, page=$page,
            position=$pos, chapter=$chapter, quote=$quote, before_text=$before, after_text=$after, color=$color, note=$note,
            created_utc=$c, updated_utc=$u, deleted=$del
        """,
        ("$id", a.Id), ("$k", bookKey), ("$d", driveId), ("$p", relPath), ("$kind", (int)a.Kind), ("$anchor", a.Anchor),
        ("$page", a.Page), ("$pos", a.Position), ("$chapter", a.Chapter), ("$quote", a.Quote), ("$before", a.Before),
        ("$after", a.After), ("$color", HighlightColors.Clamp(a.Color)), ("$note", a.Note), ("$c", Stamp(a.CreatedUtc)),
        ("$u", Stamp(a.UpdatedUtc)), ("$del", a.Deleted ? 1 : 0));

    /// <summary>
    /// Brings in annotations from another copy (the file beside the books, a backup): each one that is new here, or
    /// newer than the one here, is kept. Returns how many came in.
    /// </summary>
    public int Merge(string bookKey, string driveId, string relPath, IEnumerable<Annotation> incoming, Dictionary<string, DateTime> local)
    {
        var count = 0;
        _db.Transaction(() =>
        {
            foreach (var a in incoming)
            {
                if (string.IsNullOrEmpty(a.Id) || string.IsNullOrEmpty(a.Anchor)) continue;
                if (local.TryGetValue(a.Id, out var here) && here >= a.UpdatedUtc) continue;
                Upsert(bookKey, driveId, relPath, a);
                local[a.Id] = a.UpdatedUtc;
                count++;
            }
        });
        return count;
    }

    private static AnnotationRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        new Annotation
        {
            Id = r.GetString(3),
            Kind = (AnnotationKind)r.GetInt32(4),
            Anchor = r.GetString(5),
            Page = r.GetInt32(6),
            Position = r.GetDouble(7),
            Chapter = Str(r, 8),
            Quote = Str(r, 9),
            Before = Str(r, 10),
            After = Str(r, 11),
            Color = r.GetInt32(12),
            Note = Str(r, 13),
            CreatedUtc = Parse(r.GetString(14)),
            UpdatedUtc = Parse(r.GetString(15)),
            Deleted = r.GetInt32(16) != 0,
        });

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

    private static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime Parse(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
