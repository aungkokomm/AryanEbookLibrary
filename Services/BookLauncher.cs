using System.Diagnostics;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services;

/// <summary>Opens a book in the user's default reader (like CineLibrary's Play button: only works when the drive is connected).</summary>
public static class BookLauncher
{
    public static string? Open(Book book)
    {
        var path = book.FullPath;
        if (path is null) return $"The drive \"{book.DriveLabel}\" is not connected.";
        if (!File.Exists(path)) return "The file no longer exists. Rescan the library to update the catalog.";

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
