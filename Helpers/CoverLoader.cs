using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AryanEbookLibrary.Helpers;

/// <summary>
/// Covers for the library's cards and rows, read off the UI thread and decoded from memory. A BitmapImage given the
/// file's address takes the whole app down (0xC000027B) when a scan rewrites that cover while it is being opened:
/// 7 of 20 starts on a copy of the real library, 0 of 20 this way.
/// </summary>
public static class CoverLoader
{
    /// <summary>
    /// Shows the cover in <paramref name="target"/>. An unreadable or half-written file leaves the image empty, so the
    /// caller's placeholder stays; a target recycled for another book meanwhile is left alone.
    /// </summary>
    public static async Task ShowAsync(Image target, string path, int decodeWidth)
    {
        var image = new BitmapImage { DecodePixelWidth = decodeWidth };
        target.Source = image;
        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(path));
            if (!ReferenceEquals(target.Source, image)) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }
        catch
        {
            // unreadable cover: the placeholder stays
        }
    }
}
