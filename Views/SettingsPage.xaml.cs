using System.Diagnostics;
using AryanEbookLibrary.Helpers;
using AryanEbookLibrary.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
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
        BuildSwatches();
        IntensitySlider.Value = Math.Clamp(AppServices.Settings.ThemeIntensity * 100, 0, 200);
        ShowIntensity();
        AutoScanSwitch.IsOn = AppServices.Settings.AutoScanOnStart;
        LookupOnlineSwitch.IsOn = AppServices.Settings.LookupOnline;
        GoogleKeyBox.Password = AppServices.Settings.GoogleBooksKey;
        ReadInAppSwitch.IsOn = AppServices.Settings.ReadPdfInApp;
        ReadEpubInAppSwitch.IsOn = AppServices.Settings.ReadEpubInApp;
        ReadComicsInAppSwitch.IsOn = AppServices.Settings.ReadComicsInApp;
        DefineMyanmarBox.IsChecked = AppServices.Settings.DefineShowsMyanmar;
        DefineHindiBox.IsChecked = AppServices.Settings.DefineShowsHindi;
        Select(ReaderToolbarBox, AppServices.Settings.ReaderToolbar);
        Select(ReadAloudSpeedBox, AppServices.Settings.ReadAloudRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ShowVoices();
        Select(LanguageBox, AppServices.Settings.PreferredLanguage);
        Select(FormatBox, AppServices.Settings.PreferredFormat);
        ShowKeptCopies();
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

    // Round swatches as in My Notebook; the chosen one has a ring and a tick.
    private void BuildSwatches()
    {
        foreach (var theme in ColorTheme.Themes)
        {
            var color = ColorTheme.Parse(theme.Accent) ?? ColorTheme.Brand;
            var swatch = new Button
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Padding = new Thickness(0),
                BorderThickness = new Thickness(2), Tag = theme.Accent,
                Background = new SolidColorBrush(color),
                Resources =
                {
                    // The swatch keeps its own colour under the pointer and when pressed
                    ["ButtonBackgroundPointerOver"] = new SolidColorBrush(color),
                    ["ButtonBackgroundPressed"] = new SolidColorBrush(color),
                },
                Content = new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Colors.White) },
            };
            ToolTipService.SetToolTip(swatch, theme.Name);
            AutomationProperties.SetName(swatch, theme.Name);
            swatch.Click += (_, _) =>
            {
                AppServices.Settings.AccentColor = theme.Accent;
                AppServices.Settings.Save();
                ColorTheme.Changed();
                ShowSwatches();
                ShowIntensity();
            };
            SwatchPanel.Children.Add(swatch);
        }
        ShowSwatches();
        ActualThemeChanged += (_, _) => ShowSwatches();
    }

    private void ShowSwatches()
    {
        // The ring in the page's own theme: Application resources would give the app's
        var ring = ActualTheme == ElementTheme.Dark ? Colors.White : Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
        foreach (var swatch in SwatchPanel.Children.OfType<Button>())
        {
            var chosen = (string)swatch.Tag == AppServices.Settings.AccentColor;
            swatch.BorderBrush = new SolidColorBrush(chosen ? ring : Colors.Transparent);
            ((FontIcon)swatch.Content).Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(swatch, chosen ? "Selected" : "");
        }
    }

    private void ShowIntensity()
    {
        IntensitySlider.IsEnabled = ColorTheme.Tinted;
        IntensityDescription.Text = ColorTheme.Tinted
            ? "How strongly the colour tints the window"
            : "Aryan blue keeps the window untinted; choose another colour to tint it";
    }

    private void OnIntensityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ThemeIntensity = e.NewValue / 100;
        AppServices.Settings.Save();
        ColorTheme.IntensityChanged();
    }

    private void OnAutoScanToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.AutoScanOnStart = AutoScanSwitch.IsOn;
        AppServices.Settings.Save();
    }

    private void OnReadInAppToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ReadPdfInApp = ReadInAppSwitch.IsOn;
        AppServices.Settings.ReadEpubInApp = ReadEpubInAppSwitch.IsOn;
        AppServices.Settings.ReadComicsInApp = ReadComicsInAppSwitch.IsOn;
        AppServices.Settings.Save();
    }

    private void OnReaderToolbarChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.ReaderToolbar = TagOf(ReaderToolbarBox) is "Hide" ? "Hide" : "Always";
        AppServices.Settings.Save();
        Reader.ReaderWindow.ToolbarSettingChanged();
    }

    private void OnReadAloudSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(TagOf(ReadAloudSpeedBox), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
            AppServices.Settings.ReadAloudRate = rate;
        AppServices.Settings.Save();
    }

    /// <summary>Read aloud uses Windows' own voices: which languages they read here, and where more come from.</summary>
    private void ShowVoices()
    {
        string languages;
        try
        {
            languages = Reader.ReadAloud.VoiceLanguages();
        }
        catch (Exception ex)
        {
            Log.Write("read aloud: the voices could not be listed: " + ex.Message);
            languages = "";
        }
        VoicesText.Text = (languages.Length > 0 ? $"Windows' voices here read {languages}. " : "Windows has no voices here. ")
            + "More are added in Windows Settings, Time & language, Speech. Ctrl+Shift+U reads a book aloud.";
    }

    private void OnDefineLanguagesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.DefineShowsMyanmar = DefineMyanmarBox.IsChecked == true;
        AppServices.Settings.DefineShowsHindi = DefineHindiBox.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void OnLookupOnlineToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.LookupOnline = LookupOnlineSwitch.IsOn;
        AppServices.Settings.Save();
        if (LookupOnlineSwitch.IsOn) AppServices.Library.StartOnlineLookups();
        else AppServices.Online.Stop();
    }

    private void OnGoogleKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.GoogleBooksKey = GoogleKeyBox.Password.Trim();
        AppServices.Settings.Save();
    }

    /// <summary>A key just given: the books still missing details are looked up now, not after the next start.</summary>
    private void OnGoogleKeyLostFocus(object sender, RoutedEventArgs e)
    {
        if (AppServices.Settings.LookupOnline && AppServices.Settings.GoogleBooksKey.Length > 0) AppServices.Library.StartOnlineLookups();
    }

    private static void Select(ComboBox box, string tag)
    {
        box.SelectedIndex = Math.Max(0, box.Items.OfType<ComboBoxItem>().ToList()
            .FindIndex(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase)));
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OnPreferredLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.PreferredLanguage = TagOf(LanguageBox);
        AppServices.Settings.Save();
    }

    private void OnPreferredFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.PreferredFormat = TagOf(FormatBox);
        AppServices.Settings.Save();
    }

    private void ShowKeptCopies()
    {
        var count = AppServices.Settings.KeptCopies.Count;
        KeptCopiesText.Text = count == 0
            ? "You have not picked a copy in any group yet."
            : count == 1 ? "You picked the copy to keep in 1 group." : $"You picked the copy to keep in {count} groups.";
        ForgetKeptButton.IsEnabled = count > 0;
    }

    private void OnForgetKeptCopies(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.KeptCopies.Clear();
        AppServices.Settings.Save();
        ShowKeptCopies();
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
            .ShowThemedAsync();

    private async void OnExportCatalog(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"Aryan-library-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("Spreadsheet", new List<string> { ".csv" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var books = AppServices.Library.AllBooks;
            var count = await Task.Run(() => CatalogExport.Csv(file.Path, books));
            CatalogResult.Text = $"{Fn.Count(count, "book")} written to {file.Name}.";
        }
        catch (Exception ex)
        {
            CatalogResult.Text = "Export failed: " + ex.Message;
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"AryanLibrary-backup-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("Backup file", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var count = await Task.Run(() => BackupService.Export(file.Path, AppServices.Repo, AppServices.AnnotationStore));
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

    /// <summary>
    /// THIRD-PARTY-NOTICES.txt in the classic Notepad, by its full path and without the shell: the Store Notepad, which
    /// the shell would pick, fails to start from beside a self-contained Windows App SDK app.
    /// </summary>
    private void OnOpenLicences(object sender, RoutedEventArgs e)
    {
        try
        {
            var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            Process.Start(new ProcessStartInfo { FileName = notepad, Arguments = $"\"{notices}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Write("Open licences failed: " + ex.Message);
        }
    }
}
