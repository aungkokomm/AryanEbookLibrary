namespace AryanEbookLibrary.Services;

/// <summary>Tiny local-only log file (no telemetry, nothing leaves the machine).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static void Write(string message)
    {
        try
        {
            if (string.IsNullOrEmpty(AppPaths.DataDir)) return;
            lock (Gate)
            {
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
