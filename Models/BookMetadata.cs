namespace AryanEbookLibrary.Models;

/// <summary>What a metadata reader extracts from a book file (or a Calibre metadata.opf).</summary>
public sealed class BookMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Series { get; set; }
    public double? SeriesIndex { get; set; }
    public string? Publisher { get; set; }
    public int? Year { get; set; }
    public string? Language { get; set; }
    public string? Description { get; set; }
    public string? Isbn { get; set; }
    public string? Subjects { get; set; }
    public int? PageCount { get; set; }
    public byte[]? Cover { get; set; }
    public string CoverExt { get; set; } = ".jpg";

    /// <summary>Which details were taken from the file name rather than the book (<see cref="NameField"/> bits).</summary>
    public int NameFields { get; set; }

    /// <summary>The first page of a PDF is a page of text (a copyright page, a scan's notes), not a cover.</summary>
    public bool CoverIsTextPage { get; set; }

    /// <summary>
    /// A publisher the book shows only indirectly: its copyright page, or the website its author field names. Used when
    /// neither the book's metadata nor its file name says one, which name the edition more surely.
    /// </summary>
    public string? FallbackPublisher { get; set; }

    public static class NameField
    {
        public const int Title = 1, Author = 2, Series = 4, Year = 8, Publisher = 16;
    }

    /// <summary>Copy values from <paramref name="o"/>. With overwrite=false only empty fields are filled.</summary>
    public void MergeFrom(BookMetadata o, bool overwrite)
    {
        Title = Pick(Title, o.Title, overwrite);
        Author = Pick(Author, o.Author, overwrite);
        Series = Pick(Series, o.Series, overwrite);
        Publisher = Pick(Publisher, o.Publisher, overwrite);
        Language = Pick(Language, o.Language, overwrite);
        Description = Pick(Description, o.Description, overwrite);
        Isbn = Pick(Isbn, o.Isbn, overwrite);
        Subjects = Pick(Subjects, o.Subjects, overwrite);

        if (o.SeriesIndex.HasValue && (overwrite || !SeriesIndex.HasValue)) SeriesIndex = o.SeriesIndex;
        if (o.Year.HasValue && (overwrite || !Year.HasValue)) Year = o.Year;
        if (o.PageCount.HasValue && (overwrite || !PageCount.HasValue)) PageCount = o.PageCount;
        if (o.Cover is { Length: > 0 } && (overwrite || Cover is null || Cover.Length == 0))
        {
            Cover = o.Cover;
            CoverExt = o.CoverExt;
        }
    }

    private static string? Pick(string? mine, string? theirs, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(theirs)) return mine;
        if (overwrite || string.IsNullOrWhiteSpace(mine)) return theirs;
        return mine;
    }
}
