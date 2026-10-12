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

    private static string Hash(string key) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..20];

    public static async Task<string> SaveAsync(byte[] bytes, string ext, string key, string prefix = "")
    {
        var name = prefix + Hash(key) + ext;
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

    // ---- the reader's own choice of cover ----

    /// <summary>
    /// Covers the reader chose (a picture, a page of the book), named apart so a rescan or rebuilding the covers keeps
    /// them, and with the time in the name, so choosing again is a new file every view notices.
    /// </summary>
    private const string MinePrefix = "my-";

    public static async Task<string> SaveMineAsync(byte[] bytes, string ext, string key)
    {
        RemoveMine(key);
        var name = $"{MinePrefix}{Hash(key)}-{DateTime.UtcNow.Ticks:x16}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(AppPaths.Covers, name), await TryDownscaleAsync(bytes) ?? bytes);
        return name;
    }

    public static void RemoveMine(string key)
    {
        foreach (var f in Directory.EnumerateFiles(AppPaths.Covers, MinePrefix + Hash(key) + "-*"))
            try { File.Delete(f); } catch { /* in use: left behind, and the newest is the one used */ }
    }

    /// <summary>A moved or renamed book keeps the cover chosen for it.</summary>
    public static void MoveMine(string oldKey, string newKey)
    {
        if (!Directory.Exists(AppPaths.Covers)) return;
        var from = MinePrefix + Hash(oldKey);
        var chosen = Directory.EnumerateFiles(AppPaths.Covers, from + "-*").Order(StringComparer.Ordinal).LastOrDefault();
        if (chosen is null) return;
        RemoveMine(newKey);
        try
        {
            File.Move(chosen, Path.Combine(AppPaths.Covers, MinePrefix + Hash(newKey) + Path.GetFileName(chosen)[from.Length..]));
        }
        catch (IOException)
        {
            // in use: the book shows its own cover until it is chosen again
        }
    }

    /// <summary>Every chosen cover, newest of each, to look books up in with <see cref="MineFor"/>.</summary>
    public static Dictionary<string, string> Mine()
    {
        var mine = new Dictionary<string, string>();
        if (!Directory.Exists(AppPaths.Covers)) return mine;
        foreach (var name in Directory.EnumerateFiles(AppPaths.Covers, MinePrefix + "*").Select(Path.GetFileName).Order(StringComparer.Ordinal))
            if (name!.Length > MinePrefix.Length + 20) mine[name.Substring(MinePrefix.Length, 20)] = name;
        return mine;
    }

    public static string? MineFor(Dictionary<string, string> mine, string key) => mine.Count == 0 ? null : mine.GetValueOrDefault(Hash(key));

    public static void ClearAll()
    {
        foreach (var f in Directory.EnumerateFiles(AppPaths.Covers))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith(OnlinePrefix, StringComparison.Ordinal) || name.StartsWith(MinePrefix, StringComparison.Ordinal)) continue;
            try { File.Delete(f); } catch { /* in use */ }
        }
    }
}
