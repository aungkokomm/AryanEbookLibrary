using System.Diagnostics;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Opens a book: in the app's own reader when it reads that kind of file (and Settings says to), otherwise in the
/// user's default app. Like CineLibrary's Play button, it only works when the drive is connected.
/// </summary>
public static class BookLauncher
{
    /// <summary>The app's own reader, set by the app at start. Returns an error message, or null when it opened.</summary>
    public static Func<Book, string?>? InAppReader { get; set; }

    /// <summary>Whether "Open" goes to the app's own reader for this book.</summary>
    public static bool ReadsInApp(Book book) => InAppReader is not null && book.Format switch
    {
        BookFormat.Pdf => AppServices.Settings.ReadPdfInApp,
        BookFormat.Epub or BookFormat.Mobi or BookFormat.Azw3 => AppServices.Settings.ReadEpubInApp,
        _ => false,
    };

    public static string? Open(Book book, bool withDefaultApp = false)
    {
        var path = book.FullPath;
        if (path is null) return $"The drive \"{book.DriveLabel}\" is not connected.";
        if (!File.Exists(path)) return "The file no longer exists. Rescan the library to update the catalog.";

        if (!withDefaultApp && ReadsInApp(book)) return InAppReader!(book);

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Open failed for {path}: {ex.Message}");
            return "No default app is set for this file type (" + book.FormatLabel + ").";
        }
    }

    public static void ShowInFolder(Book book)
    {
        var path = book.FullPath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("ShowInFolder failed: " + ex.Message);
        }
    }
}
