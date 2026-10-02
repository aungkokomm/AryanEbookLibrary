using System.Globalization;
using System.Text.Json.Serialization;

namespace AryanEbookLibrary.Models;

/// <summary>
/// What an annotation marks: a passage of text, a picture of part of a page, a page with only a note, or a page kept to
/// come back to (a bookmark, which may have a note too).
/// </summary>
public enum AnnotationKind
{
    Highlight = 0,
    Area = 1,
    PageNote = 2,
    Bookmark = 3,
}

/// <summary>
/// A highlight, a clipped area or a note on a page, in any book the app's own reader opens. One record for all of
/// them, as KOReader keeps: a highlight with a note is a note too. Where it is comes twice, as other readers learned
/// to: the exact place (<see cref="Anchor"/>) and the words with a little of what comes before and after them, so it
/// can be found again when the exact place no longer fits (another copy of the book, a changed file).
/// </summary>
/// <remarks>
/// Kept like a book's personal state: in the index, in a file beside the books on their drive and in the backup.
/// It is never written into the book itself. A deleted one stays as a <see cref="Deleted"/> marker, so a copy of the
/// file on another drive or computer cannot bring it back.
/// </remarks>
public sealed class Annotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AnnotationKind Kind { get; set; }

    /// <summary>
    /// The exact place, by the reader that made it: "epub1:" and a CFI; "pdf1:page:index:page:index" (the first and
    /// last character, both counted); "pdfarea1:page:x:y:w:h" and "comicarea1:page:x:y:w:h" (fractions of the page);
    /// "page1:page" for a note on a page of a PDF or a comic; "mark1:page" for a bookmark in a PDF or a comic, and
    /// "epubmark1:" and the CFI of the place for one in an EPUB.
    /// </summary>
    public string Anchor { get; set; } = "";

    /// <summary>The page it starts on, from 0: a PDF or comic page, or the EPUB reader's location.</summary>
    public int Page { get; set; }

    /// <summary>How far through the book, 0 to 1, for putting a book's annotations in order.</summary>
    public double Position { get; set; }

    public string Chapter { get; set; } = "";
    public string Quote { get; set; } = "";

    /// <summary>A little of the text before and after the quote, for finding it again.</summary>
    public string Before { get; set; } = "";
    public string After { get; set; } = "";

    /// <summary>1 to 5, as <see cref="HighlightColors"/> names them.</summary>
    public int Color { get; set; } = 1;
    public string Note { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }

    [JsonIgnore] public bool HasNote => Note.Trim().Length > 0;

    /// <summary>A highlight or a clipped area: the kinds with a colour, counted and listed as highlights.</summary>
    [JsonIgnore] public bool IsMark => Kind is AnnotationKind.Highlight or AnnotationKind.Area;

    /// <summary>The page as people count it, from 1.</summary>
    [JsonIgnore] public int PageNumber => Page + 1;

    /// <summary>
    /// Whether a picture of it is kept: a clipped area, and a PDF highlight, whose file may not spell its words the way
    /// the page shows them (Myanmar and Hindi PDFs rarely do).
    /// </summary>
    [JsonIgnore] public bool KeepsPicture => Kind == AnnotationKind.Area || Anchor.StartsWith("pdf1:", StringComparison.Ordinal);

    /// <summary>Whether it is shown as its picture rather than its words.</summary>
    [JsonIgnore] public bool ShowsPicture => Kind == AnnotationKind.Area || (KeepsPicture && !ReadsWell(Quote));

    /// <summary>
    /// Whether words taken from a PDF can be shown as text: not empty, and none of the scripts a PDF's text layer
    /// commonly scrambles (Myanmar, the Indic scripts), nor private-use or replacement characters.
    /// </summary>
    public static bool ReadsWell(string text)
    {
        if (text.Trim().Length == 0) return false;
        foreach (var c in text)
        {
            if (c is >= (char)0x0900 and <= (char)0x0DFF        // Devanagari to Sinhala
                or >= (char)0x1000 and <= (char)0x109F          // Myanmar
                or >= (char)0xA9E0 and <= (char)0xA9FF          // Myanmar Extended-B
                or >= (char)0xAA60 and <= (char)0xAA7F          // Myanmar Extended-A
                or >= (char)0xE000 and <= (char)0xF8FF          // private use: a font's own glyph numbers
                or (char)0xFFFD)
                return false;
            if (char.IsControl(c) && !char.IsWhiteSpace(c)) return false;
        }
        return true;
    }

    public Annotation Copy() => (Annotation)MemberwiseClone();

    /// <summary>The numbers of an anchor after its tag ("pdf1:3:10:3:42" gives 3, 10, 3, 42), or null.</summary>
    public static double[]? AnchorNumbers(string anchor, string tag)
    {
        if (!anchor.StartsWith(tag + ":", StringComparison.Ordinal)) return null;
        var parts = anchor[(tag.Length + 1)..].Split(':');
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return numbers;
    }

    public static string MakeAnchor(string tag, params double[] numbers) =>
        tag + ":" + string.Join(":", numbers.Select(n => n.ToString("0.#####", CultureInfo.InvariantCulture)));
}

/// <summary>The five highlight colours, in the order they are offered. An annotation keeps the number, not the colour.</summary>
public static class HighlightColors
{
    public static readonly (string Name, Windows.UI.Color Color)[] All =
    {
        ("Lime", Windows.UI.Color.FromArgb(0xFF, 0xB5, 0xE6, 0x1D)),
        ("Pink", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6F, 0xB5)),
        ("Orange", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9F, 0x43)),
        ("Red", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x5A, 0x5F)),
        ("Green", Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xCC, 0x71)),
    };

    public static int Count => All.Length;

    public static int Clamp(int color) => Math.Clamp(color, 1, All.Length);

    public static string Name(int color) => All[Clamp(color) - 1].Name;

    public static Windows.UI.Color Of(int color) => All[Clamp(color) - 1].Color;

    /// <summary>The colour with some of the page showing through, for marking text on a page.</summary>
    public static Windows.UI.Color Wash(int color, byte alpha = 0x66)
    {
        var c = Of(color);
        return Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    public static string Hex(int color)
    {
        var c = Of(color);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
