using System.Net.Http.Headers;
using System.Text.Json;

namespace AryanEbookLibrary.Services;

/// <summary>A release newer than the running build.</summary>
public sealed record UpdateInfo(string Version, string ReleaseUrl);

/// <summary>
/// Asks GitHub once, at start-up, whether a newer Aryan has been released, as Ayaan PDF does. It only asks, and sends
/// nothing about the user or their books: one request for the repository's public "latest release", the page anyone
/// can open in a browser. Nothing is downloaded or installed; the bubble it leads to opens the release page. And every
/// failure is silence: no network, a proxy, GitHub's rate limit or a reply it cannot read all mean "nothing to say".
/// </summary>
public static class UpdateChecker
{
    private const string LatestRelease = "https://api.github.com/repos/aungkokomm/AryanEbookLibrary/releases/latest";

    private static readonly string Version = typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "";

    // ARYAN_PRETEND_VERSION makes this build compare itself as an older one, so the bubble can be seen against the
    // real latest release.
    private static string Current =>
        Environment.GetEnvironmentVariable("ARYAN_PRETEND_VERSION") is { Length: > 0 } pretend ? pretend : Version;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub refuses a request with no User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AryanEbookLibrary", Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// The newer release, or null when this build is the latest, when that release is the one the user chose to skip,
    /// or when GitHub could not be asked.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string? skippedVersion)
    {
        try
        {
            using var response = await Http.GetAsync(LatestRelease).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"update: GitHub answered {(int)response.StatusCode}");
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var tag = json.RootElement.GetProperty("tag_name").GetString();
            var page = json.RootElement.GetProperty("html_url").GetString();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(page)) return null;

            var latest = UpdateVersions.Normalize(tag);
            if (!UpdateVersions.IsNewer(latest, Current))
            {
                Log.Write($"update: {Current} is up to date (latest {latest})");
                return null;
            }
            if (string.Equals(latest, skippedVersion, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"update: {latest} is out, and the user chose to skip it");
                return null;
            }
            Log.Write($"update: {latest} is out, this is {Current}");
            return new UpdateInfo(latest, page);
        }
        catch (Exception ex)
        {
            Log.Write($"update: could not check ({ex.GetType().Name})");
            return null;
        }
    }
}
