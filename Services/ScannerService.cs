using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Services;

/// <summary>Live scan state, CineLibrary style: running counts plus the folder being worked on.</summary>
public sealed record ScanProgress(
    int Found, int Checked, int Inserted, int Updated, int Skipped, string CurrentFolder, bool Done);

public sealed class ScanResult
{
    public int Found;
    public int Inserted;
    public int Updated;
    public int Skipped;
    public int Failed;
    public int Missing;
    public int OfflineFolders;

    public void Add(ScanResult o)
    {
        Found += o.Found;
        Inserted += o.Inserted;
        Updated += o.Updated;
        Skipped += o.Skipped;
        Failed += o.Failed;
        Missing += o.Missing;
        OfflineFolders += o.OfflineFolders;
    }

    public string Summary =>
        $"{Inserted:N0} new, {Updated:N0} updated, {Skipped:N0} unchanged" +
        (Failed > 0 ? $", {Failed:N0} failed" : "") +
        (Missing > 0 ? $", {Missing:N0} missing" : "");
}

/// <summary>
/// Walks library folders (always including every subfolder) and updates the SQLite index.
/// Same architecture as CineLibrary's scanner:
///  - defensive stack walk: system/recycle folders are skipped, an unreadable folder never stops the scan;
///  - incremental: a file whose size and timestamp did not change is not read again, unless it was read by an
///    older metadata reader (then it is read again without re-rendering its cover);
///  - names: the file-name parser reads each name in the light of the whole library (known authors, names
///    that recur); after the walk, details taken from file names are read again with everything learned;
///  - mark-missing-then-clear: books that vanished from a connected folder are flagged for review, never
///    silently deleted. A cancelled scan changes no flags.
/// Folders on unplugged drives are skipped and their books stay in the catalog (OFFLINE).
/// </summary>
public sealed class ScannerService
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "RECYCLER", "Config.Msi", "$WinREAgent", "Recovery",
        ".caltrash"   // Calibre's trash
    };

    private readonly LibraryRepository _repo;
    public ScannerService(LibraryRepository repo) => _repo = repo;

    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<LibraryFolder> folders, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var total = new ScanResult();
        FileNameParser.Context = NameContext.Build(_repo.GetDeclaredAuthors(), _repo.GetAllRelPaths());

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

        await Task.Run(RefreshNamesFromFiles, ct);
        progress?.Report(new ScanProgress(total.Found, total.Found, total.Inserted, total.Updated, total.Skipped, "", true));
        return total;
    }

    /// <summary>
    /// Reads the file names again with what the whole library now knows (the authors every book declares,
    /// names that recur), for books whose title, author or other details came from their file name, and for
    /// books with no author at all. Only file names are read, so this takes a moment even for thousands.
    /// </summary>
    private void RefreshNamesFromFiles()
    {
        var ctx = NameContext.Build(_repo.GetDeclaredAuthors(), _repo.GetAllRelPaths());
        FileNameParser.Context = ctx;
        var changed = new List<LibraryRepository.NameDerivedBook>();
        foreach (var b in _repo.GetNameDerivedBooks())
        {
            var p = FileNameParser.Parse(b.RelPath);
            var nf = b.NameFields;
            var title = b.Title;
            var author = b.Author;
            var series = b.Series;
            var seriesIndex = b.SeriesIndex;
            var year = b.Year;
            var publisher = b.Publisher;

            if ((nf & BookMetadata.NameField.Title) != 0 && !string.IsNullOrWhiteSpace(p.Title)) title = p.Title;
            if ((nf & BookMetadata.NameField.Author) != 0 || author.Length == 0)
            {
                author = p.Author ?? "";
                nf = author.Length > 0 ? nf | BookMetadata.NameField.Author : nf & ~BookMetadata.NameField.Author;
            }
            if ((nf & BookMetadata.NameField.Series) != 0)
            {
                series = p.Series ?? "";
                seriesIndex = p.SeriesIndex ?? seriesIndex;
            }
            if ((nf & BookMetadata.NameField.Year) != 0) year = p.Year;
            if ((nf & BookMetadata.NameField.Publisher) != 0) publisher = p.Publisher ?? "";

            var now = b with { NameFields = nf, Title = title, Author = author, Series = series, SeriesIndex = seriesIndex, Year = year, Publisher = publisher };
            if (now != b) changed.Add(now);
        }
        if (changed.Count > 0)
        {
            _repo.UpdateNameDerived(changed);
            Log.Write($"Names read again with the library's context: {changed.Count} books updated ({ctx.PeopleCount} known names)");
        }
    }

    private async Task<ScanResult> ScanFolderAsync(
        LibraryFolder folder, string root, string driveRoot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var result = new ScanResult();

        var files = await Task.Run(() => FindBookFiles(root, progress, ct), ct);
        result.Found = files.Count;
        var existing = _repo.GetExistingForFolder(folder.Id);
        var seen = new List<string>(files.Count);

        // Calibre keeps one book (possibly in several formats) per folder. Only then is metadata.opf trustworthy.
        var stemsPerDir = files
            .GroupBy(f => f.DirectoryName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => g.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StringComparer.OrdinalIgnoreCase);

        var work = new List<(FileInfo File, string Rel, bool IsNew, bool ReadCover)>();
        foreach (var fi in files)
        {
            var rel = Path.GetRelativePath(driveRoot, fi.FullName);
            seen.Add(rel);

            if (existing.TryGetValue(rel, out var ex))
            {
                var unchanged = ex.Size == fi.Length && ex.ModifiedTicks == fi.LastWriteTimeUtc.Ticks;
                if (unchanged && ex.MetaVersion >= MetadataService.Version)
                {
                    result.Skipped++;
                    continue;
                }
                // Changed file: read everything. Unchanged but read by an older reader: keep the cover it has.
                work.Add((fi, rel, false, !unchanged || !ex.HasCover));
            }
            else
            {
                work.Add((fi, rel, true, true));
            }
        }

        var done = result.Skipped;
        Report();

        await Parallel.ForEachAsync(work,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    var format = FormatHelper.FromPath(item.File.Name);
                    var useCalibre = stemsPerDir.TryGetValue(item.File.DirectoryName ?? "", out var c) && c == 1;
                    var md = await MetadataService.ReadAsync(item.File.FullName, format, useCalibre, item.ReadCover);

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
                        AddedUtc = AddedTime(item.File),
                        MetaVersion = MetadataService.Version,
                        NameFields = md.NameFields,
                        CoverWeak = md.CoverIsTextPage
                    };

                    if (md.Cover is { Length: > 0 })
                        book.CoverFile = await CoverStore.SaveAsync(md.Cover, md.CoverExt, book.StateKey);

                    _repo.UpsertBook(book);

                    if (item.IsNew) Interlocked.Increment(ref result.Inserted);
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

                if (Interlocked.Increment(ref done) % 5 == 0) Report(item.File.DirectoryName);
            });

        // Only a completed walk may decide what is missing.
        result.Missing = _repo.MarkMissing(folder.Id, folder.DriveId, seen);
        _repo.TouchDrive(folder.DriveId, driveRoot);
        Report();
        return result;

        void Report(string? currentDir = null) =>
            progress?.Report(new ScanProgress(result.Found, done, result.Inserted, result.Updated, result.Skipped,
                currentDir ?? root, false));
    }

    private static DateTime AddedTime(FileInfo fi)
    {
        var created = fi.CreationTimeUtc;
        var modified = fi.LastWriteTimeUtc;
        var best = created < modified ? created : modified;
        return best.Year < 1990 ? DateTime.UtcNow : best;
    }

    /// <summary>
    /// Stack walk of <paramref name="root"/> and every subfolder. Skips system/recycle folders and folders that
    /// are both hidden and system; an access-denied or vanished folder is skipped instead of ending the scan.
    /// </summary>
    private static List<FileInfo> FindBookFiles(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var list = new List<FileInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            try
            {
                foreach (var fi in dir.EnumerateFiles())
                {
                    if (!FormatHelper.IsSupported(fi.Name)) continue;
                    if (fi.Name.StartsWith("._", StringComparison.Ordinal)) continue;   // macOS resource forks
                    list.Add(fi);
                }

                foreach (var sub in dir.EnumerateDirectories())
                {
                    if (ExcludedDirs.Contains(sub.Name)) continue;
                    var attr = sub.Attributes;
                    if ((attr & FileAttributes.Hidden) != 0 && (attr & FileAttributes.System) != 0) continue;
                    stack.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            progress?.Report(new ScanProgress(list.Count, 0, 0, 0, 0, dir.FullName, false));
        }
        return list;
    }
}
