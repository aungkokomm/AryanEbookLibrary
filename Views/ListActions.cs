using System.Runtime.InteropServices.WindowsRuntime;
using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace AryanEbookLibrary.Views;

/// <summary>
/// What a list in the navigation pane offers on right-click besides Rename and Delete (CineLibrary's list menu):
/// copy its books' files to a folder, and save the list as a picture of its covers to share.
/// </summary>
internal static class ListActions
{
    // ------------------------------------------------------------ copy books to folder

    public static async Task CopyToFolderAsync(Window owner, XamlRoot root, string list)
    {
        var vm = AppServices.Library;
        var books = vm.BooksInList(list);
        if (books.Count == 0)
        {
            await Say(root, "Nothing to copy", $"“{list}” has no books on it yet.");
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var folder = await PickAsync(root, picker.PickSingleFolderAsync);
        if (folder is null) return;
        var destination = folder.Path;

        var plan = await Task.Run(() => ListCopy.Plan(books, destination));
        if (plan.Items.Count == 0)
        {
            await Say(root, "Cannot copy yet", plan.OfflineDrives.Count > 0
                ? "Every book on the list is on a drive that is not plugged in:\n• " + string.Join("\n• ", plan.OfflineDrives) + "\n\nPlug it in and try again."
                : "The files of these books are not where the last scan found them. Refresh the drive and try again.");
            return;
        }

        // Some books wait on an unplugged drive: say which, and let the user plug it in first (the safer default).
        if (plan.OfflineDrives.Count > 0)
        {
            var go = await new ContentDialog
            {
                Title = plan.OfflineDrives.Count == 1 ? "A drive is not plugged in" : $"{plan.OfflineDrives.Count} drives are not plugged in",
                Content = $"{Books(plan.OfflineBooks)} on this list cannot be copied until these are plugged in:\n• " +
                          string.Join("\n• ", plan.OfflineDrives) + $"\n\nPlug them in first, or copy the {Books(plan.Items.Count)} that are here.",
                PrimaryButtonText = $"Copy {Books(plan.Items.Count)}",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = root
            }.ShowThemedAsync();
            if (go != ContentDialogResult.Primary) return;
        }

        var existing = plan.Existing();
        var overwrite = false;
        if (existing.Count > 0)
        {
            var shown = string.Join("\n", existing.Take(5).Select(i => "• " + ShortName(Path.GetFileName(i.Target))));
            if (existing.Count > 5) shown += $"\n…and {existing.Count - 5} more";
            var answer = await new ContentDialog
            {
                Title = existing.Count == 1 ? "A file is already there" : $"{existing.Count} files are already there",
                Content = $"What should happen to the copies already in {destination}?\n\n{shown}",
                PrimaryButtonText = "Skip them",
                SecondaryButtonText = "Replace them",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root
            }.ShowThemedAsync();
            if (answer == ContentDialogResult.None) return;
            overwrite = answer == ContentDialogResult.Secondary;
        }

        var needed = plan.BytesToCopy(overwrite);
        var free = ListCopy.FreeBytes(destination);
        if (free >= 0 && free < needed)
        {
            await Say(root, "Not enough room", $"The books need {Size(needed)}, and only {Size(free)} is free there.");
            return;
        }

        var result = await RunWithProgressAsync(root, plan, overwrite, destination, list);
        var parts = new List<string>();
        if (result.Copied > 0) parts.Add($"{Books(result.Copied)} copied");
        if (result.Skipped > 0) parts.Add($"{result.Skipped} already there");
        if (result.Failed > 0) parts.Add($"{result.Failed} could not be copied (see the log)");
        if (plan.OfflineBooks > 0) parts.Add($"{plan.OfflineBooks} on unplugged drives");
        if (plan.MissingFiles > 0) parts.Add($"{plan.MissingFiles} not found");
        // The folder's name, not its whole path, which ran the status bar to three lines.
        var place = Path.GetFileName(destination.TrimEnd('\\', '/'));
        if (place.Length == 0) place = destination;
        vm.Tell((result.Cancelled ? "Copy stopped: " : $"“{list}” to “{place}”: ") + (parts.Count == 0 ? "nothing copied" : string.Join(", ", parts)));
    }

    /// <summary>A dialog with a bar while the files copy; its button stops the copy, and it closes itself when done.</summary>
    private static async Task<ListCopy.Result> RunWithProgressAsync(XamlRoot root, ListCopy plan, bool overwrite, string destination, string list)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, plan.BytesToCopy(overwrite)), Height = 6 };
        var bookText = new TextBlock();
        var bytesText = new TextBlock { Style = (Style)Application.Current.Resources["PageHintStyle"] };
        var fileText = new TextBlock { Style = (Style)Application.Current.Resources["PageHintStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var dialog = new ContentDialog
        {
            Title = $"Copying “{list}”",
            Content = new StackPanel { Spacing = 10, Width = 460, Children = { bookText, bar, bytesText, fileText } },
            CloseButtonText = "Stop",
            XamlRoot = root
        };
        using var cts = new CancellationTokenSource();
        var finished = false;
        dialog.Closing += (_, _) => { if (!finished) cts.Cancel(); };

        var progress = new Progress<ListCopy.Progress>(p =>
        {
            bar.Value = p.BytesDone;
            bookText.Text = $"Book {p.Index} of {p.Count}";
            bytesText.Text = $"{Size(p.BytesDone)} of {Size(p.BytesTotal)}  ·  to {destination}";
            fileText.Text = p.Name;
        });
        bookText.Text = "Starting…";

        var run = plan.RunAsync(overwrite, progress, cts.Token);
        _ = run.ContinueWith(_ => dialog.DispatcherQueue.TryEnqueue(() =>
        {
            finished = true;
            dialog.Hide();
        }), TaskScheduler.Default);
        // A few small files can be copied before the dialog has even opened, when hiding it does nothing yet.
        dialog.Opened += (_, _) =>
        {
            if (!run.IsCompleted) return;
            finished = true;
            dialog.Hide();
        };
        await dialog.ShowThemedAsync();
        return await run;
    }

    // ------------------------------------------------------------ export as image

    /// <summary>
    /// The list's covers as one PNG, to share: a dark card with the list's name, how many books, and up to 48
    /// covers four to a row (CineLibrary's list poster). A book without a cover shows its title instead.
    /// </summary>
    public static async Task ExportImageAsync(Window owner, XamlRoot root, string list)
    {
        var vm = AppServices.Library;
        var books = vm.BooksInList(list);
        if (books.Count == 0)
        {
            await Say(root, "Nothing to show", $"“{list}” has no books on it yet.");
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"{SafeName(list)}-{DateTime.Now:yyyyMMdd}"
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var file = await PickAsync(root, picker.PickSaveFileAsync);
        if (file is null) return;

        vm.Tell($"Making the picture of “{list}”…");
        var ok = await RenderAsync(root, list, books, file.Path);
        vm.Tell(ok ? $"Saved the picture of “{list}” as {file.Name}" : $"Could not make the picture of “{list}” (see the log)");
    }

    /// <summary>
    /// Builds the picture from elements placed in the window's own tree, pushed far off screen: an element in a
    /// Popup is not always drawn, and then RenderTargetBitmap captures nothing (CineLibrary found this).
    /// </summary>
    private static async Task<bool> RenderAsync(XamlRoot root, string list, List<Book> books, string outPath)
    {
        if (root.Content is not Panel host) return false;

        const int coverW = 180, coverH = 270, gap = 14, padding = 28, headerH = 92;
        var shown = books.Take(48).ToList();
        var cols = Math.Min(4, Math.Max(2, shown.Count));
        var rows = (shown.Count + cols - 1) / cols;
        var width = cols * coverW + (cols - 1) * gap + padding * 2;
        var height = headerH + rows * coverH + (rows - 1) * gap + padding;   // the top padding is inside the header

        var card = new Grid
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x14, 0x1C)),
            RequestedTheme = ElementTheme.Dark,
            RenderTransform = new TranslateTransform { X = -50000, Y = -50000 },
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerH) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Spacing = 4, Padding = new Thickness(padding, padding, padding, 0) };
        header.Children.Add(new TextBlock
        {
            Text = list,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(new TextBlock
        {
            Text = (shown.Count == books.Count ? Books(books.Count) : $"{shown.Count} of {books.Count} books") + "  ·  Aryan eBook Library",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF))
        });
        card.Children.Add(header);

        var grid = new Grid { Margin = new Thickness(padding, 0, padding, padding), ColumnSpacing = gap, RowSpacing = gap };
        for (var c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(coverW) });
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(coverH) });
        Grid.SetRow(grid, 1);
        card.Children.Add(grid);

        var images = new List<Task>();
        for (var i = 0; i < shown.Count; i++)
        {
            var cell = await CoverCellAsync(shown[i], coverW, coverH, images);
            Grid.SetColumn(cell, i % cols);
            Grid.SetRow(cell, i / cols);
            grid.Children.Add(cell);
        }

        Canvas.SetZIndex(card, -1);
        if (host is Grid hostGrid) Grid.SetRowSpan(card, Math.Max(1, hostGrid.RowDefinitions.Count));
        host.Children.Add(card);
        try
        {
            card.UpdateLayout();
            await WaitForImagesAsync(images, 8000);
            card.UpdateLayout();
            await Task.Delay(50);

            var target = new RenderTargetBitmap();
            await target.RenderAsync(card, width, height);
            if (target.PixelWidth == 0 || target.PixelHeight == 0) return false;
            var pixels = await target.GetPixelsAsync();

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var stream = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("List picture failed: " + ex);
            return false;
        }
        finally
        {
            host.Children.Remove(card);
        }
    }

    private static async Task<FrameworkElement> CoverCellAsync(Book book, int w, int h, List<Task> pending)
    {
        var cell = new Grid
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x26, 0x36))
        };
        cell.Children.Add(new TextBlock
        {
            Text = book.Title,
            Foreground = new SolidColorBrush(Colors.White),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14)
        });
        if (book.CoverPath is not { } path) return cell;
        try
        {
            var bytes = await Task.Run(() => File.Exists(path) ? File.ReadAllBytes(path) : null);
            if (bytes is null) return cell;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            // Listened to from the start: a cover already decoded does not say so again once it is on the card,
            // and waiting for that cost every picture the whole time-out.
            var bitmap = new BitmapImage { DecodePixelWidth = w * 2 };
            var loaded = new TaskCompletionSource();
            bitmap.ImageOpened += (_, _) => loaded.TrySetResult();
            bitmap.ImageFailed += (_, _) => loaded.TrySetResult();
            pending.Add(loaded.Task);
            await bitmap.SetSourceAsync(stream);
            if (bitmap.PixelWidth > 0) loaded.TrySetResult();
            cell.Children.Add(new Image { Stretch = Stretch.UniformToFill, Source = bitmap });
        }
        catch (Exception)
        {
            // an unreadable cover: the title shows
        }
        return cell;
    }

    /// <summary>Until every cover is decoded, or the time is up so one bad file cannot hold the picture back.</summary>
    private static async Task WaitForImagesAsync(List<Task> covers, int timeoutMs) =>
        await Task.WhenAny(Task.WhenAll(covers), Task.Delay(timeoutMs));

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Windows' own folder or save window. It runs in a process of its own and can fail (E_FAIL) instead of
    /// answering; then the user is told, rather than the click seeming to do nothing (it had reached the app's
    /// last-resort handler, which only logs).
    /// </summary>
    private static async Task<T?> PickAsync<T>(XamlRoot root, Func<Windows.Foundation.IAsyncOperation<T>> pick) where T : class
    {
        try
        {
            return await pick();
        }
        catch (Exception ex)
        {
            Log.Write($"Picker failed: 0x{ex.HResult:X8} {ex.Message}");
            await Say(root, "Please try again", "Windows' window for choosing where the files go ran into a problem, so nothing was saved.");
            return null;
        }
    }

    private static async Task Say(XamlRoot root, string title, string text) =>
        await new ContentDialog { Title = title, Content = text, CloseButtonText = "OK", XamlRoot = root }.ShowThemedAsync();

    private static string Books(int n) => n == 1 ? "1 book" : $"{n:N0} books";

    private static string Size(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }

    /// <summary>A long file name with its middle left out, so it fits a dialog line without breaking mid-word.</summary>
    private static string ShortName(string name) => name.Length <= 48 ? name : name[..30] + "…" + name[^15..];

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
