using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Books the catalogue has but the drive does not, and the ones that only moved somewhere else on the same
/// drive. Relinking carries the book's own row and everything the user gave it to the new path; removing
/// drops only the catalogue row, never a file.
/// </summary>
public sealed partial class MissingPage : Page
{
    private List<MissingEntry> _missing = new();
    private List<MovedBook> _moved = new();

    public MissingPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _missing = MissingBooks.All(AppServices.Repo);
        MissingList.ItemsSource = _missing;
        EmptyText.Visibility = _missing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FindMovedButton.IsEnabled = _missing.Any(m => m.IsConnected);
        ForgetButton.IsEnabled = _missing.Count > 0;

        var offline = _missing.Count(m => !m.IsConnected);
        SummaryText.Text = _missing.Count == 0
            ? "Nothing is missing."
            : $"{_missing.Count:N0} book(s) were not found by the last scan" +
              (offline > 0 ? $", {offline:N0} of them on a drive that is not connected now." : ".");

        ShowMoved();
    }

    private void ShowMoved()
    {
        MovedList.ItemsSource = _moved;
        MovedPanel.Visibility = _moved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MovedHeader.Text = _moved.Count == 1
            ? "1 book was found somewhere else on its drive"
            : $"{_moved.Count:N0} books were found somewhere else on their drive";
        RelinkAllButton.Visibility = _moved.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFindMoved(object sender, RoutedEventArgs e)
    {
        Busy.IsActive = true;
        FindMovedButton.IsEnabled = false;
        ResultText.Text = "Looking through the tracked folders...";
        try
        {
            var missing = _missing.Where(m => m.IsConnected).ToList();
            _moved = await Task.Run(() => MissingBooks.FindMoved(AppServices.Repo, missing));
            ResultText.Text = _moved.Count == 0
                ? "No file on the drives looks like any of these books."
                : $"{_moved.Count:N0} of {missing.Count:N0} found.";
        }
        catch (Exception ex)
        {
            ResultText.Text = "The search failed: " + ex.Message;
            Log.Write("Looking for moved books failed: " + ex);
        }
        finally
        {
            Busy.IsActive = false;
            FindMovedButton.IsEnabled = true;
            ShowMoved();
        }
    }

    private async void OnRelink(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovedBook moved) return;
        MissingBooks.Relink(AppServices.Repo, moved);
        _moved.Remove(moved);
        await AppServices.Library.ReloadAsync();
        ResultText.Text = $"\"{moved.Book.Title}\" now points at {moved.NewRelPath}.";
        Refresh();
    }

    private async void OnRelinkAll(object sender, RoutedEventArgs e)
    {
        var count = _moved.Count;
        foreach (var moved in _moved) MissingBooks.Relink(AppServices.Repo, moved);
        _moved.Clear();
        await AppServices.Library.ReloadAsync();
        ResultText.Text = $"{count:N0} book(s) relinked.";
        Refresh();
    }

    private async void OnForget(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MissingEntry book) return;
        AppServices.Library.RemoveMissing(new[] { book.Id });
        await AppServices.Library.ReloadAsync();
        Refresh();
    }

    private async void OnForgetAll(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || _missing.Count == 0) return;
        var confirm = await new ContentDialog
        {
            Title = "Remove from the catalogue",
            Content = $"{_missing.Count:N0} book(s) will be dropped from the catalogue. No file is deleted, and what " +
                      "you gave them (favorites, notes, tags) is kept in case they come back.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        }.ShowAsync();
        if (confirm != ContentDialogResult.Primary) return;

        AppServices.Library.RemoveMissing(_missing.Select(m => m.Id).ToList());
        await AppServices.Library.ReloadAsync();
        _moved.Clear();
        ResultText.Text = "Removed from the catalogue.";
        Refresh();
    }
}
