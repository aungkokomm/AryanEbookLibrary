using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Colour themes, as My Notebook has them: one accent for every control (buttons, switches, selections, links,
/// progress) and, for every theme but Aryan blue, a tint of it across every window's title bar, chrome and page, as
/// strong as the intensity setting says (Follow). Aryan blue is the classic look: App.xaml's hand-tuned brand blue over Mica,
/// brought back exactly as it was.
/// </summary>
public static class ColorTheme
{
    /// <param name="Page">The light theme's page: a pale colour of the accent's family, which the chrome is mixed from.</param>
    public sealed record Theme(string Name, string Accent, string Page = "");

    // Dark enough for white text on every one of them (5:1 and better), in both app themes. The pages are My Notebook's.
    public static readonly Theme[] Themes =
    {
        new("Aryan blue", ""),
        new("Dark blue", "#1E4D8B", "#EAF1FB"),
        new("Teal", "#0F6E63", "#E4F3F0"),
        new("Dark green", "#2A6048", "#E8F3EC"),
        new("Plum", "#6A4A8F", "#F2ECFA"),
        new("Rose", "#B0436A", "#FBEEF3"),
        new("Coffee brown", "#6B4A33", "#F4EDE3"),
        new("Slate", "#45556B", "#EDF0F3"),
    };

    public static readonly Color Brand = Color.FromArgb(255, 0x18, 0x6B, 0xD5);

    /// <summary>The accent in use.</summary>
    public static Color Accent => Parse(AppServices.Settings.AccentColor) ?? Brand;

    /// <summary>A theme that tints the window, rather than the classic Aryan blue.</summary>
    public static bool Tinted => Parse(AppServices.Settings.AccentColor) is not null;

    /// <summary>The accent changed: the open windows look their accent brushes up again.</summary>
    public static event Action? AccentChanged;

    /// <summary>Only the tint changed (the intensity slider): the windows repaint their surfaces.</summary>
    public static event Action? TintChanged;

    private static readonly string[] Shades =
    {
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    };

    // App.xaml's own values, read before anything is changed, so Aryan blue comes back exactly.
    private static Dictionary<(string Theme, string Key), Color>? _classic;
    private static Color _classicBrandText, _classicBrandTextLight, _classicHover, _classicPressed;

    /// <summary>
    /// The accent everywhere: the six shades Fluent makes its accent brushes from, in both theme dictionaries of
    /// App.xaml's brand dictionary (which must stay merged after XamlControlsResources), and Aryan's own brand brushes,
    /// which are changed in place so every page using them follows. At start-up, before any window, this is all it
    /// takes; afterwards AccentChanged tells the windows to look the shades up again.
    /// </summary>
    public static void Apply()
    {
        PaintBars();
        var resources = Application.Current.Resources;
        var brand = resources.MergedDictionaries.FirstOrDefault(d =>
            d.ThemeDictionaries.TryGetValue("Dark", out var t) && ((ResourceDictionary)t).ContainsKey("SystemAccentColorLight2"));
        if (brand is null) return;
        var dark = (ResourceDictionary)brand.ThemeDictionaries["Dark"];
        var light = (ResourceDictionary)brand.ThemeDictionaries["Light"];
        var appThemes = resources.ThemeDictionaries;
        var brandText = (SolidColorBrush)((ResourceDictionary)appThemes["Default"])["BrandBlueTextBrush"];
        var brandTextLight = (SolidColorBrush)((ResourceDictionary)appThemes["Light"])["BrandBlueTextBrush"];
        var fill = (SolidColorBrush)resources["BrandBlueBrush"];
        var hover = (SolidColorBrush)resources["BrandBlueHoverBrush"];
        var pressed = (SolidColorBrush)resources["BrandBluePressedBrush"];

        if (_classic is null)
        {
            _classic = new();
            foreach (var key in Shades)
            {
                _classic[("Dark", key)] = (Color)dark[key];
                _classic[("Light", key)] = (Color)light[key];
            }
            (_classicBrandText, _classicBrandTextLight) = (brandText.Color, brandTextLight.Color);
            (_classicHover, _classicPressed) = (hover.Color, pressed.Color);
        }

        if (Parse(AppServices.Settings.AccentColor) is not { } a)
        {
            foreach (var ((theme, key), color) in _classic) (theme == "Dark" ? dark : light)[key] = color;
            resources["SystemAccentColor"] = Brand;
            (brandText.Color, brandTextLight.Color) = (_classicBrandText, _classicBrandTextLight);
            (fill.Color, hover.Color, pressed.Color) = (Brand, _classicHover, _classicPressed);
            return;
        }

        // Fluent fills with Dark1 in the light theme and Light2 in the dark one, so both are the accent itself, with
        // white text on it; text in the accent colour uses Dark2 on light and Light3 on dark.
        light["SystemAccentColorLight1"] = Lighten(a, 0.15);
        light["SystemAccentColorLight2"] = Lighten(a, 0.35);
        light["SystemAccentColorLight3"] = Lighten(a, 0.55);
        light["SystemAccentColorDark1"] = a;
        light["SystemAccentColorDark2"] = Darken(a, 0.17);
        light["SystemAccentColorDark3"] = Darken(a, 0.33);
        dark["SystemAccentColorLight1"] = Lighten(a, 0.15);
        dark["SystemAccentColorLight2"] = a;
        dark["SystemAccentColorLight3"] = Lighten(a, 0.45);
        dark["SystemAccentColorDark1"] = Darken(a, 0.17);
        dark["SystemAccentColorDark2"] = Darken(a, 0.33);
        dark["SystemAccentColorDark3"] = Darken(a, 0.5);
        resources["SystemAccentColor"] = a;
        (brandText.Color, brandTextLight.Color) = (Lighten(a, 0.45), a);
        (fill.Color, hover.Color, pressed.Color) = (a, Lighten(a, 0.1), Darken(a, 0.14));
    }

    /// <summary>The setting changed: the accent is applied again and the open windows follow.</summary>
    public static void Changed()
    {
        Apply();
        AccentChanged?.Invoke();
    }

    /// <summary>The intensity setting changed.</summary>
    public static void IntensityChanged()
    {
        PaintBars();
        TintChanged?.Invoke();
    }

    private static (Color Dark, Color Light)? _plainBars;

    /// <summary>
    /// App.xaml's FloatingBarBrush, for bars that float over a page: the window's tint in both themes, or their plain
    /// colour back for Aryan blue. Changed in place, so open windows follow.
    /// </summary>
    private static void PaintBars()
    {
        var themes = Application.Current.Resources.ThemeDictionaries;
        var dark = (SolidColorBrush)((ResourceDictionary)themes["Default"])["FloatingBarBrush"];
        var light = (SolidColorBrush)((ResourceDictionary)themes["Light"])["FloatingBarBrush"];
        _plainBars ??= (dark.Color, light.Color);
        dark.Color = Surfaces(true)?.Chrome ?? _plainBars.Value.Dark;
        light.Color = Surfaces(false)?.Chrome ?? _plainBars.Value.Light;
    }

    /// <summary>
    /// Keeps a window in the colour theme until it closes: root is its root grid (title bar and chrome, otherwise
    /// Mica), page the optional border behind its page. It follows the app theme, a new accent and the intensity.
    /// </summary>
    public static void Follow(Window window, Panel root, Border? page)
    {
        var plain = root.Background;
        void Paint()
        {
            if (Surfaces(root.ActualTheme == ElementTheme.Dark) is { } tint)
            {
                root.Background = new SolidColorBrush(tint.Chrome);
                if (page is not null) page.Background = new SolidColorBrush(tint.Page);
            }
            else
            {
                root.Background = plain;
                if (page is not null) page.Background = null;
            }
        }
        // Fluent's accent brushes were made from the old shades: a theme flip makes every control in the window look
        // them up again. Flyouts and dialogs made from now on use the new ones by themselves.
        void NewAccent()
        {
            var requested = root.RequestedTheme;
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = requested;
            Paint();
        }
        Paint();
        root.ActualThemeChanged += (_, _) => Paint();
        AccentChanged += NewAccent;
        TintChanged += Paint;
        window.Closed += (_, _) =>
        {
            AccentChanged -= NewAccent;
            TintChanged -= Paint;
        };
    }

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color DarkChrome = Color.FromArgb(255, 28, 28, 28);

    /// <summary>
    /// The window's two tinted surfaces, null for Aryan blue, made as My Notebook makes them. Light: the theme's own pale
    /// page, and the chrome (title bar and sidebar) mixed from it toward the accent, so the chrome carries the hue rather
    /// than turning grey. Dark: both mixed from near-black toward the accent, the page a little more. The intensity
    /// (0 to 2, 1 by default) scales the accent's share.
    /// </summary>
    public static (Color Chrome, Color Page)? Surfaces(bool darkTheme)
    {
        if (Parse(AppServices.Settings.AccentColor) is not { } a) return null;
        var m = Math.Clamp(AppServices.Settings.ThemeIntensity, 0, 2);
        if (darkTheme) return (Mix(DarkChrome, a, 0.10 * m), Mix(DarkChrome, a, 0.18 * m));
        var page = Parse(Themes.FirstOrDefault(t => t.Accent.Equals(AppServices.Settings.AccentColor, StringComparison.OrdinalIgnoreCase))?.Page)
            ?? Mix(White, a, 0.08);
        return (Mix(page, a, 0.15 * m), page);
    }

    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return null;
        try
        {
            return Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>From a toward b by t (0 to 1).</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Lighten(Color c, double t) => Mix(c, White, t);
    private static Color Darken(Color c, double t) => Mix(c, Color.FromArgb(255, 0, 0, 0), t);
}
