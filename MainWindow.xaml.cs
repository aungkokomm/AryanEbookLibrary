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
            case "needs": ShowLibrary(LibraryFilter.NeedsDetails); break;
            case "authors": Navigate(typeof(AuthorsPage)); break;
            case "series": Navigate(typeof(SeriesPage)); break;
            case "tags": Navigate(typeof(TagsPage)); break;
            case "duplicates": Navigate(typeof(DuplicatesPage)); break;
            case "missing": Navigate(typeof(MissingPage)); break;
            case "drives": Navigate(typeof(DrivesPage)); break;
        }
    }

    private void ShowLibrary(LibraryFilter filter)
    {
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.Filter = filter;
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Shows every book naming this person (from the Authors page).</summary>
    public void ShowBooksBy(string author)
    {
        Library.SeriesFilter = "";
        Library.TagFilter = "";
        Library.Filter = LibraryFilter.All;
        Library.SearchText = author;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Shows one series, its numbered books in order (from the Series page).</summary>
    public void ShowSeries(string series)
    {
        Library.Filter = LibraryFilter.All;
        Library.SearchText = "";
        Library.SeriesFilter = series;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    /// <summary>Shows every book wearing one tag (from the Tags page).</summary>
    public void ShowTag(string tag)
    {
        Library.Filter = LibraryFilter.All;
        Library.SearchText = "";
        Library.TagFilter = tag;
        SelectNav("all");
        Navigate(typeof(LibraryPage));
    }

    private void SelectNav(string tag) =>
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);

    private void Navigate(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => Library.CancelScan();
}
