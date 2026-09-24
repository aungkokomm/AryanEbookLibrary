using System.Text.Json;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Keeps personal state (favorites, status, notes, tags...) in a small sidecar file at the root of each
/// library folder, so it travels with the drive to another PC. This is the same idea as CineLibrary's
/// "personal state lives alongside the titles". The SQLite index stays the fast local copy.
/// </summary>
public sealed class StateSyncService
{
    public const string SidecarName = ".aryan-library.json";

    /// <summary>
    /// Highlights and notes on pages, in a file of their own: an older copy of the app rewrites the first file
    /// without them, and they can grow long.
    /// </summary>
    public const string HighlightsName = ".aryan-highlights.json";

    private readonly LibraryRepository _repo;
    private readonly AnnotationStore _annotations;
    private readonly Dictionary<long, Timer> _pending = new();
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public StateSyncService(LibraryRepository repo, AnnotationStore annotations)
    {
        _repo = repo;
        _annotations = annotations;
    }

    private sealed class SidecarFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, BookState> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Each book's annotations by its path under the folder, the deleted ones' markers too.</summary>
    private sealed class HighlightsFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, List<Annotation>> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Debounced write: many quick edits produce one file write.</summary>
    public void Schedule(long folderId)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(folderId, out var old)) old.Dispose();
            _pending[folderId] = new Timer(_ => Flush(folderId), null, 2000, Timeout.Infinite);
        }
    }

    public void FlushAll()
    {
        List<long> ids;
        lock (_gate) ids = _pending.Keys.ToList();
        foreach (var id in ids) Flush(id);
    }

    public void Flush(long folderId)
    {
        lock (_gate)
        {
            if (_pending.Remove(folderId, out var t)) t.Dispose();
        }

        try
        {
            var folder = _repo.GetFolder(folderId);
            if (folder is null) return;
            var root = DriveRegistry.Resolve(folder.DriveId, folder.RelPath);
            if (root is null || !Directory.Exists(root)) return;

            var file = new SidecarFile();
            foreach (var row in _repo.GetStatesForFolder(folderId))
            {
                if (row.State.IsDefault) continue;
                file.Items[SubPath(folder, row.RelPath)] = row.State;
            }
            Write(Path.Combine(root, SidecarName), file, file.Items.Count);

            var marks = new HighlightsFile();
            foreach (var row in _annotations.ForFolder(folderId))
            {
                var sub = SubPath(folder, row.RelPath);
                if (!marks.Items.TryGetValue(sub, out var list)) marks.Items[sub] = list = new();
                list.Add(row.Annotation);
            }
            Write(Path.Combine(root, HighlightsName), marks, marks.Items.Count);
        }
        catch (Exception ex)
        {
            // read-only drive, permission problem, etc. The local index still has the data.
            Log.Write($"Sidecar write failed for folder {folderId}: {ex.Message}");
        }
    }

    /// <summary>Writes the file whole, through a temporary one; not at all while there is nothing to write and no file yet.</summary>
    private static void Write<T>(string target, T content, int count)
    {
        if (count == 0 && !File.Exists(target)) return;
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(content, Options));
        File.Move(tmp, target, overwrite: true);
    }

    /// <summary>Merges the sidecars found on the drive into the local index (newest edit wins).</summary>
    public int Import(LibraryFolder folder)
    {
        var root = DriveRegistry.Resolve(folder.DriveId, folder.RelPath);
        if (root is null) return 0;
        return ImportStates(folder, root) + ImportAnnotations(folder, root);
    }

    private int ImportStates(LibraryFolder folder, string root)
    {
        try
        {
            var path = Path.Combine(root, SidecarName);
            if (!File.Exists(path)) return 0;

            var file = JsonSerializer.Deserialize<SidecarFile>(File.ReadAllText(path));
            if (file is null) return 0;

            var local = _repo.GetAllStates().ToDictionary(s => s.Key, s => s.State.UpdatedUtc);
            var count = 0;

            foreach (var (sub, state) in file.Items)
            {
                var rel = RelPath(folder, sub);
                var key = Book.MakeKey(folder.DriveId, rel);
                if (local.TryGetValue(key, out var localUpdated) && localUpdated >= state.UpdatedUtc) continue;
                _repo.UpsertState(folder.DriveId, rel, key, state);
                _repo.EnsureLists(state.Lists);   // a list made on another computer appears here too
                count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            Log.Write($"Sidecar import failed for folder {folder.Id}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Highlights and notes from the drive: each one new here, or newer than the one here, comes in.</summary>
    private int ImportAnnotations(LibraryFolder folder, string root)
    {
        try
        {
            var path = Path.Combine(root, HighlightsName);
            if (!File.Exists(path)) return 0;
            var file = JsonSerializer.Deserialize<HighlightsFile>(File.ReadAllText(path));
            if (file is null) return 0;

            var local = _annotations.UpdatedById();
            var count = 0;
            foreach (var (sub, list) in file.Items)
            {
                var rel = RelPath(folder, sub);
                count += _annotations.Merge(Book.MakeKey(folder.DriveId, rel), folder.DriveId, rel, list, local);
            }
            if (count > 0) Log.Write($"Sidecar: {count} highlights and notes came in for folder {folder.Id}");
            return count;
        }
        catch (Exception ex)
        {
            Log.Write($"Highlights import failed for folder {folder.Id}: {ex.Message}");
            return 0;
        }
    }

    private static string RelPath(LibraryFolder folder, string sub) =>
        string.IsNullOrEmpty(folder.RelPath) ? sub : Path.Combine(folder.RelPath, sub);

    /// <summary>
    /// Brings the folder's sidecar and the local index level, for a drive that has just been plugged in (or was
    /// already in at start-up): newer edits on the drive come in, then the file is written again when this computer
    /// has edits it lacks. An edit made while the drive was away could not be written then, and before this it
    /// waited on the drive until the next edit in the same folder. Returns how many edits came in.
    /// </summary>
    public int SyncFolder(LibraryFolder folder)
    {
        var imported = Import(folder);
        try
        {
            var root = DriveRegistry.Resolve(folder.DriveId, folder.RelPath);
            if (root is null || !Directory.Exists(root)) return imported;
            var path = Path.Combine(root, SidecarName);
            var onDrive = File.Exists(path)
                ? JsonSerializer.Deserialize<SidecarFile>(File.ReadAllText(path))?.Items ?? new()
                : new Dictionary<string, BookState>();
            var items = new Dictionary<string, BookState>(onDrive, StringComparer.OrdinalIgnoreCase);

            var behind = _repo.GetStatesForFolder(folder.Id).Any(row =>
                items.TryGetValue(SubPath(folder, row.RelPath), out var there)
                    ? row.State.UpdatedUtc > there.UpdatedUtc
                    : !row.State.IsDefault);
            if (behind || AnnotationsBehind(folder, root)) Flush(folder.Id);
        }
        catch (Exception ex)
        {
            Log.Write($"Sidecar sync failed for folder {folder.Id}: {ex.Message}");
        }
        return imported;
    }

    /// <summary>Whether the drive's highlights file lacks an annotation made or changed on this computer.</summary>
    private bool AnnotationsBehind(LibraryFolder folder, string root)
    {
        var local = _annotations.ForFolder(folder.Id);
        if (local.Count == 0) return false;
        var path = Path.Combine(root, HighlightsName);
        var onDrive = File.Exists(path) ? JsonSerializer.Deserialize<HighlightsFile>(File.ReadAllText(path))?.Items : null;
        var there = (onDrive ?? new()).Values.SelectMany(l => l)
            .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.Max(a => a.UpdatedUtc), StringComparer.Ordinal);
        return local.Any(row => !there.TryGetValue(row.Annotation.Id, out var updated) || row.Annotation.UpdatedUtc > updated);
    }

    private static string SubPath(LibraryFolder folder, string bookRelPath)
    {
        if (string.IsNullOrEmpty(folder.RelPath)) return bookRelPath;
        var prefix = folder.RelPath.TrimEnd('\\', '/') + "\\";
        return bookRelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? bookRelPath[prefix.Length..]
            : bookRelPath;
    }
}
