using System.Text;
using System.Text.RegularExpressions;
using AryanEbookLibrary.Models;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// PDF metadata without a third-party PDF library:
///  - Title/Author/Subject/Keywords: scanned from the Info dictionary near the start/end of the file.
///  - Cover + page count: rendered by the built-in Windows.Data.Pdf renderer (page 1).
/// </summary>
public static class PdfReader
{
    private const int ScanBytes = 768 * 1024;

    private static readonly Regex LiteralField =
        new(@"/(Title|Author|Subject|Keywords)\s*\(((?:\\.|[^\\)])*)\)", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HexField =
        new(@"/(Title|Author|Subject|Keywords)\s*<([0-9A-Fa-f\s]+)>", RegexOptions.Compiled);

    public static async Task<BookMetadata> ReadAsync(string path)
    {
        var md = new BookMetadata();

        try
        {
            ReadInfoDictionary(path, md);
        }
        catch (Exception ex)
        {
            Log.Write($"PDF info failed ({Path.GetFileName(path)}): {ex.Message}");
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var doc = await PdfDocument.LoadFromFileAsync(file);
            md.PageCount = (int)doc.PageCount;

            if (doc.PageCount > 0)
            {
                using var page = doc.GetPage(0);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = 480,
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                };

                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options);

                var bytes = new byte[stream.Size];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);

                md.Cover = bytes;
                md.CoverExt = ".png";
            }
        }
        catch (Exception ex)
        {
            // password-protected or damaged PDFs: keep whatever the Info scan found
            Log.Write($"PDF render failed ({Path.GetFileName(path)}): {ex.Message}");
        }

        return md;
    }

    private static void ReadInfoDictionary(string path, BookMetadata md)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var len = fs.Length;
            if (len <= ScanBytes * 2L)
            {
                var all = new byte[len];
                fs.ReadExactly(all, 0, all.Length);
                text = Encoding.Latin1.GetString(all);
            }
            else
            {
                var head = new byte[ScanBytes];
                fs.ReadExactly(head, 0, head.Length);
                var tail = new byte[ScanBytes];
                fs.Seek(len - ScanBytes, SeekOrigin.Begin);
                fs.ReadExactly(tail, 0, tail.Length);
                text = Encoding.Latin1.GetString(head) + "\n" + Encoding.Latin1.GetString(tail);
            }
        }

        // For incrementally-updated PDFs the last occurrence is the current one.
        var fields = new Dictionary<string, string>();
        foreach (Match m in LiteralField.Matches(text))
            fields[m.Groups[1].Value] = DecodeLiteral(m.Groups[2].Value);
        foreach (Match m in HexField.Matches(text))
            fields[m.Groups[1].Value] = DecodeHex(m.Groups[2].Value);

        if (fields.TryGetValue("Title", out var title) && !LooksLikeJunkTitle(title))
            md.Title = XmlUtil.Clean(title);
        if (fields.TryGetValue("Author", out var author)) md.Author = XmlUtil.Clean(author);
        if (fields.TryGetValue("Keywords", out var kw)) md.Subjects = XmlUtil.Clean(kw);
    }

    private static bool LooksLikeJunkTitle(string t)
    {
        t = t.Trim();
        if (t.Length == 0) return true;
        if (t.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("untitled", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(t, @"\.(docx?|indd|pages|rtf|tex|qxd|pub|odt)$", RegexOptions.IgnoreCase);
    }

    private static string DecodeLiteral(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                bytes.Add((byte)c);
                continue;
            }

            c = s[++i];
            switch (c)
            {
                case 'n': bytes.Add(10); break;
                case 'r': bytes.Add(13); break;
                case 't': bytes.Add(9); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case '\r':
                    if (i + 1 < s.Length && s[i + 1] == '\n') i++;
                    break;
                case '\n': break;
                case >= '0' and <= '7':
                {
                    var val = c - '0';
                    for (var k = 0; k < 2 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7'; k++)
                        val = val * 8 + (s[++i] - '0');
                    bytes.Add((byte)val);
                    break;
                }
                default: bytes.Add((byte)c); break;
            }
        }
        return DecodeBytes(bytes.ToArray());
    }

    private static string DecodeHex(string hex)
    {
        hex = Regex.Replace(hex, @"\s+", "");
        if (hex.Length % 2 == 1) hex += "0";
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return DecodeBytes(bytes);
    }

    private static string DecodeBytes(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return Encoding.Latin1.GetString(b);
    }
}
