using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AryanEbookLibrary.Services.Online;

/// <summary>One work by an author, as Wikidata records it.</summary>
public sealed record WikidataWork(string Key, string Title, int? Year, string? Series, double? SeriesIndex, string? WikipediaTitle);

/// <summary>
/// Wikidata, asked once per author rather than once per book: one query returns the author's whole
/// catalogue with publication years, series names and numbers (P179 with the P1545 ordinal, the cleanest
/// structured series there is) and the English Wikipedia page of each work. Free, no key, CC0 data.
/// Author matching is deliberately careful: a person only counts when Wikidata says they write
/// (Colophon anchored a science fiction author to a snooker player by skipping that test).
/// </summary>
public sealed class WikidataClient
{
    private const string Sparql = "https://query.wikidata.org/sparql";
    private const string Human = "Q5";

    /// <summary>Occupations that mean "this person writes things other people read" (Colophon's list).</summary>
    private static readonly HashSet<string> WritingWork = new(StringComparer.Ordinal)
    {
        "Q36180",    // writer
        "Q482980",   // author
        "Q6625963",  // novelist
        "Q49757",    // poet
        "Q214917",   // playwright
        "Q1930187",  // journalist
        "Q11774202", // essayist
        "Q4964182",  // philosopher
        "Q201788",   // historian
        "Q1234713",  // theologian
        "Q1622272",  // university teacher
        "Q901",      // scientist
        "Q170790",   // mathematician
        "Q169470",   // physicist
        "Q593644",   // chemist
        "Q39631",    // physician
        "Q188094",   // economist
        "Q4773904",  // anthropologist
        "Q2306091",  // sociologist
        "Q212980",   // psychologist
        "Q333634",   // translator
        "Q1114448",  // comics writer
        "Q3400985",  // academic
        "Q15980158", // non-fiction writer
        "Q11499147", // spiritual teacher
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public WikidataClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };   // the query service can be slow
        var version = typeof(WikidataClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AryanEbookLibrary/{version} (Windows ebook catalog)");
    }

    /// <summary>
    /// The Wikidata id of a person with this name who writes, or null when there is no sure one. One query
    /// does search, "is a human" and "writes for a living" together: asking the ordinary API instead needed
    /// a claims fetch per candidate, which Open Library-style rates answered with 429.
    /// </summary>
    public async Task<string?> FindAuthorAsync(string name, CancellationToken ct)
    {
        var occupations = string.Join(" ", WritingWork.Select(q => "wd:" + q));
        var query = $$"""
            SELECT ?author ?authorLabel WHERE {
              SERVICE wikibase:mwapi {
                bd:serviceParam wikibase:api "EntitySearch" .
                bd:serviceParam wikibase:endpoint "www.wikidata.org" .
                bd:serviceParam mwapi:search "{{Escape(name)}}" .
                bd:serviceParam mwapi:language "en" .
                ?author wikibase:apiOutputItem mwapi:item .
              }
              ?author wdt:P31 wd:{{Human}} ; wdt:P106 ?occ .
              VALUES ?occ { {{occupations}} }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en" }
            } LIMIT 10
            """;
        using var doc = await GetJsonAsync(Sparql + "?format=json&query=" + Uri.EscapeDataString(query), ct);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("bindings", out var rows)) return null;

        var wanted = OnlineMatcher.Tokens(name).Where(t => t.Length >= 3).ToHashSet();
        if (wanted.Count == 0) return null;
        foreach (var r in rows.EnumerateArray())
        {
            var id = Value(r, "author");
            var label = Value(r, "authorLabel");
            if (id is null || label is null) continue;
            if (!wanted.IsSubsetOf(OnlineMatcher.Tokens(label))) continue;   // every word of the name must be in theirs
            return id[(id.LastIndexOf('/') + 1)..];
        }
        return null;
    }

    private static string Escape(string s) => s.Replace("\\", " ").Replace("\"", " ").Trim();

    /// <summary>Everything Wikidata says this author wrote (their catalogue, in one query).</summary>
    public async Task<List<WikidataWork>> WorksByAuthorAsync(string qid, CancellationToken ct)
    {
        var query = $$"""
            SELECT ?work ?workLabel ?seriesLabel ?ordinal ?pub ?sitelink WHERE {
              ?work wdt:P50 wd:{{qid}} .
              OPTIONAL { ?work p:P179 ?st . ?st ps:P179 ?series . OPTIONAL { ?st pq:P1545 ?ordinal } }
              OPTIONAL { ?work wdt:P577 ?pub }
              OPTIONAL { ?sitelink schema:about ?work ; schema:isPartOf <https://en.wikipedia.org/> }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en" }
            } LIMIT 200
            """;
        var url = Sparql + "?format=json&query=" + Uri.EscapeDataString(query);
        using var doc = await GetJsonAsync(url, ct);

        var works = new List<WikidataWork>();
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("bindings", out var rows)) return works;

        foreach (var r in rows.EnumerateArray())
        {
            var key = Value(r, "work");
            var title = Value(r, "workLabel");
            if (key is null || title is null || title.StartsWith('Q')) continue;   // an item with no English label
            var page = Value(r, "sitelink") is { } link && link.Contains("/wiki/")
                ? Uri.UnescapeDataString(link[(link.LastIndexOf("/wiki/", StringComparison.Ordinal) + 6)..]).Replace('_', ' ')
                : null;
            works.Add(new WikidataWork(
                key[(key.LastIndexOf('/') + 1)..], title,
                Value(r, "pub") is { Length: >= 4 } p && int.TryParse(p[..4], out var y) ? y : null,
                Value(r, "seriesLabel") is { } s && !s.StartsWith('Q') ? s : null,
                Value(r, "ordinal") is { } o && double.TryParse(o, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null,
                page));
        }
        return works;
    }

    private static string? Value(JsonElement row, string name) =>
        row.TryGetProperty(name, out var b) && b.TryGetProperty("value", out var v) ? v.GetString() : null;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await WaitTurnAsync(ct);
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"Wikidata answered {(int)resp.StatusCode}", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt >= 2) throw new OnlineUnavailableException("Wikidata didn't answer.", ex);
                await Task.Delay(ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? 30000 : 3000, ct);
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
