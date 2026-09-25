using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AryanEbookLibrary.Helpers;

/// <summary>Static helpers used from x:Bind function bindings in XAML.</summary>
public static class Fn
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisEmpty(string? s) => string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisNotEmpty(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Small decoded thumbnail for grid/list cards (keeps memory low with thousands of covers).</summary>
    public static ImageSource? Cover(string? path) => Load(path, 320);

    public static ImageSource? CoverThumb(string? path) => Load(path, 96);

    public static ImageSource? CoverLarge(string? path) => Load(path, 600);

    private static ImageSource? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            // DecodePixelWidth must be set before UriSource so the image is decoded at thumbnail size only.
            var image = new BitmapImage { DecodePixelWidth = decodeWidth };
            image.UriSource = new Uri(path);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"1 book", "2,812 books"; <paramref name="many"/> for the odd plural ("person", "people").</summary>
    public static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n:N0} {many ?? one + "s"}";

    public static string Stars(int rating) =>
        rating <= 0 ? "" : new string('★', Math.Min(rating, 5)) + new string('☆', 5 - Math.Min(rating, 5));
}
