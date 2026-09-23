using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Books that look like they are in the library twice, with the reason for each group. Files are only
/// shown, never deleted or moved: "Show in folder" hands the decision to the user.
/// </summary>
public sealed partial class DuplicatesPage : Page
{
    public DuplicatesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private List<DuplicateGroup> _groups = new();

    private void Refresh()
    {
        var settings = AppServices.Settings;
        var rule = new KeepRule
        {
            Language = settings.PreferredLanguage,
            Format = settings.PreferredFormat,
            Chosen = settings.KeptCopies
        };
        _groups = DuplicateFinder.Find(AppServices.Library.AllBooks, rule);
        var duplicates = _groups.Count(g => !g.SameBookOtherFormat);
        var editions = _groups.Count - duplicates;

        GroupList.ItemsSource = null;
        GroupList.ItemsSource = _groups;
        EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _groups.Count == 0
            ? "Every book in the catalogue is there once."
            : $"{duplicates:N0} book(s) look like they are here twice" +
              (editions > 0 ? $", and {editions:N0} are here in another format or language, which is not a duplicate." : ".");
        RuleText.Text = "Nothing here is deleted. The copy marked Keep is the one the rule picks: " +
                        RuleLine() + " Pick another one yourself with \"Keep this one\".";
    }

    private static string RuleLine()
    {
        var format = AppServices.Settings.PreferredFormat;
        var language = AppServices.Settings.PreferredLanguage;
        var parts = new List<string>();
        if (language.Length > 0) parts.Add(DuplicateFinder.LanguageName(language));
        if (format.Length > 0) parts.Add(format);
        return parts.Count == 0
            ? "the biggest file, until you choose a format or a language in Settings."
            : string.Join(", then ", parts) + ", then the biggest file.";
    }

    private void OnKeepThisOne(object sender, RoutedEventArgs e)
    {
        // Only this group changes, so the list is left alone and the page does not jump back to the top.
        if ((sender as Button)?.Tag is not Book b) return;
        _groups.FirstOrDefault(g => g.Books.Contains(b))?.KeepInstead(b);
        AppServices.Settings.Save();
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Book b) BookLauncher.ShowInFolder(b);
    }

    private bool _dialogOpen;

    private async void OnDetails(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Book b || _dialogOpen || XamlRoot is null) return;
        _dialogOpen = true;
        BookDetailsDialog dialog;
        try
        {
            dialog = new BookDetailsDialog(b, AppServices.Library) { XamlRoot = XamlRoot };
            await dialog.ShowThemedAsync();
        }
        finally
        {
            _dialogOpen = false;
        }

        if (dialog.Next == DetailsNext.FindOnline)
        {
            _dialogOpen = true;
            try
            {
                await new FindOnlineDialog(b) { XamlRoot = XamlRoot }.ShowThemedAsync();
            }
            finally
            {
                _dialogOpen = false;
            }
        }
        Refresh();
    }
}
