using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AryanEbookLibrary.Services;

/// <summary>Caches one cover image per book in AryanLibrary-Data\Covers (downscaled so a big library stays small).</summary>
public static class CoverStore
{
    private const uint MaxHeight = 600;

    public static async Task<string> SaveAsync(byte[] bytes, string ext, string key, string prefix = "")
    {
        var name = prefix + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..20] + ext;
        var path = Path.Combine(AppPaths.Covers, name);

        var data = await TryDownscaleAsync(bytes) ?? bytes;
        await File.WriteAllBytesAsync(path, data);
        return name;
    }

    private static async Task<byte[]?> TryDownscaleAsync(byte[] bytes)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(bytes.AsBuffer());
            input.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(input);
            if (decoder.PixelHeight <= MaxHeight) return null;

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            encoder.BitmapTransform.ScaledHeight = MaxHeight;
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * (double)MaxHeight / decoder.PixelHeight);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            var result = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(result.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
            return result;
        }
        catch
        {
            // unsupported codec or odd image: store the original bytes instead
            return null;
        }
    }

    /// <summary>Open Library's covers, named apart so rebuilding the books' own covers keeps them.</summary>
    public static Task<string> SaveOnlineAsync(byte[] bytes, string key) => SaveAsync(bytes, ".jpg", key, OnlinePrefix);

    private const string OnlinePrefix = "ol-";

    public static void ClearAll()
    {
        foreach (var f in Directory.EnumerateFiles(AppPaths.Covers))
        {
            if (Path.GetFileName(f).StartsWith(OnlinePrefix, StringComparison.Ordinal)) continue;
            try { File.Delete(f); } catch { /* in use */ }
        }
    }
}
