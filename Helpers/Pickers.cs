using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace AryanEbookLibrary.Helpers;

/// <summary>
/// Windows' windows for choosing a folder or a file, answered with a path.
/// </summary>
/// <remarks>
/// ⚠️ THE WINDOWS APP SDK'S PICKERS, NOT Windows.Storage.Pickers. Those are run for the app by a separate Windows
/// process (PickerHost.exe) and fail outright on some PCs, notably when the app runs as administrator: a reader on
/// Windows 10 could not add a folder at all and got an "Error" box with nothing in it (issue #1). These run inside the
/// app, and a path is all any caller here ever used.
/// </remarks>
internal static class Pickers
{
    public static async Task<string?> FolderAsync(Window owner, PickerLocationId start)
    {
        var picker = new FolderPicker(owner.AppWindow.Id) { SuggestedStartLocation = start };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(
        Window owner, string name, string kind, string extension, PickerLocationId start = PickerLocationId.Unspecified)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id) { SuggestedFileName = name, SuggestedStartLocation = start };
        picker.FileTypeChoices.Add(kind, new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static async Task<string?> OpenAsync(Window owner, string extension)
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id);
        picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
