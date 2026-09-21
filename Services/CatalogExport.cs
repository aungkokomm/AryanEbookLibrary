using System.Text;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// The whole catalogue as one CSV file: every book with what the app knows about it, so the library can be
/// printed, searched in a spreadsheet or taken somewhere else. Written UTF-8 with a byte order mark, which
/// is what Excel needs to show Burmese and Hindi correctly.
/// </summary>
public static class CatalogExport
{
    private static readonly string[] Columns =
    {
        "Title", "Author", "Series", "Series index", "Year", "Publisher", "Language", "Format", "Size (MB)",
        "ISBN", "Subjects", "Tags", "Rating", "Status", "Progress", "Favorite", "Notes",
        "Added", "Last opened", "Finished", "Drive", "Path", "Available"
    };

    public static int Csv(string path, IEnumerable<Book> books)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns));

        var count = 0;
        foreach (var b in books)
        {
            var row = new[]
            {
                b.Title,
                b.Author,
                b.Series,
                b.SeriesIndex?.ToString("0.##") ?? "",
                b.Year?.ToString() ?? "",
                b.Publisher,
                b.Language,
                b.FormatLabel,
                (b.FileSize / 1048576.0).ToString("0.0"),
                b.Isbn,
                b.Subjects,
                b.UserTags,
                b.Rating > 0 ? b.Rating.ToString() : "",
                b.StatusText,
                b.Progress > 0 ? b.Progress + "%" : "",
                b.IsFavorite ? "yes" : "",
                b.Notes,
                Date(b.AddedUtc),
                Date(b.LastOpenedUtc),
                Date(b.FinishedUtc),
                b.DriveLabel,
                b.RelPath,
                b.IsAvailable ? "yes" : "offline"
            };
            sb.AppendLine(string.Join(',', row.Select(Field)));
            count++;
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return count;
    }

    private static string Date(DateTime? value) => value?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    /// <summary>One CSV field: quoted when it holds a comma, a quote or a line break.</summary>
    private static string Field(string? value)
    {
        var s = (value ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return s.Contains(',') || s.Contains('"') ? '"' + s.Replace("\"", "\"\"") + '"' : s;
    }
}
