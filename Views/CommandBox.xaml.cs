using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AryanEbookLibrary.Views;

/// <summary>One thing the command box can go to or do. Rank orders the kinds when matches are equally good.</summary>
public sealed record CommandItem(string Text, string Kind, int Rank, Action Run, string Also = "")
{
    /// <summary>What the typed words are looked for in: the name, and for a book its author.</summary>
    public string Haystack { get; } = Also.Length > 0 ? Text + " " + Also : Text;
}

/// <summary>
/// Ctrl+K: every page, shelf, list, tag, series, author and book in one box. The best matches come first (the whole
/// name, then its start, then a word's start), places and actions before books. The choice runs once the box has
/// closed, so it may open a dialog of its own.
/// </summary>
public sealed partial class CommandBox : ContentDialog
{
    public const int PlaceRank = 0, GroupRank = 1, NameRank = 2, BookRank = 3;
    private const int Shown = 60;

    private readonly List<CommandItem> _items;

    public CommandItem? Chosen { get; private set; }

    public CommandBox(List<CommandItem> items)
    {
        _items = items;
        InitializeComponent();
        Opened += (_, _) => QueryBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Refresh();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var query = QueryBox.Text.Trim();
        List<CommandItem> shown;
        if (query.Length == 0)
        {
            shown = _items.Where(i => i.Rank <= GroupRank).Take(Shown).ToList();
        }
        else
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            shown = _items
                .Select(i => (Item: i, Score: Score(i, query, words)))
                .Where(m => m.Score >= 0)
                .OrderBy(m => m.Score).ThenBy(m => m.Item.Rank).ThenBy(m => m.Item.Text.Length)
                .ThenBy(m => m.Item.Text, StringComparer.CurrentCultureIgnoreCase)
                .Take(Shown)
                .Select(m => m.Item)
                .ToList();
        }
        Results.ItemsSource = shown;
        if (shown.Count > 0) Results.SelectedIndex = 0;
        Hint.Text = shown.Count == 0 ? "Nothing matches" : "Up and Down to choose, Enter to go, Esc to close";
    }

    /// <summary>-1 when a word is missing; else 0 for the whole name, 1 for its start, 2 for a word's start, 3 anywhere.</summary>
    private static int Score(CommandItem item, string query, string[] words)
    {
        const StringComparison Ignore = StringComparison.CurrentCultureIgnoreCase;
        foreach (var word in words)
            if (!item.Haystack.Contains(word, Ignore)) return -1;
        if (item.Text.Equals(query, Ignore)) return 0;
        if (item.Text.StartsWith(query, Ignore)) return 1;
        if (item.Text.Split(' ').Any(w => w.StartsWith(words[0], Ignore))) return 2;
        return 3;
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = Results.Items.Count;
        switch (e.Key)
        {
            case VirtualKey.Down when count > 0:
                Results.SelectedIndex = Math.Min(count - 1, Results.SelectedIndex + 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.Up when count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                Go(Results.SelectedItem as CommandItem);
                e.Handled = true;
                break;
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e) => Go(e.ClickedItem as CommandItem);

    private void Go(CommandItem? item)
    {
        if (item is null) return;
        Chosen = item;
        Hide();
    }
}
