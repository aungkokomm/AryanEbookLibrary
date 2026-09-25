namespace AryanEbookLibrary.Services;

/// <summary>
/// Whether the last session ended normally. A marker file is written when Aryan starts and removed when it closes
/// (or when Windows ends the session with Aryan open). Left behind, it means Aryan stopped without closing: a crash no
/// handler could catch, a kill, or the power going.
/// </summary>
public static class Session
{
    private static string Marker => Path.Combine(AppPaths.DataDir, "aryan.running");

    /// <summary>True when the last session did not close normally.</summary>
    public static bool LastEndedBadly { get; private set; }

    public static void Begin()
    {
        LastEndedBadly = File.Exists(Marker);
        if (LastEndedBadly) Log.Write("app: the last session did not close normally");
        try { File.WriteAllText(Marker, DateTime.Now.ToString("o")); } catch { /* no marker: nothing to report next time */ }
    }

    public static void End()
    {
        try { File.Delete(Marker); } catch { /* reported next time, harmlessly */ }
    }
}
