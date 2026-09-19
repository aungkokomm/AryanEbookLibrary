using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AryanEbookLibrary.Views;

public sealed partial class DrivesPage : Page
{
    private LibraryViewModel Library => AppServices.Library;

    public DrivesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Library.DriveStatusChanged += OnDriveStatusChanged;
            Reload();
        };
        Unloaded += (_, _) => Library.DriveStatusChanged -= OnDriveStatusChanged;
    }

    private void OnDriveStatusChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Reload);

    private void Reload()
    {
        var repo = AppServices.Repo;
        var counts = repo.CountBooksByFolder();
        var folders = repo.GetFolders();

        var items = new List<DriveItem>();
        foreach (var drive in repo.GetDrives())
        {
            var online = DriveRegistry.IsOnline(drive.Id);
            var root = DriveRegistry.GetRoot(drive.Id) ?? drive.LastRoot;

            var item = new DriveItem
            {
                Id = drive.Id,
                Label = drive.Label,
                IsOnline = online,
                Root = online ? root : $"last seen as {root}",
                Folders = folders.Where(f => f.DriveId == drive.Id).Select(f => new FolderItem
                {
                    Folder = f,
                    DisplayPath = string.IsNullOrEmpty(f.RelPath) ? "(entire drive)" : f.RelPath,
                    BookCount = counts.TryGetValue(f.Id, out var n) ? n : 0
                }).ToList()
            };
            if (item.Folders.Count > 0) items.Add(item);
        }

        DriveList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var error = await Library.AddFolderAsync(folder.Path);
        if (error.Length > 0)
        {
            await new ContentDialog
            {
                Title = "Cannot add folder",
                Content = error,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
        Reload();
    }

    private async void OnRescanAll(object sender, RoutedEventArgs e)
    {
        await Library.ScanAsync();
        Reload();
    }

    private async void OnRescanFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FolderItem item) return;
        await Library.ScanAsync(new[] { item.Folder });
        Reload();
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FolderItem item) return;

        var dialog = new ContentDialog
        {
            Title = "Remove folder from library?",
            Content = $"\"{item.DisplayPath}\" and its {item.BookCountText} will be removed from the catalog. " +
                      "Your files are not touched, and favorites/notes are kept in case you add the folder again.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Library.RemoveFolder(item.Folder.Id);
        Reload();
    }

    private void OnDriveLabelLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DriveItem drive)
            Library.RenameDrive(drive.Id, drive.Label);
    }
}
