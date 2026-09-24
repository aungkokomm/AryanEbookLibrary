using System.Globalization;
using System.Text;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>One highlight or note as it is exported: what its card shows.</summary>
public sealed record ExportRow(
    string BookTitle,
    string Author,
    string File,
    string Kind,
    int Color,
    string Words,
    string Note,
    int Page,
    string Chapter,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? Picture);

/// <summary>
/// Highlights and notes written out for other apps: Markdown for notes apps (Obsidian, Notion, a plain text editor),
/// grouped by book with the pictures beside it, and CSV for a spreadsheet.
/// </summary>
public static class AnnotationExport
{
    public const string BookNoteKind = "Note on the book";

    /// <summary>What kind a card is, in words.</summary>
    public static string KindOf(Annotation a, bool bookNote) => bookNote ? BookNoteKind : a.Kind switch
    {
        AnnotationKind.Area => "Clip",
        AnnotationKind.PageNote => "Note on a page",
        _ => "Highlight",
    };

    /// <summary>
    /// The rows as Markdown, a section per book in the order they come. Pictures are linked in
    /// <paramref name="picturesFolder"/> (beside the file); <paramref name="pictures"/> gets the ones to copy there.
    /// </summary>
    public static string Markdown(IReadOnlyList<ExportRow> rows, string heading, string picturesFolder, List<string> pictures)
    {
        var sb = new StringBuilder();
        var books = rows.Select(r => (r.BookTitle, r.Author)).Distinct().Count();
        sb.Append("# ").AppendLine(heading).AppendLine();
        sb.Append("Aryan eBook Library, ").Append(DateTime.Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)).Append(". ")
          .Append(Count(rows.Count, heading.Contains("Note", StringComparison.OrdinalIgnoreCase) ? "note" : "highlight"))
          .Append(" from ").Append(Count(books, "book")).AppendLine(".");

        foreach (var group in rows.GroupBy(r => (r.BookTitle, r.Author)))
        {
            sb.AppendLine().Append("## ").AppendLine(OneLine(group.Key.BookTitle));
            if (group.Key.Author.Length > 0) sb.AppendLine(OneLine(group.Key.Author));
            foreach (var r in group)
            {
                sb.AppendLine();
                if (r.Words.Length > 0)
                {
                    foreach (var line in Lines(r.Words)) sb.AppendLine(line.Trim().Length > 0 ? "> " + line.TrimEnd() : ">");
                    sb.AppendLine();
                }
                if (r.Picture is not null)
                {
                    var name = Path.GetFileName(r.Picture);
                    pictures.Add(r.Picture);
                    sb.Append("![Picture of page ").Append(r.Page.ToString("N0", CultureInfo.CurrentCulture)).Append("](")
                      .Append(Uri.EscapeDataString(picturesFolder)).Append('/').Append(Uri.EscapeDataString(name)).AppendLine(")").AppendLine();
                }
                if (r.Note.Length > 0)
                {
                    var lines = Lines(r.Note);
                    sb.Append(r.Kind == BookNoteKind ? "**Note on the book:** " : "**Note:** ").AppendLine(lines[0]);
                    foreach (var line in lines.Skip(1)) sb.AppendLine(line);
                    sb.AppendLine();
                }
                sb.Append('*').Append(Where(r)).AppendLine("*");
            }
        }
        return sb.ToString();
    }

    /// <summary>The rows as CSV for a spreadsheet: every field quoted when it has to be, dates in local time.</summary>
    public static string Csv(IReadOnlyList<ExportRow> rows)
    {
        var sb = new StringBuilder();
        void Line(params string[] fields) => sb.Append(string.Join(",", fields.Select(Field))).Append("\r\n");
        Line("Book", "Author", "Kind", "Colour", "Words", "Note", "Page", "Chapter", "Created", "Updated", "File");
        foreach (var r in rows)
            Line(r.BookTitle, r.Author, r.Kind,
                 r.Kind is "Highlight" or "Clip" ? HighlightColors.Name(r.Color) : "",
                 r.Words, r.Note,
                 r.Page >= 1 ? r.Page.ToString(CultureInfo.InvariantCulture) : "",
                 r.Chapter,
                 Local(r.CreatedUtc), Local(r.UpdatedUtc), r.File);
        return sb.ToString();
    }

    /// <summary>"Page 12 · Chapter One · Lime · 24 Sep 2026".</summary>
    private static string Where(ExportRow r)
    {
        var parts = new List<string>();
        if (r.Page >= 1) parts.Add((r.Kind == "Note on a page" ? "Note on page " : "Page ") + r.Page.ToString("N0", CultureInfo.CurrentCulture));
        if (r.Chapter.Trim().Length > 0) parts.Add(OneLine(r.Chapter));
        if (r.Kind is "Highlight" or "Clip") parts.Add(HighlightColors.Name(r.Color));
        if (r.CreatedUtc > DateTime.MinValue) parts.Add(r.CreatedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        return string.Join(" " + (char)0x00B7 + " ", parts);
    }

    private static string Local(DateTime utc) =>
        utc > DateTime.MinValue ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    private static string Field(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string[] Lines(string text) => text.Trim().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string OneLine(string text) => string.Join(" ", Lines(text).Select(l => l.Trim()).Where(l => l.Length > 0));

    private static string Count(int n, string what) => n == 1 ? "1 " + what : n.ToString("N0", CultureInfo.CurrentCulture) + " " + what + "s";
}
