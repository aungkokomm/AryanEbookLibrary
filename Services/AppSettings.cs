using System.Text.Json;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";       // System | Light | Dark
    // The colour theme's accent, "#RRGGBB"; "" is Aryan blue, the classic look (ColorTheme).
    public string AccentColor { get; set; } = "";
    // How strongly a colour theme tints the window: 0 none, 1 the usual, up to 2.
    public double ThemeIntensity { get; set; } = 1.0;
    public bool AutoScanOnStart { get; set; } = true;
    public ViewMode ViewMode { get; set; } = ViewMode.Grid;
    public SortMode SortMode { get; set; } = SortMode.Title;
    public bool SortDescending { get; set; }
    public string GridDensity { get; set; } = "M";     // S | M | L | XL card size
    public bool LookupOnline { get; set; }               // fill missing details from Open Library in the background
    public string GoogleBooksKey { get; set; } = "";     // the user's own Google Books API key; "" = Google Books is not asked
    // Authors the library offered for books with none ("site|name") and the user turned down.
    public List<string> NoAuthorOffers { get; set; } = new();
    public List<string> NotSamePeople { get; set; } = new();   // name pairs the user said are different people
    // When the same book is in the library twice, which copy to keep. "" means no preference.
    public string PreferredFormat { get; set; } = "";          // EPUB | PDF | MOBI | AZW3 | KFX | CBZ | CBR
    public string PreferredLanguage { get; set; } = "";        // en | hi | my ...
    // Groups where the user picked the copy themselves: group key -> that file's key.
    public Dictionary<string, string> KeptCopies { get; set; } = new();
    // Tags the library suggested and the user turned down, so they are not offered again.
    public List<string> NoTags { get; set; } = new();
    // Saved library views, shown in the navigation pane in this order.
    public List<Shelf> Shelves { get; set; } = new();
    public bool ShelvesExpanded { get; set; } = true;   // folded away, they leave room for the rest of the pane
    public bool ListsExpanded { get; set; } = true;     // the same for My lists
    // How many books the user means to finish in a year, by year. Missing or 0 means no goal.
    public Dictionary<int, int> ReadingGoals { get; set; } = new();
    // The app's own reader. Off, PDFs open in the default app as before.
    public bool ReadPdfInApp { get; set; } = true;
    // The same for EPUB, MOBI, AZW3 and KFX books.
    public bool ReadEpubInApp { get; set; } = true;
    public int ReaderFontSize { get; set; } = 100;             // percent of the book's own size
    public string ReaderFlow { get; set; } = "scrolled";       // paginated | scrolled
    // The book reader's text: each starts at the look books always had.
    public string ReaderFont { get; set; } = "book";           // book | serif | sans
    public string ReaderLineSpacing { get; set; } = "normal";  // tight | normal | wide | extra
    public string ReaderTextWidth { get; set; } = "medium";    // narrow | medium | wide
    public bool ReaderJustify { get; set; }
    public double ReadAloudRate { get; set; } = 1.0;           // Windows' usual speed is 1
    // And for CBZ and CBR comics.
    public bool ReadComicsInApp { get; set; } = true;
    public string ReaderComicView { get; set; } = "Continuous"; // Continuous | Page | Width
    public string ReaderPageTheme { get; set; } = "Paper";     // Paper | Sepia | Night
    public bool ReaderContentsOpen { get; set; }
    public int LastHighlightColor { get; set; } = 1;   // the colour the Note button and "Highlight" use
    public string ReaderToolbar { get; set; } = "Always";     // Always | Hide (until the pointer goes to the top)
    public int ReaderWidth { get; set; } = 1100;
    public int ReaderHeight { get; set; } = 900;
    // A book's details window: it opens maximized, and comes back to this size when restored.
    public int DetailsWidth { get; set; } = 1100;
    public int DetailsHeight { get; set; } = 800;
    // What Define shows under the English definition.
    public bool DefineShowsMyanmar { get; set; } = true;
    public bool DefineShowsHindi { get; set; } = true;
    // The newer version the user dismissed the update bubble for, so it is not offered again. A later one is.
    public string SkippedUpdateVersion { get; set; } = "";

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
