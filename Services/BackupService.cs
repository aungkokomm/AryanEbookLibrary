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
        public List<string> Lists { get; set; } = new();   // My lists, so an empty one comes back too
        public List<BackupItem> Items { get; set; } = new();
        public List<BackupAnnotation> Annotations { get; set; } = new();   // highlights and notes, deleted ones' markers too
    }

    private sealed class BackupItem
    {
        public string DriveId { get; set; } = "";
        public string RelPath { get; set; } = "";
        public string Title { get; set; } = "";      // informational only, helps when reading the file by hand
        public BookState State { get; set; } = new();
    }

    private sealed class BackupAnnotation
    {
        public string DriveId { get; set; } = "";
        public string RelPath { get; set; } = "";
        public Annotation Annotation { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Writes every book's personal state and every annotation. Returns how many entries were written.</summary>
    public static int Export(string file, LibraryRepository repo, AnnotationStore annotations)
    {
        var titles = repo.GetTitlesByKey();
        var backup = new BackupFile { Lists = repo.GetLists() };

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

        foreach (var row in annotations.All(withDeleted: true))
            backup.Annotations.Add(new BackupAnnotation { DriveId = row.DriveId, RelPath = row.RelPath, Annotation = row.Annotation });

        File.WriteAllText(file, JsonSerializer.Serialize(backup, Options));
        return backup.Items.Count + backup.Annotations.Count;
    }

    /// <summary>Merges a backup into the local state. Newest edit wins. Returns the number of entries applied.</summary>
    public static int Import(string file, LibraryRepository repo, AnnotationStore annotations)
    {
        var backup = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(file))
                     ?? throw new InvalidDataException("Not a valid backup file.");
        if (!string.Equals(backup.App, "AryanEbookLibrary", StringComparison.Ordinal))
            throw new InvalidDataException("This file was not created by Aryan eBook Library.");

        repo.EnsureLists(backup.Lists);
        var local = repo.GetAllStates().ToDictionary(s => s.Key, s => s.State.UpdatedUtc);
        var applied = 0;

        foreach (var item in backup.Items)
        {
            var key = Book.MakeKey(item.DriveId, item.RelPath);
            if (local.TryGetValue(key, out var updated) && updated >= item.State.UpdatedUtc) continue;
            repo.UpsertState(item.DriveId, item.RelPath, key, item.State);
            repo.EnsureLists(item.State.Lists);
            applied++;
        }

        var marks = annotations.UpdatedById();
        foreach (var book in backup.Annotations.GroupBy(a => Book.MakeKey(a.DriveId, a.RelPath)))
        {
            var first = book.First();
            applied += annotations.Merge(book.Key, first.DriveId, first.RelPath, book.Select(a => a.Annotation), marks);
        }
        return applied;
    }
}
