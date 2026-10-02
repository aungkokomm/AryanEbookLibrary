using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;

namespace AryanEbookLibrary;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // Every failure reaches the log. One on the window's thread is handled so Aryan carries on, and the user is told
        // once; one on another thread ends the app, and the next start says so (Session).
        UnhandledException += (_, e) =>
        {
            Log.Write("UnhandledException: " + e.Exception);
            e.Handled = true;
            if (_told) return;
            _told = true;
            AppServices.Library?.Tell("Something went wrong, and Aryan carried on. The details are in aryan.log in the data folder.");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Crash: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("UnobservedTaskException: " + e.Exception);
            e.SetObserved();
        };
    }

    private static bool _told;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Init();
        Log.Write("app: started " + typeof(App).Assembly.GetName().Version?.ToString(3));
        Session.Begin();
        AppServices.Library = new LibraryViewModel();
        BookLauncher.InAppReader = book => Reader.ReaderWindow.Open(book);

        // The colour theme before the first window: every brush made from the accent then starts in its colour.
        // (Not in the constructor: the app's resources cannot be reached there yet.)
        try
        {
            ColorTheme.Apply();
        }
        catch (Exception ex)
        {
            Log.Write("Colour theme not applied: " + ex.Message);
        }

        MainWindow = new MainWindow();
        AppServices.ApplyTheme();
        MainWindow.Activate();
    }
}
