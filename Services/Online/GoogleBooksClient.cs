using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Services.Online;

/// <summary>A catalogue a book can be looked up in by ISBN or by title and author: Open Library, Google Books.</summary>
public interface IBookCatalogue
{
    Task<OnlineCandidate?> ByIsbnAsync(string isbn, CancellationToken ct);
    Task<List<OnlineCandidate>> SearchAsync(string title, string? author, int limit, CancellationToken ct);
}

/// <summary>Google Books will not answer more today: its daily allowance for the key is used up.</summary>
public sealed class GoogleQuotaException(string message) : Exception(message);

/// <summary>
/// Google Books' public API, with the user's own key (without one Google refuses: every keyless request in the world
/// shares one allowance, which is always used up). Its records have descriptions and categories where Open Library's
/// mostly do not. One request a second, a 10 second connection timeout and one retry; only the title, author or ISBN
/// being looked up is sent, with the key.
/// </summary>
public sealed class GoogleBooksClient : IBookCatalogue
{
    private const string Fields =
        "items(id,volumeInfo(title,subtitle,authors,publisher,publishedDate,description,categories,industryIdentifiers,language))";

    private readonly HttpClient _http;
    private readonly Func<string> _key;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public GoogleBooksClient(Func<string> key)
    {
        _key = key;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var version = typeof(GoogleBooksClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AryanEbookLibrary/{version} (Windows ebook catalog)");
    }

    /// <summary>A key has been given in Settings.</summary>
    public bool HasKey => _key().Trim().Length > 0;

    public async Task<OnlineCandidate?> ByIsbnAsync(string isbn, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("isbn:" + isbn, 1, ct);
        return Volumes(doc.RootElement, byIsbn: true).FirstOrDefault();
    }

    public async Task<List<OnlineCandidate>> SearchAsync(string title, string? author, int limit, CancellationToken ct)
    {
        var q = "intitle:" + Quote(title) + (string.IsNullOrWhiteSpace(author) ? "" : " inauthor:" + Quote(author));
        using var doc = await GetJsonAsync(q, limit, ct);
        return Volumes(doc.RootElement, byIsbn: false).ToList();
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s.Replace("\"", "") + "\"" : s;

    private async Task<JsonDocument> GetJsonAsync(string q, int limit, CancellationToken ct)
    {
        var url = "https://www.googleapis.com/books/v1/volumes?q=" + Uri.EscapeDataString(q) +
                  $"&maxResults={Math.Clamp(limit, 1, 20)}&printType=books&fields=" + Uri.EscapeDataString(Fields) +
                  "&key=" + Uri.EscapeDataString(_key().Trim());
        for (var attempt = 1; ; attempt++)
        {
            await WaitTurnAsync(ct);
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    // "Queries per day": nothing more today. Anything else is a moment's rate limit, worth one retry.
                    if (body.Contains("per day", StringComparison.OrdinalIgnoreCase))
                        throw new GoogleQuotaException("Google Books' daily allowance for this key is used up.");
                    throw new HttpRequestException("Google Books answered 429", null, resp.StatusCode);
                }
                if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
                    throw new GoogleQuotaException("Google Books did not accept the key (" + (int)resp.StatusCode + ").");
                if ((int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"Google Books answered {(int)resp.StatusCode}", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt >= 2) throw new OnlineUnavailableException("Google Books didn't answer.", ex);
                var slow = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
                await Task.Delay(slow ? 10000 : 2000, ct);
            }
        }
    }

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

    /// <summary>
    /// Google's volumes as candidates. Google ranks its answers, so the first ranks highest where the matcher prefers
    /// the most editions; its categories are the subjects and its description comes with the answer.
    /// </summary>
    public static IEnumerable<OnlineCandidate> Volumes(JsonElement root, bool byIsbn)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        var rank = items.GetArrayLength();
        foreach (var item in items.EnumerateArray())
        {
            rank--;
            if (!item.TryGetProperty("volumeInfo", out var v)) continue;
            var title = Str(v, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            int? year = Str(v, "publishedDate") is { } date && Regex.Match(date, @"^(1[5-9]|20)\d\d") is { Success: true } m
                ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
            var isbns = new List<string>();
            if (v.TryGetProperty("industryIdentifiers", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var id in ids.EnumerateArray())
                    if (Str(id, "type") is "ISBN_13" or "ISBN_10" && Str(id, "identifier") is { } n) isbns.Add(n);
            yield return new OnlineCandidate(
                "gb:" + (Str(item, "id") ?? ""), title.Trim(), Str(v, "subtitle"), Strs(v, "authors"), year,
                Str(v, "publisher"), null, isbns, rank + 1, Categories(Strs(v, "categories")), byIsbn,
                Description(Str(v, "description")));
        }
    }

    /// <summary>"Computers / Security / General" reads "Computers, Security"; the same one twice once.</summary>
    private static List<string> Categories(List<string> categories) => categories
        .SelectMany(c => c.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Where(c => c.Length >= 3 && !c.Equals("General", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(c => c.ToLowerInvariant())
        .Take(6)
        .ToList();

    /// <summary>Google sends descriptions as HTML: paragraphs become lines, the tags go.</summary>
    private static string? Description(string? html) =>
        string.IsNullOrWhiteSpace(html) || XmlUtil.StripHtml(html) is not { Length: > 0 } text ? null : text;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
}
