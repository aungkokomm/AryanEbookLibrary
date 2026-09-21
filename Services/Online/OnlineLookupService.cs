using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services.Metadata;
using Microsoft.UI.Dispatching;

namespace AryanEbookLibrary.Services.Online;

/// <summary>
/// Fills in missing details from Open Library in the background (only when the user switched it on), and does
/// the fetching for the Find online window. Every result is saved as it arrives, so closing the app loses
/// nothing and the next start carries on with the books still to do.
/// </summary>
public sealed class OnlineLookupService
{
    private sealed record Item(LookupBook Book, bool NeedsCover, int Tries);

    private readonly LibraryRepository _repo;
    private CancellationTokenSource? _cts;
    private int _held;
    private volatile bool _running;

    public OpenLibraryClient Client { get; } = new();

    /// <summary>Background progress for the status bar, "" when there is nothing to say. Raised on the UI thread.</summary>
    public event Action<string>? StatusChanged;

    public OnlineLookupService(LibraryRepository repo) => _repo = repo;

    /// <summary>Looks up the books that miss details and were not looked up yet. Call on the UI thread.</summary>
    public void Start(IReadOnlyList<Book> books)
    {
        if (!AppServices.Settings.LookupOnline || _running) return;
        var now = DateTime.UtcNow;
        var queue = books.Where(b => NeedsLookup(b, now))
            .OrderBy(b => b.Isbn.Length > 0 ? 0 : b.FileAuthor.Length == 0 ? 1 : 2)   // surest first
            .Select(b => new Item(ToLookup(b), b.NeedsCover, b.Online?.Tries ?? 0))
            .ToList();
        if (queue.Count == 0) return;

        _running = true;
        _cts = new CancellationTokenSource();
        _ = RunAsync(queue, DispatcherQueue.GetForCurrentThread(), _cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>While the Find online window is open the background waits, so the user's search goes first.</summary>
    public void Hold(bool on) => Interlocked.Add(ref _held, on ? 1 : -1);

    public static LookupBook ToLookup(Book b)
    {
        var isbn = string.IsNullOrEmpty(b.Isbn) ? null : Isbn.Digits(b.Isbn);
        return new LookupBook(b.StateKey, b.Title, b.Author,
            b.CustomAuthor is null && (b.NameFields & BookMetadata.NameField.Author) != 0,
            Path.GetFileNameWithoutExtension(b.RelPath), Isbn.IsValid(isbn) ? isbn : null, b.Series);
    }

    /// <summary>Not looked up yet (or Open Library failed a while ago), and something is missing that it could give.</summary>
    private static bool NeedsLookup(Book b, DateTime now)
    {
        if (b.Online is { } o && !(o.Status == OnlineDetails.Error && o.Tries < 5 && now - o.UpdatedUtc > TimeSpan.FromMinutes(30)))
            return false;
        var fromName = (b.NameFields & (BookMetadata.NameField.Title | BookMetadata.NameField.Author)) != 0;
        if (!(b.FileAuthor.Length == 0 || fromName || b.NeedsCover || b.FileDescription.Length == 0)) return false;
        var l = ToLookup(b);
        return l.Isbn is not null || OnlineMatcher.CanSearch(l);
    }

    public static OnlineDetails FromCandidate(OnlineCandidate c, string status, string? how) => new()
    {
        Status = status,
        How = how,
        SourceKey = c.WorkKey,
        Title = c.Title,
        Author = c.Authors.Count > 0 ? c.AuthorText : null,
        Publisher = c.Publisher,
        Year = c.Year,
        Subjects = c.Subjects.Count > 0 ? string.Join(", ", c.Subjects) : null,
        CoverId = c.CoverId
    };

    /// <summary>
    /// Shows details the user picked at once, then adds the description and cover when they arrive (Open Library
    /// can take many seconds). They are only added if the pick is still there by then. Call on the UI thread.
    /// </summary>
    public void ApplyPicked(Book book, OnlineDetails o)
    {
        _repo.UpsertOnline(book.StateKey, o);
        book.SetOnline(o);

        var key = book.StateKey;
        var full = o.Copy();
        var ui = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await FetchExtrasAsync(key, full, full.UseCover, timeout.Token);
            }
            catch (Exception ex)
            {
                Log.Write("Open Library: description or cover not fetched: " + ex.Message);
                return;
            }
            ui.TryEnqueue(() =>
            {
                var b = AppServices.Library.FindByKey(key);
                if (b?.Online is not { IsPicked: true } now || now.SourceKey != full.SourceKey) return;   // removed or re-picked meanwhile
                _repo.UpsertOnline(key, full);
                b.SetOnline(full);
            });
        });
    }

    /// <summary>
    /// Fetches the description and, when wanted, the cover, then saves. If Open Library fails for those two,
    /// the details are saved without them rather than lost.
    /// </summary>
    public async Task CompleteAndSaveAsync(string key, OnlineDetails o, bool wantCover, CancellationToken ct)
    {
        await FetchExtrasAsync(key, o, wantCover, ct);
        _repo.UpsertOnline(key, o);
    }

    private async Task FetchExtrasAsync(string key, OnlineDetails o, bool wantCover, CancellationToken ct)
    {
        if (o.SourceKey is not null && o.Description is null)
        {
            try { o.Description = await Client.DescriptionAsync(o.SourceKey, ct); }
            catch (OnlineUnavailableException) { }
        }
        if (wantCover && o.CoverId is { } id && o.CoverFile is null)
        {
            try
            {
                if (await Client.CoverAsync(id, ct) is { } bytes) o.CoverFile = await CoverStore.SaveOnlineAsync(bytes, key);
            }
            catch (OnlineUnavailableException) { }
        }
    }

    private async Task RunAsync(List<Item> queue, DispatcherQueue ui, CancellationToken ct)
    {
        int done = 0, filled = 0, suggested = 0, failuresInARow = 0;
        void Report(string text) => ui.TryEnqueue(() => StatusChanged?.Invoke(text));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Report($"Open Library: looking up {queue.Count:N0} books");
        try
        {
            // Three at a time: each answer takes seconds, while the client keeps to one request a second.
            var options = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = stop.Token };
            await Parallel.ForEachAsync(queue, options, async (item, t) =>
            {
                while (Volatile.Read(ref _held) > 0) await Task.Delay(500, t);
                OnlineDetails details;
                try
                {
                    var d = await OnlineMatcher.LookupAsync(Client, item.Book, t);
                    details = d.Candidate is { } c ? FromCandidate(c, d.Status, d.How) : new OnlineDetails { Status = d.Status };
                    if (details.IsApplied) await CompleteAndSaveAsync(item.Book.Key, details, item.NeedsCover, t);
                    else _repo.UpsertOnline(item.Book.Key, details);
                    Interlocked.Exchange(ref failuresInARow, 0);
                    if (details.IsApplied) Interlocked.Increment(ref filled);
                    else if (details.Status == OnlineDetails.Suggested) Interlocked.Increment(ref suggested);
                }
                catch (OnlineUnavailableException)
                {
                    details = new OnlineDetails { Status = OnlineDetails.Error, Tries = item.Tries + 1 };
                    _repo.UpsertOnline(item.Book.Key, details);
                    if (Interlocked.Increment(ref failuresInARow) >= 3) stop.Cancel();
                }

                var key = item.Book.Key;
                ui.TryEnqueue(() => AppServices.Library.FindByKey(key)?.SetOnline(details));
                var n = Interlocked.Increment(ref done);
                Report($"Open Library: {n:N0} of {queue.Count:N0} looked up, {Volatile.Read(ref filled):N0} filled in");
            });
            Report($"Open Library filled in {filled:N0} of {queue.Count:N0} books" +
                   (suggested > 0 ? $", {suggested:N0} suggestions wait in their details" : ""));
        }
        catch (OperationCanceledException)
        {
            Report(ct.IsCancellationRequested ? "" : "Open Library isn't answering. Aryan will try again after the next scan.");
        }
        catch (Exception ex)
        {
            Log.Write("Open Library lookups failed: " + ex);
            Report("");
        }
        finally
        {
            Log.Write($"Open Library: {done} of {queue.Count} looked up, {filled} filled in, {suggested} suggested");
            _running = false;
        }
    }
}
