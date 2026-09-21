using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AryanEbookLibrary.Services.Online;

/// <summary>A Wikipedia article about a book: its opening paragraphs and its main picture (usually the cover).</summary>
public sealed record WikipediaArticle(string Title, string Summary, string? ImageUrl);

/// <summary>
/// English Wikipedia, asked only for a page Wikidata already tied to the book, so there is no risk of
/// landing on a film of the same name. Gives what Open Library usually lacks: a real description (only
/// 10 of 47 matched books had one there) and often the cover. Text is CC BY-SA, so it is shown as
/// "from Wikipedia".
/// </summary>
public sealed class WikipediaClient
{
    private const string Api = "https://en.wikipedia.org/w/api.php";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public WikipediaClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var version = typeof(WikipediaClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AryanEbookLibrary/{version} (Windows ebook catalog)");
    }

    public async Task<WikipediaArticle?> ArticleAsync(string pageTitle, CancellationToken ct)
    {
        var url = Api + "?action=query&prop=extracts|pageimages&exintro=1&explaintext=1&piprop=original" +
                  "&redirects=1&format=json&formatversion=2&titles=" + Uri.EscapeDataString(pageTitle);
        using var doc = await GetJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) || pages.GetArrayLength() == 0) return null;

        var page = pages[0];
        if (page.TryGetProperty("missing", out _)) return null;
        var title = page.TryGetProperty("title", out var t) ? t.GetString() ?? pageTitle : pageTitle;
        var extract = page.TryGetProperty("extract", out var e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(extract)) return null;

        var image = page.TryGetProperty("original", out var o) && o.TryGetProperty("source", out var src)
            ? src.GetString() : null;
        return new WikipediaArticle(title, Shorten(extract), image);
    }

    public async Task<byte[]?> ImageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 1000 ? bytes : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new OnlineUnavailableException("The picture could not be downloaded.", ex);
        }
    }

    /// <summary>The first paragraphs, without the pronunciation and script asides articles open with.</summary>
    private static string Shorten(string extract)
    {
        extract = Regex.Replace(extract, @"\s*\([^()]*?(Hebrew|Arabic|Sanskrit|Hindi|Burmese|Chinese|Japanese|Russian|Greek|pronounced)[^()]*\)", "");
        extract = Regex.Replace(extract, @"\n{2,}", "\n\n").Trim();
        var paragraphs = extract.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join("\n\n", paragraphs.Take(3));
        return text.Length > 1600 ? text[..1600].TrimEnd() + "..." : text;
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
                    throw new HttpRequestException($"Wikipedia answered {(int)resp.StatusCode}", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt >= 2) throw new OnlineUnavailableException("Wikipedia didn't answer.", ex);
                await Task.Delay(2000, ct);
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
}
