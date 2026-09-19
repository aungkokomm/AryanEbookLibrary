using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using AryanEbookLibrary.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary;

public sealed partial class MainWindow : Window
{
    public LibraryViewModel Library => AppServices.Library;
    private readonly DeviceChangeWatcher _deviceWatcher;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Aryan eBook Library";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));

        // Event-driven drive detection (WM_DEVICECHANGE) instead of a poll timer, as in CineLibrary.
        // Don't await inside the WndProc: it must return promptly.
        _deviceWatcher = new DeviceChangeWatcher(WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => _ = Library.OnDeviceChangeAsync());

        Closed += (_, _) =>
        {
            _deviceWatcher.Dispose();
            AppServices.Shutdown();
        };

        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(LibraryPage));
        Library.Initialize();
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        switch (args.InvokedItemContainer?.Tag as string)
        {
            case "all": ShowLibrary(LibraryFilter.All); break;
            case "continue": ShowLibrary(LibraryFilter.ContinueReading); break;
            case "recent": ShowLibrary(LibraryFilter.RecentlyAdded); break;
            case "favorites": ShowLibrary(LibraryFilter.Favorites); break;
            case "unread": ShowLibrary(LibraryFilter.Unread); break;
            case "finished": ShowLibrary(LibraryFilter.Finished); break;
            case "drives": Navigate(typeof(DrivesPage)); break;
        }
    }

    private void ShowLibrary(LibraryFilter filter)
    {
        Library.Filter = filter;
        Navigate(typeof(LibraryPage));
    }

    private void Navigate(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => Library.CancelScan();
}
