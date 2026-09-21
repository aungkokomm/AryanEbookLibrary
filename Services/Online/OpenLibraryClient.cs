using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AryanEbookLibrary.Services.Online;

/// <summary>One book as Open Library's search returns it.</summary>
public sealed record OnlineCandidate(
    string WorkKey, string Title, string? Subtitle, IReadOnlyList<string> Authors, int? Year, string? Publisher,
    long? CoverId, IReadOnlyList<string> Isbns, int EditionCount, IReadOnlyList<string> Subjects, bool ByIsbn)
{
    public string AuthorText => string.Join(", ", Authors.Take(3));
    public string? CoverUrl(char size) => CoverId is { } id ? $"https://covers.openlibrary.org/b/id/{id}-{size}.jpg" : null;
}

/// <summary>Open Library did not answer (network down, timeout, server error), as opposed to "no such book".</summary>
public sealed class OnlineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Open Library's public API, used politely: one request a second at most (their limit for apps that do not
/// send contact details, and none are sent), a 10 second connection timeout, and one retry. Nothing about the
/// user is sent, only the title, author or ISBN being looked up.
/// </summary>
public sealed class OpenLibraryClient
{
    private const string Fields =
        "key,title,subtitle,author_name,first_publish_year,publisher,cover_i,isbn,edition_count,subject";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public OpenLibraryClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var version = typeof(OpenLibraryClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AryanEbookLibrary/{version} (Windows ebook catalog)");
    }

    /// <summary>The book with this ISBN (its work, with the matching edition's publisher and year), or null.</summary>
    public async Task<OnlineCandidate?> ByIsbnAsync(string isbn, CancellationToken ct)
    {
        var url = "https://openlibrary.org/search.json?isbn=" + Uri.EscapeDataString(isbn) + "&limit=1&fields=" + Fields +
                  ",editions,editions.key,editions.title,editions.publisher,editions.publish_date,editions.cover_i";
        using var doc = await GetJsonAsync(url, ct);
        return Docs(doc.RootElement, byIsbn: true).FirstOrDefault();
    }

    /// <summary>Books whose title (and author, when given) match.</summary>
    public async Task<List<OnlineCandidate>> SearchAsync(string title, string? author, int limit, CancellationToken ct)
    {
        var url = "https://openlibrary.org/search.json?title=" + Uri.EscapeDataString(title) +
                  (string.IsNullOrWhiteSpace(author) ? "" : "&author=" + Uri.EscapeDataString(author)) +
                  $"&limit={limit}&fields=" + Fields;
        using var doc = await GetJsonAsync(url, ct);
        return Docs(doc.RootElement, byIsbn: false).ToList();
    }

    /// <summary>The work's description, or null when it has none.</summary>
    public async Task<string?> DescriptionAsync(string workKey, CancellationToken ct)
    {
        if (!workKey.StartsWith("/works/", StringComparison.Ordinal)) return null;
        using var doc = await GetJsonAsync("https://openlibrary.org" + workKey + ".json", ct);
        if (!doc.RootElement.TryGetProperty("description", out var d)) return null;
        var text = d.ValueKind == JsonValueKind.String ? d.GetString()
            : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("value", out var v) ? v.GetString() : null;
        return CleanDescription(text);
    }

    /// <summary>A cover image by its cover id (not rate limited, unlike covers by ISBN), or null.</summary>
    public async Task<byte[]?> CoverAsync(long coverId, CancellationToken ct)
    {
        var url = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                return bytes.Length > 1000 ? bytes : null;   // Open Library's 1×1 "no cover" image
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt >= 2) throw new OnlineUnavailableException("The cover could not be downloaded.", ex);
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitTurnAsync(ct);
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"Open Library answered {(int)resp.StatusCode}", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt >= 2) throw new OnlineUnavailableException("Open Library didn't answer.", ex);
                var slow = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
                await Task.Delay(slow ? 10000 : 2000, ct);
            }
        }
    }

    /// <summary>At most one request a second, whoever asks (the background fill or the Find online window).</summary>
    private async Task WaitTurnAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest.AddSeconds(1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IEnumerable<OnlineCandidate> Docs(JsonElement root, bool byIsbn)
    {
        if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var d in docs.EnumerateArray())
        {
            var title = Str(d, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var publishers = Strs(d, "publisher");
            var year = Int(d, "first_publish_year");
            var publisher = publishers.Count == 1 ? publishers[0] : null;
            long? cover = Long(d, "cover_i");

            // An ISBN search also returns the edition with that ISBN: its publisher and year are this book's.
            if (d.TryGetProperty("editions", out var eds) && eds.TryGetProperty("docs", out var edDocs) &&
                edDocs.ValueKind == JsonValueKind.Array && edDocs.GetArrayLength() > 0)
            {
                var ed = edDocs[0];
                publisher = Strs(ed, "publisher").FirstOrDefault() ?? publisher;
                var date = Strs(ed, "publish_date").FirstOrDefault();
                if (date is not null && Regex.Match(date, @"\b(1[5-9]|20)\d\d\b") is { Success: true } m)
                    year = int.Parse(m.Value, CultureInfo.InvariantCulture);
                cover = Long(ed, "cover_i") ?? cover;
            }

            yield return new OnlineCandidate(
                Str(d, "key") ?? "", title.Trim(), Str(d, "subtitle"), Strs(d, "author_name"), year,
                publisher, cover, Strs(d, "isbn"), Int(d, "edition_count") ?? 1, CleanSubjects(Strs(d, "subject")), byIsbn);
        }
    }

    /// <summary>Readable subjects only: Open Library mixes in codes like "Com051170" and "Cs.cmp_sc.app_sw".</summary>
    private static List<string> CleanSubjects(List<string> subjects) => subjects
        .Select(s => s.Trim())
        .Where(s => s.Length is >= 3 and <= 40 && !s.Any(char.IsDigit) && !s.Contains('.') && !s.Contains('_') &&
                    !s.Contains('=') && !s.StartsWith("nyt:", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(s => s.ToLowerInvariant())
        .Take(6)
        .ToList();

    /// <summary>Descriptions often end in a "----------" block of links, or carry Markdown links.</summary>
    private static string? CleanDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cut = Regex.Match(text, @"\n\s*-{3,}");
        if (cut.Success) text = text[..cut.Index];
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");   // [label](url) → label
        text = Regex.Replace(text, @"\([Ss]ource: [^)]*\)", "");
        text = text.Replace("\r\n", "\n").Trim();
        return text.Length > 0 ? text : null;
    }

    // Some records carry HTML entities: "Zbyn&#283;k K&#345;ivka".
    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? WebUtility.HtmlDecode(v.GetString()) : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) && i > 0 ? i : null;

    private static List<string> Strs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => WebUtility.HtmlDecode(x.GetString()!)).ToList()
            : new List<string>();
}
