using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AryanEbookLibrary.Reader.Define;

namespace AryanEbookLibrary.Services;

/// <summary>A word looked up with Define in the reader: the sentence it was in, the book, where, and when.</summary>
public sealed class WordLookup
{
    public long Id { get; set; }
    public string Word { get; set; } = "";
    public string Sentence { get; set; } = "";
    public string BookKey { get; set; } = "";
    public string BookTitle { get; set; } = "";   // kept, so the word still says where it came from once the book is gone
    public string Place { get; set; } = "";       // "page 45" in a PDF; empty in a book that flows
    public int Times { get; set; } = 1;           // the same word in the same sentence, looked up again
    public DateTime LookedUtc { get; set; }
}

/// <summary>
/// Words you looked up: every Define in the reader is kept with its sentence and book, for the Words page and for
/// learning them elsewhere (a CSV for a spreadsheet, a text file Anki imports as cards).
/// </summary>
public static class WordList
{
    // Where a sentence ends: . ! ? and the Burmese, Hindi and Chinese full stops (။ । 。).
    private static readonly char[] Ends = ['.', '!', '?', '။', '।', '。'];

    private const int MaxSentence = 300;

    /// <summary>
    /// The sentence a word was in, from the text before and after it: back to the end of the sentence before, on to
    /// this one's end, line breaks as spaces. A very long one is cut around the word.
    /// </summary>
    public static string Sentence(string before, string word, string after)
    {
        var start = before.LastIndexOfAny(Ends);
        var head = start < 0 ? before : before[(start + 1)..];
        var end = after.IndexOfAny(Ends);
        var tail = end < 0 ? after : after[..(end + 1)];
        head = Regex.Replace(head, @"\s+", " ").TrimStart();
        tail = Regex.Replace(tail, @"\s+", " ").TrimEnd();
        word = Regex.Replace(word, @"\s+", " ");
        if (head.Length + word.Length + tail.Length > MaxSentence)
        {
            var room = Math.Max(0, (MaxSentence - word.Length) / 2);
            if (head.Length > room) head = "…" + head[^room..].TrimStart();
            if (tail.Length > room) tail = tail[..room].TrimEnd() + "…";
        }
        return (head + word + tail).Trim();
    }

    /// <summary>A line of meaning to learn the word by: up to two parts of speech, each its first definition.</summary>
    public static string Meaning(WordDefinition? definition) =>
        definition is null ? "" : string.Join("; ", definition.Senses.Take(2)
            .Where(s => s.Definitions.Count > 0)
            .Select(s => $"{s.PartOfSpeech}: {s.Definitions[0]}"));

    /// <summary>Every lookup as a spreadsheet row. UTF-8 with a byte order mark, which Excel needs for Burmese and Hindi.</summary>
    public static int Csv(string path, IReadOnlyList<WordLookup> words, Func<string, string> meaning)
    {
        var sb = new StringBuilder();
        sb.Append("Word,Meaning,Sentence,Book,Where,Looked up,Times\r\n");
        foreach (var w in words)
            sb.Append(string.Join(",", Field(w.Word), Field(meaning(w.Word)), Field(w.Sentence), Field(w.BookTitle), Field(w.Place),
                Field(w.LookedUtc.ToLocalTime().ToString("yyyy-MM-dd")), w.Times.ToString())).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return words.Count;
    }

    /// <summary>
    /// One card a word for Anki (File, Import): the word on the front; its meaning, the sentences it was met in (the word
    /// in bold) and the book on the back. Tab-separated, with the header lines that tell Anki so and that it holds HTML.
    /// </summary>
    public static int Anki(string path, IReadOnlyList<WordLookup> words, Func<string, string> meaning)
    {
        var cards = Grouped(words);
        var sb = new StringBuilder("#separator:tab\n#html:true\n");
        foreach (var card in cards)
        {
            var word = card[0].Word;
            var back = new List<string>();
            if (meaning(word) is { Length: > 0 } m) back.Add(Html(m));
            foreach (var use in card.Take(3).Where(u => u.Sentence.Length > 0))
                back.Add("<i>" + Bold(Html(use.Sentence), Html(word)) + "</i>" + (use.BookTitle.Length > 0 ? "<br><small>" + Html(use.BookTitle) + "</small>" : ""));
            sb.Append(Html(word)).Append('\t').Append(string.Join("<br><br>", back)).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return cards.Count;
    }

    /// <summary>The lookups of each word together (in any case), the word looked up last first, its newest use first.</summary>
    public static List<List<WordLookup>> Grouped(IEnumerable<WordLookup> words) =>
        words.GroupBy(w => w.Word.ToLowerInvariant())
            .Select(g => g.OrderByDescending(w => w.LookedUtc).ToList())
            .OrderByDescending(g => g[0].LookedUtc)
            .ToList();

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string Html(string text) => WebUtility.HtmlEncode(Regex.Replace(text, @"\s+", " "));

    private static string Bold(string sentence, string word) =>
        word.Length == 0 ? sentence : Regex.Replace(sentence, @"(?<!\w)" + Regex.Escape(word) + @"(?!\w)", m => "<b>" + m.Value + "</b>", RegexOptions.IgnoreCase);
}
