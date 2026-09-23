using System.IO.Compression;
using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Reader.Define;

/// <summary>
/// The offline dictionaries behind Define (from Ayaan PDF), each read once, on first use, off the UI thread:
/// WordNet's English definitions, the AKK dictionary's Myanmar meanings and the user's Hindi meanings. Null when
/// a file is missing or unreadable, which Define says or quietly does without.
/// </summary>
internal static class DefinitionDictionary
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionary");

    private static readonly Lazy<Task<WordDefinitions?>> English =
        new(() => Task.Run(() => Read("wordnet-en.tsv.gz", WordDefinitions.Load, d => $"{d.WordCount} words")));

    private static readonly Lazy<Task<MyanmarGlosses?>> Myanmar =
        new(() => Task.Run(() => Read("akk-en-my.tsv.gz", MyanmarGlosses.Load, g => $"{g.Count} Myanmar entries")));

    private static readonly Lazy<Task<HindiGlosses?>> Hindi =
        new(() => Task.Run(() => Read("hindi-en-hi.tsv.gz", HindiGlosses.Load, g => $"{g.Count} Hindi entries")));

    public static Task<WordDefinitions?> LoadAsync() => English.Value;

    public static Task<MyanmarGlosses?> LoadMyanmarAsync() => Myanmar.Value;

    public static Task<HindiGlosses?> LoadHindiAsync() => Hindi.Value;

    private static T? Read<T>(string name, Func<TextReader, T> load, Func<T, string> describe) where T : class
    {
        var path = Path.Combine(Folder, name);
        try
        {
            if (!File.Exists(path))
            {
                Log.Write($"define: no dictionary at {path}");
                return null;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var dictionary = load(reader);
            Log.Write($"define: {describe(dictionary)} loaded in {clock.ElapsedMilliseconds} ms");
            return dictionary;
        }
        catch (Exception ex)
        {
            Log.Write($"define: {name} could not be read: {ex.Message}");
            return null;
        }
    }
}
