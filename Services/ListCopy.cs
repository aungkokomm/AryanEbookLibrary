using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// "Copy books to folder" for one of My lists (CineLibrary's list copy, for book files): which files can be
/// copied now, which wait on an unplugged drive, what is already at the destination, then the copy itself with
/// progress and a way to stop. Only the book files are copied, never the folders around them.
/// </summary>
public sealed class ListCopy
{
    public sealed record Item(Book Book, string Source, string Target, long Size);

    public List<Item> Items { get; } = new();
    /// <summary>Names of the drives holding books that cannot be copied until they are plugged in.</summary>
    public List<string> OfflineDrives { get; } = new();
    public int OfflineBooks { get; private set; }
    /// <summary>Books whose drive is here but whose file is not (moved or deleted since the last scan).</summary>
    public int MissingFiles { get; private set; }
    public long TotalBytes => Items.Sum(i => i.Size);

    public static ListCopy Plan(IEnumerable<Book> books, string destination)
    {
        var plan = new ListCopy();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in books)
        {
            if (!book.IsAvailable || book.FullPath is not { } source)
            {
                plan.OfflineBooks++;
                if (!plan.OfflineDrives.Contains(book.DriveLabel)) plan.OfflineDrives.Add(book.DriveLabel);
                continue;
            }
            var file = new FileInfo(source);
            if (!file.Exists)
            {
                plan.MissingFiles++;
                continue;
            }
            // Two books with the same file name from different folders: the second becomes "name (2).epub".
            var name = file.Name;
            for (var n = 2; !names.Add(name); n++)
                name = $"{Path.GetFileNameWithoutExtension(file.Name)} ({n}){file.Extension}";
            plan.Items.Add(new Item(book, source, Path.Combine(destination, name), file.Length));
        }
        return plan;
    }

    /// <summary>Files already at the destination under the name they would get.</summary>
    public List<Item> Existing() => Items.Where(i => File.Exists(i.Target)).ToList();

    /// <summary>Bytes the copy will write: every file, or without the ones kept when skipping.</summary>
    public long BytesToCopy(bool overwrite) => Items.Where(i => overwrite || !File.Exists(i.Target)).Sum(i => i.Size);

    /// <summary>Free space where the files go, or -1 when Windows cannot say (a network path).</summary>
    public static long FreeBytes(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    public sealed record Progress(int Index, int Count, string Name, long BytesDone, long BytesTotal);

    public sealed record Result(int Copied, int Skipped, int Failed, bool Cancelled);

    /// <summary>
    /// Copies each file through a ".partial" name and renames it when complete, so a stop half way never leaves
    /// half a book under the real name. Runs off the UI thread; progress comes back on the caller's context.
    /// </summary>
    public Task<Result> RunAsync(bool overwrite, IProgress<Progress> progress, CancellationToken ct) => Task.Run(async () =>
    {
        int copied = 0, skipped = 0, failed = 0;
        long done = 0;
        var total = BytesToCopy(overwrite);
        var buffer = new byte[1 << 20];
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (ct.IsCancellationRequested) return new Result(copied, skipped, failed, true);
            if (!overwrite && File.Exists(item.Target))
            {
                skipped++;
                continue;
            }
            progress.Report(new Progress(i + 1, Items.Count, Path.GetFileName(item.Target), done, total));
            var partial = item.Target + ".partial";
            try
            {
                await using (var from = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
                await using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int read;
                    while ((read = await from.ReadAsync(buffer, ct)) > 0)
                    {
                        await to.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        progress.Report(new Progress(i + 1, Items.Count, Path.GetFileName(item.Target), done, total));
                    }
                }
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(item.Source));
                File.Move(partial, item.Target, overwrite: true);
                copied++;
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                return new Result(copied, skipped, failed, true);
            }
            catch (Exception ex)
            {
                TryDelete(partial);
                Log.Write($"List copy: {item.Source} -> {item.Target} failed: {ex.Message}");
                failed++;
            }
        }
        return new Result(copied, skipped, failed, false);
    });

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // left behind; the next copy overwrites it
        }
    }
}
