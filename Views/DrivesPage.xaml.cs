using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using AryanEbookLibrary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Drives and their folders, CineLibrary style: Add Drive registers a drive (no scan), "+ Add folder" on a
/// drive card adds a folder on that drive and scans it, Update rescans one drive, Refresh changes rescans
/// every connected drive. Scans run under a full-page overlay with live counts and a Cancel button.
/// Every action reports failures in a dialog.
/// </summary>
public sealed partial class DrivesPage : Page
{
    /// <summary>Navigation parameter: open the folder picker as soon as the page shows (the empty library's button).</summary>
    public const string PickFolderFirst = "pick-folder";

    private LibraryViewModel Library => AppServices.Library;
    private List<DriveItem> _drives = new();
    private bool _pickOnLoad;

    public DrivesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Library.DriveStatusChanged += OnDriveStatusChanged;
            Refresh();
            if (_pickOnLoad)
            {
                _pickOnLoad = false;
                OnAddAnyFolder(this, new RoutedEventArgs());
            }
        };
        Unloaded += (_, _) => Library.DriveStatusChanged -= OnDriveStatusChanged;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) =>
        _pickOnLoad = e.Parameter as string == PickFolderFirst;

    private void OnDriveStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        try
        {
            _drives = Library.GetDriveCards();
            DrivesRepeater.ItemsSource = _drives;
            EmptyState.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Write("Drives page refresh failed: " + ex);
            _ = ShowInfoDialog("Error", "Could not read the drive list: " + ex.Message);
        }
    }

    // ── Add drive (no scan, just register it) ────────────────────────────

    private async void OnAddDrive(object sender, RoutedEventArgs e)
    {
        try { await DoAddDriveAsync(); }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    private async Task DoAddDriveAsync()
    {
        var addable = await Task.Run(Library.GetAddableDrives);
        if (addable.Count == 0)
        {
            await ShowInfoDialog("All drives added", "Every connected drive is already in your library. Connect another drive and try again.");
            return;
        }

        var combo = new ComboBox { MinWidth = 320 };
        foreach (var d in addable) combo.Items.Add(d.Label);
        combo.SelectedIndex = 0;

        var nameBox = new TextBox { Text = addable[0].Label, PlaceholderText = "Drive name (e.g. WD Passport 2TB)" };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) nameBox.Text = addable[combo.SelectedIndex].Label;
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Select drive:", FontSize = 13, Opacity = 0.7 });
        panel.Children.Add(combo);
        panel.Children.Add(new TextBlock { Text = "Drive name:", FontSize = 13, Opacity = 0.7 });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock
        {
            Text = "After adding, use “+ Add folder” on the drive card to pick which folders to index. Subfolders are included.",
            FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "Add Drive",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;
        if (combo.SelectedIndex < 0) return;

        var (id, root, label) = addable[combo.SelectedIndex];
        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? label : nameBox.Text.Trim();
        await Library.AddDriveAsync(id, name, root);
        Refresh();
    }

    // ── Add folder to a drive (scans it right away) ──────────────────────

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string driveId) return;
            if (await BlockedByScan()) return;
            if (DriveRegistry.GetRoot(driveId) is null)
            {
                await ShowInfoDialog("Drive offline", "Connect this drive to add or scan folders on it.");
                return;
            }

            var folder = await PickFolderAsync();
            if (folder is null) return;

            // Picked a folder on another drive (easy to do from the picker): say where it really is and offer
            // to add it there, instead of only refusing.
            if (DriveRegistry.Identify(folder.Path) is { } owner && !string.Equals(owner.Id, driveId, StringComparison.OrdinalIgnoreCase))
            {
                await OfferOtherDriveAsync(folder.Path, owner, driveId);
                return;
            }

            var problem = Library.CheckNewFolder(driveId, folder.Path, out var relPath);
            if (problem is not null)
            {
                await ShowInfoDialog("Cannot add this folder", problem);
                return;
            }

            var scope = relPath.Length == 0 ? DriveLabel(driveId) : "…\\" + Path.GetFileName(relPath);
            await RunScanAsync(scope, p => Library.AddFolderAsync(driveId, relPath, p));
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    private static async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        return await picker.PickSingleFolderAsync();
    }

    // ── Add a folder anywhere: its drive comes in with it when it is new (the way a new library starts) ──

    private async void OnAddAnyFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await BlockedByScan()) return;
            var folder = await PickFolderAsync();
            if (folder is null) return;
            if (DriveRegistry.Identify(folder.Path) is not { } owner)
            {
                await ShowInfoDialog("Cannot add this folder", $"Aryan could not tell which drive '{folder.Path}' is on.");
                return;
            }

            var firstBooks = Library.TotalCount == 0;
            if (!_drives.Any(d => string.Equals(d.Id, owner.Id, StringComparison.OrdinalIgnoreCase)))
            {
                await Library.AddDriveAsync(owner.Id, owner.Label, owner.Root);
                Refresh();
            }

            var problem = Library.CheckNewFolder(owner.Id, folder.Path, out var relPath);
            if (problem is not null)
            {
                await ShowInfoDialog("Cannot add this folder", problem);
                return;
            }

            var scope = relPath.Length == 0 ? DriveLabel(owner.Id) : "…\\" + Path.GetFileName(relPath);
            await RunScanAsync(scope, p => Library.AddFolderAsync(owner.Id, relPath, p));
            // A library's first books: show them, which is what the button was pressed for.
            if (firstBooks && Library.TotalCount > 0) App.MainWindow?.ShowAllBooks();
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    private async Task OfferOtherDriveAsync(string path, (string Id, string Root, string Label) owner, string pickedOnDriveId)
    {
        var known = _drives.FirstOrDefault(d => string.Equals(d.Id, owner.Id, StringComparison.OrdinalIgnoreCase));
        var dialog = new ContentDialog
        {
            Title = "That folder is on another drive",
            Content = $"'{path}' is on {known?.Label ?? owner.Label}, not on {DriveLabel(pickedOnDriveId)}. " +
                      (known is not null ? $"Add it to {known.Label} instead?" : $"Add drive {owner.Label} to the library with this folder?"),
            PrimaryButtonText = known is not null ? $"Add to {known.Label}" : "Add drive and folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

        if (known is null)
        {
            await Library.AddDriveAsync(owner.Id, owner.Label, owner.Root);
            Refresh();
        }

        var problem = Library.CheckNewFolder(owner.Id, path, out var relPath);
        if (problem is not null)
        {
            await ShowInfoDialog("Cannot add this folder", problem);
            return;
        }

        var scope = relPath.Length == 0 ? DriveLabel(owner.Id) : "…\\" + Path.GetFileName(relPath);
        await RunScanAsync(scope, p => Library.AddFolderAsync(owner.Id, relPath, p));
    }

    // ── Remove folder ─────────────────────────────────────────────────────

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not FolderItem item) return;
            if (await BlockedByScan()) return;

            var dialog = new ContentDialog
            {
                Title = "Remove folder?",
                Content = $"Remove '{item.DisplayName}' and its {item.BookCountText} from the library? " +
                          "The files on the drive are not touched, and favorites/notes are kept in case you add the folder again.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

            Library.RemoveFolder(item.Folder.Id);
            Refresh();
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    // ── Update (rescan this drive's folders) / Refresh changes (every connected drive) ──

    private async void OnUpdateDrive(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string driveId) return;
            if (await BlockedByScan()) return;
            if (DriveRegistry.GetRoot(driveId) is null)
            {
                await ShowInfoDialog("Drive offline", "This drive is not connected.");
                return;
            }
            if (_drives.FirstOrDefault(d => d.Id == driveId) is not { HasFolders: true })
            {
                await ShowInfoDialog("No folders", "Add at least one folder to this drive before updating.");
                return;
            }

            await RunScanAsync(DriveLabel(driveId), p => Library.UpdateDriveAsync(driveId, p));
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    private async void OnRefreshChanges(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await BlockedByScan()) return;
            var online = _drives.Where(d => d.IsConnected && d.HasFolders).ToList();
            if (online.Count == 0)
            {
                await ShowInfoDialog("Nothing to refresh",
                    _drives.Any(d => d.IsConnected)
                        ? "None of the connected drives has a folder to scan yet. Add a folder on a drive card first."
                        : "Plug in at least one drive that holds books, then try again.");
                return;
            }

            var scope = online.Count == 1 ? online[0].Label : $"{online.Count} drives";
            await RunScanAsync(scope, Library.RefreshChangesAsync);
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    // ── Scan (shared overlay) ────────────────────────────────────────────

    private async Task RunScanAsync(string scope, Func<IProgress<ScanProgress>, Task<ScanResult>> scan)
    {
        ScanOverlay.Visibility = Visibility.Visible;
        ScanStatusText.Text = $"Scanning {scope}…";
        ScanDetailText.Text = "Preparing…";

        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Done) return;
            ScanStatusText.Text = p.Checked == 0
                ? $"Scanning {scope}… ({p.Found:N0} found)"
                : $"Scanning {scope}… ({p.Checked:N0} of {p.Found:N0})";
            ScanDetailText.Text = Path.GetFileName(p.CurrentFolder.TrimEnd('\\'));
        });

        try
        {
            var result = await scan(progress);
            ScanStatusText.Text = "Done: " + result.Summary;
            ScanDetailText.Text = result.OfflineFolders > 0 ? $"{result.OfflineFolders} folder(s) skipped because their drive is offline" : "";
            await Task.Delay(1200);
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "Scan cancelled";
            ScanDetailText.Text = "";
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            ScanOverlay.Visibility = Visibility.Collapsed;
            await ShowInfoDialog("Scan error", ex.Message);
        }
        finally
        {
            ScanOverlay.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => Library.CancelScan();

    // ── Review missing books ─────────────────────────────────────────────

    private async void OnReviewMissing(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string driveId) return;
            if (await BlockedByScan()) return;
            var missing = AppServices.Repo.GetMissingBooks(driveId);
            if (missing.Count == 0) return;

            var boxes = new List<(CheckBox Box, long Id)>();
            var list = new StackPanel { Spacing = 4 };
            foreach (var m in missing)
            {
                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(m.Author) ? m.Title : $"{m.Title}  ·  {m.Author}",
                    FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                });
                info.Children.Add(new TextBlock
                {
                    Text = m.RelPath, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis
                });
                var box = new CheckBox { Content = info, IsChecked = true };
                boxes.Add((box, m.Id));
                list.Children.Add(box);
            }

            var all = new HyperlinkButton { Content = "Select all" };
            all.Click += (_, _) => { foreach (var (b, _) in boxes) b.IsChecked = true; };
            var none = new HyperlinkButton { Content = "Select none" };
            none.Click += (_, _) => { foreach (var (b, _) in boxes) b.IsChecked = false; };
            var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            quick.Children.Add(all);
            quick.Children.Add(none);

            var content = new StackPanel { Spacing = 6, MinWidth = 480 };
            content.Children.Add(new TextBlock
            {
                Text = $"{missing.Count:N0} book(s) on “{DriveLabel(driveId)}” weren't found in the last scan. They may have been " +
                       "moved, renamed or deleted. Ticked books are removed from the library; unticked ones stay flagged here. " +
                       "Favorites and notes are kept, and come back if the files turn up again. Your files are never touched.",
                TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4)
            });
            content.Children.Add(quick);
            content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var dialog = new ContentDialog
            {
                Title = "Review missing books",
                Content = content,
                PrimaryButtonText = "Remove ticked",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

            Library.RemoveMissing(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList());
            Refresh();
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    // ── Rename / remove drive ────────────────────────────────────────────

    private async void OnRenameDrive(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string driveId) return;
            var drive = _drives.FirstOrDefault(d => d.Id == driveId);
            if (drive is null) return;

            var box = new TextBox
            {
                Text = drive.Label, SelectionStart = 0, SelectionLength = drive.Label.Length,
                PlaceholderText = "e.g. WD Passport 2TB - Books", MinWidth = 320
            };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "Give this drive a name you will recognise, especially if you have several drives of the same model.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
            panel.Children.Add(box);

            var dialog = new ContentDialog
            {
                Title = "Rename Drive",
                Content = panel,
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

            var name = box.Text.Trim();
            if (name.Length == 0 || name == drive.Label) return;
            Library.RenameDrive(driveId, name);
            Refresh();
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    private async void OnRemoveDrive(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is not string driveId) return;
            if (await BlockedByScan()) return;
            var drive = _drives.FirstOrDefault(d => d.Id == driveId);
            if (drive is null) return;

            var dialog = new ContentDialog
            {
                Title = "Remove Drive?",
                Content = $"Remove '{drive.Label}' and its {drive.BookCountText} from the library? The files on the drive are not deleted. " +
                          "Favorites, notes and reading progress are kept, and come back if you add the drive again.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

            Library.RemoveDrive(driveId);
            Refresh();
        }
        catch (Exception ex) { await ShowInfoDialog("Error", ex.Message); }
    }

    // ── Hover effects ────────────────────────────────────────────────────

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(sender, 1);
    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e) => SetHover(sender, 0);

    private static void SetHover(object sender, double opacity)
    {
        if (sender is Grid { Children.Count: > 0 } g) g.Children[0].Opacity = opacity;
    }

    private void OnFolderPointerEntered(object sender, PointerRoutedEventArgs e) => SetFolderRemoveOpacity(sender, 1);
    private void OnFolderPointerExited(object sender, PointerRoutedEventArgs e) => SetFolderRemoveOpacity(sender, 0);

    private static void SetFolderRemoveOpacity(object sender, double opacity)
    {
        if (sender is Grid g)
            foreach (var child in g.Children)
                if (child is Button b) { b.Opacity = opacity; break; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string DriveLabel(string driveId) => _drives.FirstOrDefault(d => d.Id == driveId)?.Label ?? driveId;

    private async Task<bool> BlockedByScan()
    {
        if (!Library.IsScanning) return false;
        await ShowInfoDialog("Scan in progress", "A scan is already running (see the status bar). Wait for it to finish, then try again.");
        return true;
    }

    private async Task ShowInfoDialog(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot };
        await dialog.ShowThemedAsync();
    }
}
