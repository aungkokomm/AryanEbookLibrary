using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>A book the last scan could not find where it used to be.</summary>
public sealed record MissingEntry(long Id, string DriveId, string DriveLabel, string RelPath, string Title,
    string Author, long FileSize, BookFormat Format)
{
    public string FormatLabel => FormatHelper.Label(Format);
    public string SizeText => FileSize <= 0 ? "" : $"{FileSize / 1048576.0:0.0} MB";
    public string Where => $"{DriveLabel}  ·  {RelPath}";
    public bool IsConnected => DriveRegistry.IsOnline(DriveId);
}

/// <summary>A missing book and the file that looks like the same book, somewhere else on the drive.</summary>
public sealed class MovedBook
{
    public MissingEntry Book { get; init; } = null!;
    public string NewRelPath { get; init; } = "";
    public long FolderId { get; init; }
    public string Why { get; init; } = "";
    public string Line => $"{Book.Title}  ->  {NewRelPath}";
}

/// <summary>
/// Books that are in the catalogue but not on the drive any more, and the ones that only moved. A file
/// that moved keeps everything the user gave it (favorite, notes, tags, their own title), because the
/// catalogue row and the personal state row are carried over to the new path.
/// </summary>
public static class MissingBooks
{
    public static List<MissingEntry> All(LibraryRepository repo)
    {
        var labels = repo.GetDrives().ToDictionary(d => d.Id, d => d.Label, StringComparer.OrdinalIgnoreCase);
        return repo.GetMissing()
            .Select(m => m with { DriveLabel = labels.TryGetValue(m.DriveId, out var l) ? l : m.DriveId })
            .OrderBy(m => m.DriveLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Where each missing book seems to be now: another book's file on the same drive (a scan after the
    /// move indexed it there), or a file under a tracked folder that no book claims. The same file name
    /// and size is certain; the name alone or the size alone is taken only when exactly one file fits.
    /// </summary>
    public static List<MovedBook> FindMoved(LibraryRepository repo, IReadOnlyList<MissingEntry> missing,
        CancellationToken ct = default)
    {
        var moved = new List<MovedBook>();
        if (missing.Count == 0) return moved;

        var known = repo.GetAllRelPaths()
            .Select(p => p.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var drive in missing.Select(m => m.DriveId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var root = DriveRegistry.GetRoot(drive);
            if (root is null) continue;

            // Where the same book could be now: a file in the catalogue at another path (the usual case,
            // because a scan after the move indexed it there), or a file no book claims yet.
            var loose = repo.GetPlaced(drive)
                .Select(p => (Rel: p.RelPath, Size: p.FileSize, FolderId: p.FolderId, Name: Path.GetFileName(p.RelPath)))
                .ToList();

            foreach (var folder in repo.GetFolders().Where(f => f.DriveId == drive))
            {
                var start = folder.RelPath.Length == 0 ? root : Path.Combine(root, folder.RelPath);
                foreach (var file in Walk(start, ct))
                {
                    var rel = Path.GetRelativePath(root, file.FullName);
                    if (known.Contains(rel.ToLowerInvariant())) continue;
                    loose.Add((rel, file.Length, folder.Id, file.Name));
                }
            }
            if (loose.Count == 0) continue;

            foreach (var book in missing.Where(m => m.DriveId.Equals(drive, StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(book.RelPath);
                var sameNameAndSize = loose.Where(f => Same(f.Name, name) && f.Size == book.FileSize).ToList();
                var sameName = loose.Where(f => Same(f.Name, name)).ToList();
                var sameSize = loose.Where(f => f.Size == book.FileSize && book.FileSize > 0 &&
                                                Same(Path.GetExtension(f.Name), Path.GetExtension(name))).ToList();

                var (found, why) =
                    sameNameAndSize.Count == 1 ? (sameNameAndSize[0], "same name and size") :
                    sameName.Count == 1 ? (sameName[0], "same name") :
                    sameSize.Count == 1 ? (sameSize[0], "same size") :
                    (default, "");
                if (why.Length == 0) continue;

                moved.Add(new MovedBook { Book = book, NewRelPath = found.Rel, FolderId = found.FolderId, Why = why });
            }
        }
        return moved;
    }

    /// <summary>Points the catalogue row and everything the user gave the book at its new path.</summary>
    public static void Relink(LibraryRepository repo, MovedBook moved) =>
        repo.MoveBook(moved.Book.Id, moved.Book.DriveId, moved.Book.RelPath, moved.NewRelPath, moved.FolderId);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The scanner's defensive walk, in small: a folder that cannot be read is skipped, not fatal.</summary>
    private static IEnumerable<FileInfo> Walk(string start, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileInfo[] files;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir)) stack.Push(sub);
                files = new DirectoryInfo(dir).GetFiles().Where(f => FormatHelper.IsSupported(f.Name)).ToArray();
            }
            catch (Exception ex)
            {
                Log.Write($"Looking for moved books, skipped {dir}: {ex.Message}");
                continue;
            }
            foreach (var f in files) yield return f;
        }
    }
}
