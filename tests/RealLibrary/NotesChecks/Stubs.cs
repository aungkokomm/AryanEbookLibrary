using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services
{
    // Only the shape the repository needs; the harness never reads missing books.
    public sealed record MissingEntry(long Id, string DriveId, string DriveLabel, string RelPath, string Title,
        string Author, long FileSize, BookFormat Format);
}

namespace AryanEbookLibrary.Services.Online
{
    // Book.ShowDetails asks these about online details, which the harness never adds.
    public static class OnlineMatcher
    {
        public static bool SameWords(string a, string b) => a == b;
        public static bool HasNoTitleWords(string title) => false;
    }
}
