namespace AryanEbookLibrary.Services;

/// <summary>
/// The version arithmetic behind the update check, kept apart from the network so it can be tested (as in Ayaan PDF).
/// </summary>
public static class UpdateVersions
{
    /// <summary>A release tag as a plain version: "v1.2.0" becomes "1.2.0".</summary>
    public static string Normalize(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

    /// <summary>
    /// Whether latest is a later version than current. False when either cannot be read: a tag nobody can compare,
    /// or a build that reports no version, is not a reason to tell the user to update.
    /// </summary>
    public static bool IsNewer(string latest, string current) =>
        TryParse(latest, out var a) && TryParse(current, out var b) && a > b;

    // "1.2" and "1.2.0" are the same release, so a short version is padded to three parts before it is compared.
    private static bool TryParse(string text, out Version version)
    {
        var padded = text.Split('.').Length switch
        {
            1 => text + ".0.0",
            2 => text + ".0",
            _ => text,
        };
        return Version.TryParse(padded, out version!);
    }
}
