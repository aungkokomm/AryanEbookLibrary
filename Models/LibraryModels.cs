using CommunityToolkit.Mvvm.ComponentModel;

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

public sealed class FolderItem : ObservableObject
{
    public LibraryFolder Folder { get; init; } = new();
    public string DisplayPath { get; init; } = "";
    public int BookCount { get; init; }
    public string BookCountText => BookCount == 1 ? "1 book" : $"{BookCount} books";
}

public sealed class DriveItem : ObservableObject
{
    public string Id { get; init; } = "";

    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }

    private bool _isOnline;
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetProperty(ref _isOnline, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    public string Root { get; init; } = "";
    public string StatusText => IsOnline ? "CONNECTED" : "OFFLINE";
    public List<FolderItem> Folders { get; init; } = new();
}
