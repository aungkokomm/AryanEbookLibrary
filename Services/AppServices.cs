using AryanEbookLibrary.Services.Online;
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
    public static OnlineLookupService Online { get; private set; } = null!;
    public static ReadingSessions Sessions { get; private set; } = null!;
    public static LibraryViewModel Library { get; set; } = null!;

    public static void Init()
    {
        AppPaths.Init();
        Settings = AppSettings.Load();
        Db = new Database(AppPaths.DbFile);
        Repo = new LibraryRepository(Db);
        Sync = new StateSyncService(Repo);
        Scanner = new ScannerService(Repo);
        Online = new OnlineLookupService(Repo);
        Sessions = new ReadingSessions(Db);
    }

    /// <summary>Raised after the theme setting is applied, so windows other than the main one follow it.</summary>
    public static event Action? ThemeChanged;

    /// <summary>The theme the app's windows should use, from the setting.</summary>
    public static ElementTheme Theme => Settings.Theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    public static void ApplyTheme()
    {
        if (App.MainWindow?.Content is FrameworkElement root) root.RequestedTheme = Theme;
        ThemeChanged?.Invoke();
    }

    public static void Shutdown()
    {
        try
        {
            Online.Stop();
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
