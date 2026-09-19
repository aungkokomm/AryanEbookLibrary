using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Services;

public sealed record ScanProgress(string Message, int Done, int Total);

public sealed class ScanResult
{
    public int Added;
    public int Updated;
    public int Removed;
    public int Unchanged;
    public int Failed;
    public int OfflineFolders;

    public void Add(ScanResult o)
    {
        Added += o.Added;
        Updated += o.Updated;
        Removed += o.Removed;
        Unchanged += o.Unchanged;
        Failed += o.Failed;
        OfflineFolders += o.OfflineFolders;
    }
}

/// <summary>
/// Walks library folders and updates the SQLite index. Incremental: files whose size and timestamp did not
/// change are skipped. Folders on unplugged drives are skipped and their books stay in the catalog (OFFLINE).
/// </summary>
public sealed class ScannerService
{
    private readonly LibraryRepository _repo;
    public ScannerService(LibraryRepository repo) => _repo = repo;

    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<LibraryFolder> folders, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var total = new ScanResult();

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            var root = DriveRegistry.Resolve(folder.DriveId, folder.RelPath);
            var driveRoot = DriveRegistry.GetRoot(folder.DriveId);

            if (root is null || driveRoot is null || !Directory.Exists(root))
            {
                total.OfflineFolders++;
                continue;
            }

            total.Add(await ScanFolderAsync(folder, root, driveRoot, progress, ct));
        }
        return total;
    }

    private async Task<ScanResult> ScanFolderAsync(
        LibraryFolder folder, string root, string driveRoot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var result = new ScanResult();
        progress?.Report(new ScanProgress($"Looking for books in {root} ...", 0, 0));

        var files = await Task.Run(() => Enumerate(root, ct), ct);
        var existing = _repo.GetExistingForFolder(folder.Id);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Calibre keeps one book (possibly in several formats) per folder. Only then is metadata.opf trustworthy.
        var stemsPerDir = files
            .GroupBy(f => f.DirectoryName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => g.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StringComparer.OrdinalIgnoreCase);

        var work = new List<(FileInfo File, string Rel, bool IsNew)>();
        foreach (var fi in files)
        {
            var rel = Path.GetRelativePath(driveRoot, fi.FullName);
            seen.Add(rel);

            if (existing.TryGetValue(rel, out var ex))
            {
                if (ex.Size == fi.Length && ex.ModifiedTicks == fi.LastWriteTimeUtc.Ticks)
                {
                    result.Unchanged++;
                    continue;
                }
                work.Add((fi, rel, false));
            }
            else
            {
                work.Add((fi, rel, true));
            }
        }

        var done = 0;
        var totalWork = work.Count;
        var leaf = Path.GetFileName(root.TrimEnd('\\'));
        var label = leaf.Length > 0 ? leaf : root;

        await Parallel.ForEachAsync(work,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    var format = FormatHelper.FromPath(item.File.Name);
                    var useCalibre = stemsPerDir.TryGetValue(item.File.DirectoryName ?? "", out var c) && c == 1;
                    var md = await MetadataService.ReadAsync(item.File.FullName, format, useCalibre);

                    var book = new Book
                    {
                        FolderId = folder.Id,
                        DriveId = folder.DriveId,
                        RelPath = item.Rel,
                        Format = format,
                        Title = md.Title ?? "",
                        Author = md.Author ?? "",
                        Series = md.Series ?? "",
                        SeriesIndex = md.SeriesIndex,
                        Publisher = md.Publisher ?? "",
                        Year = md.Year,
                        Language = md.Language ?? "",
                        Description = md.Description ?? "",
                        Isbn = md.Isbn ?? "",
                        Subjects = md.Subjects ?? "",
                        FileSize = item.File.Length,
                        ModifiedTicks = item.File.LastWriteTimeUtc.Ticks,
                        AddedUtc = AddedTime(item.File)
                    };

                    if (md.Cover is { Length: > 0 })
                        book.CoverFile = await CoverStore.SaveAsync(md.Cover, md.CoverExt, book.StateKey);

                    _repo.UpsertBook(book);

                    if (item.IsNew) Interlocked.Increment(ref result.Added);
                    else Interlocked.Increment(ref result.Updated);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref result.Failed);
                    Log.Write($"Index failed for {item.File.FullName}: {ex.Message}");
                }

                var n = Interlocked.Increment(ref done);
                if (n % 5 == 0 || n == totalWork)
                    progress?.Report(new ScanProgress($"Indexing {label}: {n} / {totalWork}", n, totalWork));
            });

        // Books that vanished from a connected folder are removed from the index.
        var gone = existing.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value.Id).ToList();
        if (gone.Count > 0)
        {
            _repo.DeleteBooks(gone);
            result.Removed = gone.Count;
        }

        _repo.TouchDrive(folder.DriveId, driveRoot);
        return result;
    }

    private static DateTime AddedTime(FileInfo fi)
    {
        var created = fi.CreationTimeUtc;
        var modified = fi.LastWriteTimeUtc;
        var best = created < modified ? created : modified;
        return best.Year < 1990 ? DateTime.UtcNow : best;
    }

    private static List<FileInfo> Enumerate(string root, CancellationToken ct)
    {
        var list = new List<FileInfo>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false
        };

        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!FormatHelper.IsSupported(fi.Name)) continue;
            if (fi.Name.StartsWith("._", StringComparison.Ordinal)) continue;   // macOS resource forks
            var full = fi.FullName;
            if (full.Contains(@"\.caltrash\", StringComparison.OrdinalIgnoreCase) ||
                full.Contains(@"\$RECYCLE.BIN\", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(fi);
        }
        return list;
    }
}
