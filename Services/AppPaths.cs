namespace AryanEbookLibrary.Services;

/// <summary>
/// Portable data location: "AryanLibrary-Data" next to the exe (like CineLibrary-Data).
/// Falls back to %LocalAppData% if the exe folder is read-only (e.g. Program Files).
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static string Covers => Path.Combine(DataDir, "Covers");
    public static string DbFile => Path.Combine(DataDir, "library.db");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "aryan.log");

    public static void Init()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "AryanLibrary-Data");
        DataDir = TryUse(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AryanEbookLibrary");

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Covers);
    }

    private static bool TryUse(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
