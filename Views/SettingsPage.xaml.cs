using System.Diagnostics;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AryanEbookLibrary.Views;

public sealed partial class SettingsPage : Page
{
    private readonly bool _loading;

    public SettingsPage()
    {
        _loading = true;
        InitializeComponent();

        ThemeBox.SelectedIndex = AppServices.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        AutoScanSwitch.IsOn = AppServices.Settings.AutoScanOnStart;
        DataPathText.Text = "Stored in: " + AppPaths.DataDir;
        // From <Version> in the csproj, so the About line can never show a stale number.
        VersionText.Text = "Aryan eBook Library " + typeof(App).Assembly.GetName().Version?.ToString(3);
        _loading = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        AppServices.Settings.Theme = item.Tag as string ?? "System";
        AppServices.Settings.Save();
        AppServices.ApplyTheme();
    }

    private void OnAutoScanToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.AutoScanOnStart = AutoScanSwitch.IsOn;
        AppServices.Settings.Save();
    }

    private async void OnRebuildCovers(object sender, RoutedEventArgs e)
    {
        if (AppServices.Library.IsScanning)
        {
            await ShowMessage("A scan is already running. Wait for it to finish, then rebuild the covers.");
            return;
        }

        CoverStore.ClearAll();
        // Force every file to be re-read on the next scan by wiping the stored size/timestamp.
        AppServices.Db.Exec("UPDATE books SET modified_ticks = 0, cover_file = NULL");
        await AppServices.Library.ReloadAsync();
        try
        {
            await AppServices.Library.ScanAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await ShowMessage("The scan failed: " + ex.Message);
        }
    }

    private async Task ShowMessage(string text) =>
        await new ContentDialog { Title = "Rebuild covers", Content = text, CloseButtonText = "OK", XamlRoot = XamlRoot }
            .ShowAsync();

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"AryanLibrary-backup-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("Backup file", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var count = await Task.Run(() => BackupService.Export(file.Path, AppServices.Repo));
            BackupResult.Text = $"Exported {count} entries to {file.Name}.";
        }
        catch (Exception ex)
        {
            BackupResult.Text = "Export failed: " + ex.Message;
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            await AppServices.Library.RestoreFromBackupAsync(file.Path);
            BackupResult.Text = "Backup restored from " + file.Name + ".";
        }
        catch (Exception ex)
        {
            BackupResult.Text = "Restore failed: " + ex.Message;
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Open data folder failed: " + ex.Message);
        }
    }
}
