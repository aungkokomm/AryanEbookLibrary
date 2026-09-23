namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>
/// How a search compares text (from Ayaan PDF's DocumentSearch). ORDINAL either way, never
/// culture-aware: under a Turkish culture "I" stops matching "i" on one machine only.
/// </summary>
public readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false)
{
    public StringComparison Comparison =>
        MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>One occurrence of a search: the page, and where in that page's text.</summary>
public readonly record struct SearchMatch(int PageIndex, int Start, int Length);
