using System.Runtime.InteropServices;
using System.Text;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Tracks which drives are connected right now. Drives are identified by Windows volume serial number,
/// so a drive keeps its identity when its letter changes. Books store paths relative to the drive root.
/// </summary>
public static class DriveRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _online = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, StringBuilder? volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, uint fileSystemNameSize);

    public static bool IsOnline(string driveId)
    {
        lock (Gate) return _online.ContainsKey(driveId);
    }

    public static string? GetRoot(string driveId)
    {
        lock (Gate) return _online.TryGetValue(driveId, out var r) ? r : null;
    }

    public static string? Resolve(string driveId, string relPath)
    {
        var root = GetRoot(driveId);
        if (root is null) return null;
        return string.IsNullOrEmpty(relPath) ? root : Path.Combine(root, relPath);
    }

    /// <summary>Re-detects connected drives. Returns true if anything changed. Safe to call from a background thread.</summary>
    public static bool Refresh(IEnumerable<DriveRecord> known)
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.NoRootDirectory or DriveType.CDRom or DriveType.Unknown) continue;
                if (!d.IsReady) continue;
                var root = d.RootDirectory.FullName;
                var id = GetVolumeId(root);
                if (id is not null) next[id] = root;
            }
            catch
            {
                // drive vanished mid-enumeration
            }
        }

        foreach (var k in known)
        {
            if (!k.Id.StartsWith("UNC:", StringComparison.Ordinal) || next.ContainsKey(k.Id)) continue;
            try
            {
                if (!string.IsNullOrEmpty(k.LastRoot) && Directory.Exists(k.LastRoot)) next[k.Id] = k.LastRoot;
            }
            catch
            {
            }
        }

        lock (Gate)
        {
            var changed = next.Count != _online.Count ||
                          next.Any(kv => !_online.TryGetValue(kv.Key, out var old) ||
                                         !string.Equals(old, kv.Value, StringComparison.OrdinalIgnoreCase));
            _online = next;
            return changed;
        }
    }

    /// <summary>Identifies the drive that holds <paramref name="path"/>.</summary>
    public static (string Id, string Root, string Label)? Identify(string path)
    {
        var full = Path.GetFullPath(path);

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = full.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var root = @"\\" + parts[0] + @"\" + parts[1] + @"\";
            return ("UNC:" + root.ToLowerInvariant(), root, parts[1]);
        }

        var pathRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(pathRoot)) return null;
        var id = GetVolumeId(pathRoot);
        if (id is null) return null;

        string label;
        try
        {
            label = new DriveInfo(pathRoot).VolumeLabel;
        }
        catch
        {
            label = "";
        }

        var letter = pathRoot.TrimEnd('\\');
        label = string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})";
        return (id, pathRoot, label);
    }

    private static string? GetVolumeId(string root)
    {
        return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0)
            ? serial.ToString("X8")
            : null;
    }
}
