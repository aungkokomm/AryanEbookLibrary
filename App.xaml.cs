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
        UnhandledException += (_, e) =>
        {
            Log.Write("UnhandledException: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Init();
        AppServices.Library = new LibraryViewModel();

        MainWindow = new MainWindow();
        AppServices.ApplyTheme();
        MainWindow.Activate();
    }
}
