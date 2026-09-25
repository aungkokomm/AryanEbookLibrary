using System.Text.Json;
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

    private sealed record AuthorJob(string Key, string Name, string? Qid, string? WorksJson, int Tries, List<Item> Books);

    private readonly LibraryRepository _repo;
    private CancellationTokenSource? _cts;
    private int _held;
    private volatile bool _running;

    public OpenLibraryClient Client { get; } = new();
    public WikidataClient Wikidata { get; } = new();
    public WikipediaClient Wikipedia { get; } = new();
    public GoogleBooksClient Google { get; } = new(() => AppServices.Settings.GoogleBooksKey);

    /// <summary>Background progress for the status bar, "" when there is nothing to say. Raised on the UI thread.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised on the UI thread when a run of lookups is over; its last message is the latest StatusChanged.</summary>
    public event Action? Finished;

    public OnlineLookupService(LibraryRepository repo) => _repo = repo;

    /// <summary>Looks up the books that miss details and were not looked up yet. Call on the UI thread.</summary>
    /// <summary>
    /// Fills in what these books are missing, in the background. <paramref name="force"/> is the user asking
    /// for it by hand (the books they picked), which does not need the setting to be on.
    /// </summary>
    public void Start(IReadOnlyList<Book> books, bool force = false)
    {
        if (!force && !AppServices.Settings.LookupOnline || _running) return;
        var now = DateTime.UtcNow;
        var queue = books.Where(b => NeedsLookup(b, now))
            .OrderBy(b => b.Isbn.Length > 0 ? 0 : b.FileAuthor.Length == 0 ? 1 : 2)   // surest first
            .Select(b => new Item(ToLookup(b), b.NeedsCover, b.Online?.Tries ?? 0))
            .ToList();
        var authors = AuthorJobs(books, now);
        var google = Google.HasKey
            ? books.Where(b => NeedsGoogle(b, now))
                .OrderBy(b => b.Isbn.Length > 0 ? 0 : 1)
                .ThenByDescending(b => b.LastOpenedUtc ?? DateTime.MinValue)   // the books being read first
                .Select(b => new Item(ToLookup(b), false, b.OnlineFrom(OnlineSource.GoogleBooks)?.Tries ?? 0))
                .ToList()
            : new List<Item>();
        if (queue.Count == 0 && authors.Count == 0 && google.Count == 0) return;

        _running = true;
        _cts = new CancellationTokenSource();
        var ui = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            try
            {
                if (queue.Count > 0) await RunAsync(queue, ui, _cts.Token);
                if (authors.Count > 0) await RunAuthorsAsync(authors, ui, _cts.Token);
                if (google.Count > 0) await RunGoogleAsync(google, ui, _cts.Token);
            }
            finally
            {
                _running = false;
                ui.TryEnqueue(() => Finished?.Invoke());
            }
        });
    }

    /// <summary>
    /// Wikidata is asked once per author, not once per book: one query gives their whole catalogue with
    /// series numbers and the Wikipedia page of each work, which many books of that author then share.
    /// </summary>
    private List<AuthorJob> AuthorJobs(IReadOnlyList<Book> books, DateTime now)
    {
        var known = _repo.GetAllAuthorsOnline();
        var jobs = new List<AuthorJob>();
        foreach (var group in books.Where(WantsWikidata).GroupBy(FirstAuthor, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length < 4) continue;
            var key = group.Key.ToLowerInvariant();
            if (known.TryGetValue(key, out var seen))
            {
                var retry = seen.Status == OnlineDetails.Error && seen.Tries < 3 && now - seen.UpdatedUtc > TimeSpan.FromHours(1);
                var books2 = group.Where(b => b.OnlineFrom(OnlineSource.Wikidata) is null).ToList();
                if (!retry && books2.Count == 0) continue;
                jobs.Add(new AuthorJob(key, group.Key, seen.Qid, seen.WorksJson, seen.Tries,
                    books2.Select(b => new Item(ToLookup(b), b.NeedsCover, 0)).ToList()));
                continue;
            }
            jobs.Add(new AuthorJob(key, group.Key, null, null, 0,
                group.Select(b => new Item(ToLookup(b), b.NeedsCover, 0)).ToList()));
        }
        return jobs.OrderByDescending(j => j.Books.Count).ToList();
    }

    private static string FirstAuthor(Book b) => b.Author.Split(',')[0].Trim();

    /// <summary>A book whose author is a Latin-script person's name and which still lacks a description, cover or series.</summary>
    private static bool WantsWikidata(Book b)
    {
        if (b.Author.Length == 0 || Myanmar(b.Author) || Myanmar(b.Title)) return false;
        if (b.Author.Contains('.') || b.Author.Contains('@')) return false;          // "www.oshoworld.com", an e-mail
        if (!b.Author.Trim().Contains(' ')) return false;                            // "ISECOM", "CamScanner", "gnv64"
        return b.Description.Length == 0 || b.NeedsCover || b.Series.Length == 0;
    }

    private static bool Myanmar(string s) => s.Any(c => c is >= 'က' and <= '႟');

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

    /// <summary>
    /// Not asked of Google Books yet (or it failed a while ago), still missing something Google gives (a description,
    /// year, publisher or subjects), and something to look it up by.
    /// </summary>
    private static bool NeedsGoogle(Book b, DateTime now)
    {
        if (b.OnlineFrom(OnlineSource.GoogleBooks) is { } g &&
            !(g.Status == OnlineDetails.Error && g.Tries < 5 && now - g.UpdatedUtc > TimeSpan.FromMinutes(30)))
            return false;
        if (!(b.Description.Length == 0 || b.Year is null || b.Publisher.Length == 0 || b.Subjects.Length == 0)) return false;
        var l = ToLookup(b);
        return l.Isbn is not null || OnlineMatcher.CanSearch(l);
    }

    public static OnlineDetails FromCandidate(OnlineCandidate c, string status, string? how, string source = OnlineSource.OpenLibrary) => new()
    {
        Source = source,
        Description = c.Description,
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
        }
    }

    /// <summary>
    /// Google Books, one book at a time (its key allows about a thousand requests a day): the same matching as Open
    /// Library's, and each answer saved as it comes. When the day's allowance is used up it stops without marking the
    /// books still to do, so the next start carries on with them.
    /// </summary>
    private async Task RunGoogleAsync(List<Item> queue, DispatcherQueue ui, CancellationToken ct)
    {
        int done = 0, filled = 0, failuresInARow = 0;
        void Report(string text) => ui.TryEnqueue(() => StatusChanged?.Invoke(text));
        Report($"Google Books: looking up {queue.Count:N0} books");
        try
        {
            foreach (var item in queue)
            {
                while (Volatile.Read(ref _held) > 0) await Task.Delay(500, ct);
                ct.ThrowIfCancellationRequested();
                OnlineDetails details;
                try
                {
                    var d = await OnlineMatcher.LookupAsync(Google, item.Book, ct);
                    details = d.Candidate is { } c
                        ? FromCandidate(c, d.Status, d.How, OnlineSource.GoogleBooks)
                        : new OnlineDetails { Source = OnlineSource.GoogleBooks, Status = d.Status };
                    failuresInARow = 0;
                    if (details.IsApplied) filled++;
                }
                catch (OnlineUnavailableException)
                {
                    details = new OnlineDetails { Source = OnlineSource.GoogleBooks, Status = OnlineDetails.Error, Tries = item.Tries + 1 };
                    if (++failuresInARow >= 3)
                    {
                        _repo.UpsertOnline(item.Book.Key, details);
                        Report("Google Books isn't answering. Aryan will try again after the next scan.");
                        return;
                    }
                }
                _repo.UpsertOnline(item.Book.Key, details);
                var key = item.Book.Key;
                ui.TryEnqueue(() => AppServices.Library.FindByKey(key)?.SetOnline(details));
                done++;
                Report($"Google Books: {done:N0} of {queue.Count:N0} looked up, {filled:N0} filled in");
            }
            Report($"Google Books filled in {filled:N0} of {queue.Count:N0} books");
        }
        catch (GoogleQuotaException ex)
        {
            Log.Write("Google Books: " + ex.Message);
            Report(ex.Message + (done > 0 ? $" {filled:N0} of {done:N0} filled in so far;" : "") + " Aryan carries on after the next start.");
        }
        catch (OperationCanceledException)
        {
            Report("");
        }
        catch (Exception ex)
        {
            Log.Write("Google Books lookups failed: " + ex);
            Report("");
        }
        finally
        {
            Log.Write($"Google Books: {done} of {queue.Count} looked up, {filled} filled in");
        }
    }

    /// <summary>
    /// Wikidata author by author (their id and catalogue are kept, so a second run costs nothing), then
    /// Wikipedia for the works it names a page for: that is where descriptions and many covers come from.
    /// </summary>
    private async Task RunAuthorsAsync(List<AuthorJob> jobs, DispatcherQueue ui, CancellationToken ct)
    {
        int done = 0, matched = 0, described = 0, failuresInARow = 0;
        void Report(string text) => ui.TryEnqueue(() => StatusChanged?.Invoke(text));
        Report($"Wikidata: looking up {jobs.Count:N0} authors");
        try
        {
            foreach (var job in jobs)
            {
                while (Volatile.Read(ref _held) > 0) await Task.Delay(500, ct);
                ct.ThrowIfCancellationRequested();
                done++;
                Report($"Wikidata: {done:N0} of {jobs.Count:N0} authors, {matched:N0} books matched, {described:N0} described");

                List<WikidataWork> works;
                try
                {
                    works = await AuthorWorksAsync(job, ct);
                    failuresInARow = 0;
                }
                catch (OnlineUnavailableException)
                {
                    _repo.UpsertAuthorOnline(new LibraryRepository.AuthorOnline(
                        job.Key, job.Name, job.Qid, OnlineDetails.Error, job.WorksJson, job.Tries + 1, DateTime.UtcNow));
                    if (++failuresInARow >= 3)
                    {
                        Report("Wikidata isn't answering. Aryan will try again after the next scan.");
                        return;
                    }
                    continue;
                }

                foreach (var item in job.Books)
                {
                    ct.ThrowIfCancellationRequested();
                    var work = works.Where(w => OnlineMatcher.WorkTitleAgrees(item.Book, w.Title))
                        .MaxBy(w => OnlineMatcher.TitleOverlap(item.Book, w.Title));
                    var facts = work is null
                        ? new OnlineDetails { Source = OnlineSource.Wikidata, Status = OnlineDetails.None }
                        : new OnlineDetails
                        {
                            Source = OnlineSource.Wikidata,
                            Status = OnlineDetails.Found,
                            How = OnlineDetails.ByMatch,
                            SourceKey = work.Key,
                            Title = work.Title,
                            Year = work.Year,
                            Series = work.Series,
                            SeriesIndex = work.SeriesIndex,
                            PageTitle = work.WikipediaTitle
                        };
                    _repo.UpsertOnline(item.Book.Key, facts);
                    var key = item.Book.Key;
                    ui.TryEnqueue(() => AppServices.Library.FindByKey(key)?.SetOnline(facts));
                    if (work is null) continue;
                    matched++;

                    if (work.WikipediaTitle is null) continue;
                    OnlineDetails? article;
                    try
                    {
                        article = await ArticleAsync(key, work.WikipediaTitle, item.NeedsCover, ct);
                        failuresInARow = 0;
                    }
                    catch (OnlineUnavailableException)
                    {
                        if (++failuresInARow >= 3)
                        {
                            Report("Wikipedia isn't answering. Aryan will try again after the next scan.");
                            return;
                        }
                        continue;
                    }
                    if (article is null) continue;
                    described++;
                    _repo.UpsertOnline(key, article);
                    ui.TryEnqueue(() => AppServices.Library.FindByKey(key)?.SetOnline(article));
                }
            }
            Report($"Wikidata and Wikipedia: {matched:N0} books matched, {described:N0} with a description");
        }
        catch (OperationCanceledException)
        {
            Report("");
        }
        catch (Exception ex)
        {
            Log.Write("Wikidata lookups failed: " + ex);
            Report("");
        }
        finally
        {
            Log.Write($"Wikidata: {done} of {jobs.Count} authors, {matched} books matched, {described} described");
        }
    }

    /// <summary>The author's catalogue, from the kept copy when there is one.</summary>
    private async Task<List<WikidataWork>> AuthorWorksAsync(AuthorJob job, CancellationToken ct)
    {
        if (job.WorksJson is { Length: > 0 } saved)
            return JsonSerializer.Deserialize<List<WikidataWork>>(saved) ?? new List<WikidataWork>();

        var qid = job.Qid ?? await Wikidata.FindAuthorAsync(job.Name, ct);
        if (qid is null)
        {
            _repo.UpsertAuthorOnline(new LibraryRepository.AuthorOnline(
                job.Key, job.Name, null, OnlineDetails.None, null, job.Tries, DateTime.UtcNow));
            return new List<WikidataWork>();
        }

        var works = await Wikidata.WorksByAuthorAsync(qid, ct);
        _repo.UpsertAuthorOnline(new LibraryRepository.AuthorOnline(
            job.Key, job.Name, qid, OnlineDetails.Found, JsonSerializer.Serialize(works), job.Tries, DateTime.UtcNow));
        return works;
    }

    /// <summary>The Wikipedia article about this book: its opening paragraphs, and its picture when a cover is wanted.</summary>
    private async Task<OnlineDetails?> ArticleAsync(string key, string pageTitle, bool wantCover, CancellationToken ct)
    {
        var article = await Wikipedia.ArticleAsync(pageTitle, ct);
        if (article is null) return null;
        var details = new OnlineDetails
        {
            Source = OnlineSource.Wikipedia,
            Status = OnlineDetails.Found,
            How = OnlineDetails.ByMatch,
            SourceKey = article.Title,
            PageTitle = article.Title,
            Description = article.Summary,
            CoverUrl = article.ImageUrl
        };
        if (wantCover && article.ImageUrl is { } url)
        {
            try
            {
                if (await Wikipedia.ImageAsync(url, ct) is { } bytes)
                    details.CoverFile = await CoverStore.SaveOnlineAsync(bytes, key + "|wikipedia");
            }
            catch (OnlineUnavailableException)
            {
                // the text is worth keeping even when the picture fails
            }
        }
        return details;
    }
}
