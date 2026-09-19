using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;

namespace AryanEbookLibrary.Services;

/// <summary>Simple service locator, same pragmatic approach a small portable desktop app needs.</summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = new();
    public static Database Db { get; private set; } = null!;
    public static LibraryRepository Repo { get; private set; } = null!;
    public static ScannerService Scanner { get; private set; } = null!;
    public static StateSyncService Sync { get; private set; } = null!;
    public static LibraryViewModel Library { get; set; } = null!;

    public static void Init()
    {
        AppPaths.Init();
        Settings = AppSettings.Load();
        Db = new Database(AppPaths.DbFile);
        Repo = new LibraryRepository(Db);
        Sync = new StateSyncService(Repo);
        Scanner = new ScannerService(Repo);
    }

    public static void ApplyTheme()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = Settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    public static void Shutdown()
    {
        try
        {
            Sync.FlushAll();
            Settings.Save();
            Db.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("Shutdown: " + ex.Message);
        }
    }
}
