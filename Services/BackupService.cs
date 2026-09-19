using System.Text.Json;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>Exports/imports all personal state as one portable JSON file.</summary>
public static class BackupService
{
    private sealed class BackupFile
    {
        public string App { get; set; } = "AryanEbookLibrary";
        public int Version { get; set; } = 1;
        public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
        public List<BackupItem> Items { get; set; } = new();
    }

    private sealed class BackupItem
    {
        public string DriveId { get; set; } = "";
        public string RelPath { get; set; } = "";
        public string Title { get; set; } = "";      // informational only, helps when reading the file by hand
        public BookState State { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static int Export(string file, LibraryRepository repo)
    {
        var titles = repo.GetTitlesByKey();
        var backup = new BackupFile();

        foreach (var row in repo.GetAllStates())
        {
            if (row.State.IsDefault) continue;
            backup.Items.Add(new BackupItem
            {
                DriveId = row.DriveId,
                RelPath = row.RelPath,
                Title = titles.TryGetValue(row.Key, out var t) ? t : "",
                State = row.State
            });
        }

        File.WriteAllText(file, JsonSerializer.Serialize(backup, Options));
        return backup.Items.Count;
    }

    /// <summary>Merges a backup into the local state. Newest edit wins. Returns the number of entries applied.</summary>
    public static int Import(string file, LibraryRepository repo)
    {
        var backup = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(file))
                     ?? throw new InvalidDataException("Not a valid backup file.");
        if (!string.Equals(backup.App, "AryanEbookLibrary", StringComparison.Ordinal))
            throw new InvalidDataException("This file was not created by Aryan eBook Library.");

        var local = repo.GetAllStates().ToDictionary(s => s.Key, s => s.State.UpdatedUtc);
        var applied = 0;

        foreach (var item in backup.Items)
        {
            var key = Book.MakeKey(item.DriveId, item.RelPath);
            if (local.TryGetValue(key, out var updated) && updated >= item.State.UpdatedUtc) continue;
            repo.UpsertState(item.DriveId, item.RelPath, key, item.State);
            applied++;
        }
        return applied;
    }
}
