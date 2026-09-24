using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AryanEbookLibrary.Reader;

/// <summary>Pictures of part of a page, for clipped areas and highlighted PDF lines: cut out and kept as PNG.</summary>
internal static class ClipMaker
{
    /// <summary>The part of the page a clip keeps around what was marked, as a fraction of the page's width.</summary>
    public const double Margin = 0.006;

    /// <summary>Cuts a rectangle (fractions of the picture) out of a rendered page and encodes it as PNG.</summary>
    public static async Task<byte[]?> CropAsync(byte[] bgra, int width, int height, Rect fraction)
    {
        var (x, y, w, h) = Pixels(fraction, width, height);
        if (w < 2 || h < 2) return null;
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            System.Buffer.BlockCopy(bgra, ((y + row) * width + x) * 4, pixels, row * w * 4, w * 4);
        return await EncodeAsync(pixels, w, h);
    }

    /// <summary>
    /// Decodes only a rectangle (fractions of the picture) of an encoded picture, a comic page, no wider than
    /// <paramref name="maxWidth"/>, and encodes it as PNG.
    /// </summary>
    public static async Task<byte[]?> CropImageAsync(byte[] encoded, Rect fraction, int maxWidth)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(encoded.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
        var (x, y, w, h) = Pixels(fraction, width, height);
        if (w < 2 || h < 2) return null;
        var scale = Math.Min(1.0, maxWidth / (double)w);
        uint Scaled(double v) => (uint)Math.Max(1, Math.Round(v * scale));
        var transform = new BitmapTransform
        {
            InterpolationMode = BitmapInterpolationMode.Fant,
            ScaledWidth = Scaled(width),
            ScaledHeight = Scaled(height),
            Bounds = new BitmapBounds { X = (uint)Math.Floor(x * scale), Y = (uint)Math.Floor(y * scale), Width = Scaled(w), Height = Scaled(h) },
        };
        // The bounds must lie inside the scaled picture.
        var bounds = transform.Bounds;
        bounds.Width = Math.Min(bounds.Width, transform.ScaledWidth - bounds.X);
        bounds.Height = Math.Min(bounds.Height, transform.ScaledHeight - bounds.Y);
        transform.Bounds = bounds;
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return await EncodeAsync(data.DetachPixelData(), (int)bounds.Width, (int)bounds.Height);
    }

    private static (int X, int Y, int W, int H) Pixels(Rect fraction, int width, int height)
    {
        var x = Math.Clamp((int)Math.Floor(fraction.X * width), 0, width - 1);
        var y = Math.Clamp((int)Math.Floor(fraction.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((fraction.X + fraction.Width) * width), x + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((fraction.Y + fraction.Height) * height), y + 1, height);
        return (x, y, right - x, bottom - y);
    }

    private static async Task<byte[]> EncodeAsync(byte[] bgra, int width, int height)
    {
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        output.Seek(0);
        using var copy = new MemoryStream();
        await output.AsStreamForRead().CopyToAsync(copy);
        return copy.ToArray();
    }

    /// <summary>A rectangle of fractions grown by <see cref="Margin"/> on every side, kept inside the page.</summary>
    public static Rect Padded(Rect r)
    {
        var x = Math.Max(0, r.X - Margin);
        var y = Math.Max(0, r.Y - Margin);
        return new Rect(x, y, Math.Min(1 - x, r.Width + 2 * Margin), Math.Min(1 - y, r.Height + 2 * Margin));
    }
}
