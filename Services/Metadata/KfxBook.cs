using System.Diagnostics;
using System.Security.Cryptography;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// KFX, Amazon's newest Kindle format, is read through an EPUB copy of the book. boko, a separate open-source program
/// (GPL-3.0, beside the app in tools\boko), makes the copy once; it is kept in the data folder under the KFX file's
/// SHA-256, so a book that moves or is renamed, or a second copy of it, finds the same one. boko writes the chapters the
/// same way every time (only the order of its extra files can change), so positions and highlights in the copy stay put
/// even if it has to be made again. The KFX file itself is only ever read.
/// </summary>
public static class KfxBook
{
    /// <summary>A copy that could not be made: why, and whether that is because the book is locked (DRM).</summary>
    public sealed record Problem(string Text, bool Locked);

    /// <summary>boko, beside the app. The tests point this at the vendored copy.</summary>
    public static string Converter { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "boko", "boko.exe");

    /// <summary>Where the copies are kept. The tests point this at a folder of their own.</summary>
    public static string? CacheDir { get; set; }

    private static string Cache => CacheDir ?? Path.Combine(AppPaths.DataDir, "Kfx");

    // boko runs every chapter on its own thread already: two books at a time is plenty during a scan.
    private static readonly SemaphoreSlim Gate = new(2);

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public const string LockedText = "It is a Kindle book protected with DRM, which only Amazon's own Kindle apps can open.";

    /// <summary>The EPUB copy of a KFX book, made now if there is none yet; or why there cannot be one.</summary>
    public static async Task<(string? Epub, Problem? Problem)> EpubAsync(string kfx)
    {
        if (await Task.Run(() => Kind(kfx)) is { } problem) return (null, problem);
        var hash = await Task.Run(() => Hash(kfx));
        var epub = Path.Combine(Cache, hash + ".epub");
        if (File.Exists(epub)) return (epub, null);

        await Gate.WaitAsync();
        try
        {
            // Made by another scan or window while this one waited.
            if (File.Exists(epub)) return (epub, null);
            if (!File.Exists(Converter))
            {
                Log.Write($"KFX: the converter is missing ({Converter})");
                return (null, new Problem("The part of Aryan that reads KFX books is missing. Installing Aryan again brings it back.", false));
            }
            Directory.CreateDirectory(Cache);
            var partial = Path.Combine(Cache, $"{hash}.{Guid.NewGuid():N}.partial.epub");
            var clock = Stopwatch.StartNew();
            var (exit, output) = await RunAsync(kfx, partial);
            if (exit != 0 || !File.Exists(partial))
            {
                TryDelete(partial);
                Log.Write($"KFX: {Path.GetFileName(kfx)} could not be converted (exit {exit}): {output.Trim()}");
                return (null, output.Contains("DRM", StringComparison.OrdinalIgnoreCase)
                    ? new Problem(LockedText, true)
                    : new Problem("It could not be turned into a book Aryan can read.", false));
            }
            try
            {
                File.Move(partial, epub);
            }
            catch (IOException) when (File.Exists(epub))
            {
                TryDelete(partial);
            }
            Log.Write($"KFX: {Path.GetFileName(kfx)} converted in {clock.ElapsedMilliseconds} ms");
            return (epub, null);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Null for a KFX book boko can read. A book from the Kindle Store keeps its locked (DRMION) form, which starts
    /// differently from the open container ("CONT") that Kindle Create, Kindle Previewer and converters write.
    /// </summary>
    private static Problem? Kind(string path)
    {
        var head = new byte[8];
        using (var file = File.OpenRead(path))
            if (file.Read(head) < 8) return new Problem("The file is too short to be a KFX book.", false);
        if (head.AsSpan(0, 4).SequenceEqual("CONT"u8)) return null;
        if (head.AsSpan(1, 6).SequenceEqual("DRMION"u8)) return new Problem(LockedText, true);
        return new Problem("It is not a KFX book Aryan knows how to read.", false);
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file))[..32].ToLowerInvariant();
    }

    private static async Task<(int Exit, string Output)> RunAsync(string kfx, string epub)
    {
        var start = new ProcessStartInfo(Converter)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "convert", kfx, epub }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, $"it took longer than {Timeout.TotalMinutes} minutes");
        }
        return (process.ExitCode, await stderr + await stdout);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
