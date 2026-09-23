using System.Text.Json;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";       // System | Light | Dark
    public bool AutoScanOnStart { get; set; } = true;
    public ViewMode ViewMode { get; set; } = ViewMode.Grid;
    public SortMode SortMode { get; set; } = SortMode.Title;
    public bool SortDescending { get; set; }
    public string GridDensity { get; set; } = "M";     // S | M | L | XL card size
    public bool LookupOnline { get; set; }               // fill missing details from Open Library in the background
    public List<string> NotSamePeople { get; set; } = new();   // name pairs the user said are different people
    // When the same book is in the library twice, which copy to keep. "" means no preference.
    public string PreferredFormat { get; set; } = "";          // EPUB | PDF | MOBI | AZW3 | CBZ | CBR
    public string PreferredLanguage { get; set; } = "";        // en | hi | my ...
    // Groups where the user picked the copy themselves: group key -> that file's key.
    public Dictionary<string, string> KeptCopies { get; set; } = new();
    // Tags the library suggested and the user turned down, so they are not offered again.
    public List<string> NoTags { get; set; } = new();
    // Saved library views, shown in the navigation pane in this order.
    public List<Shelf> Shelves { get; set; } = new();
    public bool ShelvesExpanded { get; set; } = true;   // folded away, they leave room for the rest of the pane
    // How many books the user means to finish in a year, by year. Missing or 0 means no goal.
    public Dictionary<int, int> ReadingGoals { get; set; } = new();
    // The app's own reader. Off, PDFs open in the default app as before.
    public bool ReadPdfInApp { get; set; } = true;
    // The same for EPUB, MOBI and AZW3 books.
    public bool ReadEpubInApp { get; set; } = true;
    public int ReaderFontSize { get; set; } = 100;             // percent of the book's own size
    public string ReaderFlow { get; set; } = "paginated";      // paginated | scrolled
    // And for CBZ and CBR comics.
    public bool ReadComicsInApp { get; set; } = true;
    public string ReaderComicView { get; set; } = "Continuous"; // Continuous | Page | Width
    public string ReaderPageTheme { get; set; } = "Paper";     // Paper | Sepia | Night
    public bool ReaderContentsOpen { get; set; }
    public int ReaderWidth { get; set; } = 1100;
    public int ReaderHeight { get; set; } = 900;
    // What Define shows under the English definition.
    public bool DefineShowsMyanmar { get; set; } = true;
    public bool DefineShowsHindi { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed: " + ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Write("Settings save failed: " + ex.Message);
        }
    }
}
