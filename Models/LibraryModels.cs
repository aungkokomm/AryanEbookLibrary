namespace AryanEbookLibrary.Models;

/// <summary>A physical drive/volume, identified by volume serial number (label is only cosmetic and renameable).</summary>
public sealed class DriveRecord
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string LastRoot { get; set; } = "";
    public DateTime? LastSeenUtc { get; set; }
}

/// <summary>A folder the user added to the library. RelPath is relative to the drive root ("" = whole drive).</summary>
public sealed class LibraryFolder
{
    public long Id { get; set; }
    public string DriveId { get; set; } = "";
    public string RelPath { get; set; } = "";
}

/// <summary>A folder chip on a drive card.</summary>
public sealed class FolderItem
{
    public LibraryFolder Folder { get; init; } = new();
    public int BookCount { get; init; }
    public string DisplayName => string.IsNullOrEmpty(Folder.RelPath) ? "(entire drive)" : Folder.RelPath;
    public string BookCountText => BookCount == 1 ? "1 book" : $"{BookCount:N0} books";
}

/// <summary>One card on the Drives page (CineLibrary's DriveInfo).</summary>
public sealed class DriveItem
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsConnected { get; init; }
    public bool IsOffline => !IsConnected;
    public string Root { get; init; } = "";            // current root when connected, last seen root otherwise
    public int BookCount { get; init; }
    public int MissingCount { get; init; }
    public List<FolderItem> Folders { get; init; } = new();

    public string StatusText => IsConnected ? $"Connected as {Root.TrimEnd('\\')}" : "Not connected";
    public string BookCountText => $"{BookCount:N0} {(BookCount == 1 ? "book" : "books")}";
    public string FolderCountText => $"{Folders.Count} {(Folders.Count == 1 ? "folder" : "folders")}";
    public bool HasFolders => Folders.Count > 0;
    public bool HasMissing => MissingCount > 0;
    public string MissingButtonText => $"Review {MissingCount:N0}…";
    public string MissingInfoText => MissingCount == 1
        ? "1 book wasn't found in the last scan. It may have been moved or deleted."
        : $"{MissingCount:N0} books weren't found in the last scan. They may have been moved or deleted.";
}
