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

    private readonly LibraryRepository _repo;
    private readonly Dictionary<long, Timer> _pending = new();
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public StateSyncService(LibraryRepository repo) => _repo = repo;

    private sealed class SidecarFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, BookState> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

            var target = Path.Combine(root, SidecarName);
            if (file.Items.Count == 0 && !File.Exists(target)) return;

            var tmp = target + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            // read-only drive, permission problem, etc. The local index still has the data.
            Log.Write($"Sidecar write failed for folder {folderId}: {ex.Message}");
        }
    }

    /// <summary>Merges a sidecar found on the drive into the local index (newest edit wins).</summary>
    public int Import(LibraryFolder folder)
    {
        try
        {
            var root = DriveRegistry.Resolve(folder.DriveId, folder.RelPath);
            if (root is null) return 0;
            var path = Path.Combine(root, SidecarName);
            if (!File.Exists(path)) return 0;

            var file = JsonSerializer.Deserialize<SidecarFile>(File.ReadAllText(path));
            if (file is null) return 0;

            var local = _repo.GetAllStates().ToDictionary(s => s.Key, s => s.State.UpdatedUtc);
            var count = 0;

            foreach (var (sub, state) in file.Items)
            {
                var rel = string.IsNullOrEmpty(folder.RelPath) ? sub : Path.Combine(folder.RelPath, sub);
                var key = Book.MakeKey(folder.DriveId, rel);
                if (local.TryGetValue(key, out var localUpdated) && localUpdated >= state.UpdatedUtc) continue;
                _repo.UpsertState(folder.DriveId, rel, key, state);
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

    private static string SubPath(LibraryFolder folder, string bookRelPath)
    {
        if (string.IsNullOrEmpty(folder.RelPath)) return bookRelPath;
        var prefix = folder.RelPath.TrimEnd('\\', '/') + "\\";
        return bookRelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? bookRelPath[prefix.Length..]
            : bookRelPath;
    }
}
