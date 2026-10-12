using System.Text.RegularExpressions;
using AryanEbookLibrary.Reader.Define;
using AryanEbookLibrary.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AryanEbookLibrary.Views;

/// <summary>
/// Words you looked up: one card a word, with its meaning, the sentences you met it in (the word in bold) and the books.
/// Forget takes a word off; Export makes Anki cards or a spreadsheet of what is shown.
/// </summary>
public sealed partial class WordsPage : Page
{
    private const int MaxShown = 300;

    private List<WordLookup> _all = new();
    private WordDefinitions? _dictionary;
    private readonly DispatcherQueueTimer _filterTimer;

    public WordsPage()
    {
        InitializeComponent();
        _filterTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _filterTimer.Interval = TimeSpan.FromMilliseconds(200);
        _filterTimer.IsRepeating = false;
        _filterTimer.Tick += (_, _) => Show();
        Loaded += async (_, _) =>
        {
            _all = AppServices.Repo.GetLookups();
            Show();
            _dictionary = await DefinitionDictionary.LoadAsync();
            Show();   // now with meanings
        };
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>The words that match the box: in the word, a sentence or a book's title.</summary>
    private List<List<WordLookup>> Shown()
    {
        var find = FilterBox.Text.Trim();
        var groups = WordList.Grouped(_all);
        return find.Length == 0
            ? groups
            : groups.Where(g => g.Any(u => Has(u.Word) || Has(u.Sentence) || Has(u.BookTitle))).ToList();

        bool Has(string text) => text.Contains(find, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Show()
    {
        var shown = Shown();
        var books = _all.Select(u => u.BookKey).Distinct().Count();
        var words = WordList.Grouped(_all).Count;
        SummaryText.Text = words == 0 ? "No words yet."
            : $"{Fn(words, "word")} looked up in {Fn(books, "book")}" + (shown.Count != words ? $", {shown.Count:N0} shown" : "");
        ExportBtn.IsEnabled = shown.Count > 0;

        WordsPanel.Children.Clear();
        if (words == 0)
        {
            WordsPanel.Children.Add(Hint("Right-click a word while you read (or select it) and choose Define. Each word you look up is kept here."));
            return;
        }
        if (shown.Count == 0)
        {
            WordsPanel.Children.Add(Hint("No word, sentence or book has that in it."));
            return;
        }
        foreach (var uses in shown.Take(MaxShown)) WordsPanel.Children.Add(Card(uses));
        if (shown.Count > MaxShown)
            WordsPanel.Children.Add(Hint($"The {MaxShown:N0} words looked up last are shown. Type in the box above to find the others."));

        static string Fn(int n, string what) => n == 1 ? $"1 {what}" : $"{n:N0} {what}s";
    }

    private Border Card(List<WordLookup> uses)
    {
        var word = uses[0].Word;
        var panel = new StackPanel { Spacing = 6 };

        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Run { Text = word });
        var times = uses.Sum(u => u.Times);
        if (times > 1) title.Inlines.Add(new Run { Text = $"   looked up {times:N0} times", FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Brush("TextFillColorTertiaryBrush") });
        head.Children.Add(title);
        var forget = new Button { Content = "Forget", FontSize = 12, Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(forget, "Take this word off the list, every time it was looked up");
        forget.Click += (_, _) =>
        {
            AppServices.Repo.DeleteLookups(word);
            _all.RemoveAll(u => string.Equals(u.Word, word, StringComparison.OrdinalIgnoreCase));
            Show();
        };
        Grid.SetColumn(forget, 1);
        head.Children.Add(forget);
        panel.Children.Add(head);

        var meaning = WordList.Meaning(_dictionary?.Lookup(word));
        if (meaning.Length > 0)
            panel.Children.Add(new TextBlock { Text = meaning, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFillColorSecondaryBrush") });

        foreach (var use in uses.Take(3))
        {
            if (use.Sentence.Length > 0)
            {
                var sentence = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, 4, 0, 0) };
                Bold(sentence.Inlines, use.Sentence, word);
                panel.Children.Add(sentence);
            }
            var where = string.Join(", ", new[] { use.BookTitle, use.Place, use.LookedUtc.ToLocalTime().ToString("d MMM yyyy") }.Where(s => s.Length > 0));
            panel.Children.Add(new TextBlock
            {
                Text = where, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("TextFillColorTertiaryBrush")
            });
        }
        if (uses.Count > 3)
            panel.Children.Add(new TextBlock { Text = $"and {uses.Count - 3:N0} more", FontSize = 12, Foreground = Brush("TextFillColorTertiaryBrush") });

        return new Border { Style = (Style)Application.Current.Resources["CardPanelStyle"], Child = panel };
    }

    /// <summary>The sentence, with every place the word is in it in bold.</summary>
    private static void Bold(InlineCollection inlines, string sentence, string word)
    {
        var at = 0;
        foreach (Match m in Regex.Matches(sentence, @"(?<!\w)" + Regex.Escape(word) + @"(?!\w)", RegexOptions.IgnoreCase))
        {
            if (m.Index > at) inlines.Add(new Run { Text = sentence[at..m.Index] });
            inlines.Add(new Run { Text = m.Value, FontWeight = FontWeights.Bold });
            at = m.Index + m.Length;
        }
        if (at < sentence.Length) inlines.Add(new Run { Text = sentence[at..] });
    }

    private static TextBlock Hint(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // ---- export ----

    private async void OnExportAnki(object sender, RoutedEventArgs e) => await ExportAsync(anki: true);

    private async void OnExportCsv(object sender, RoutedEventArgs e) => await ExportAsync(anki: false);

    /// <summary>The words shown (all of them, or the ones the box finds), as Anki cards or a spreadsheet.</summary>
    private async Task ExportAsync(bool anki)
    {
        var words = Shown().SelectMany(g => g).ToList();
        if (words.Count == 0) return;
        var file = await ListActions.PickAsync(XamlRoot, () => Helpers.Pickers.SaveAsync(App.MainWindow!,
            $"Aryan words {DateTime.Now:yyyy-MM-dd}", anki ? "Text for Anki" : "CSV (comma separated)", anki ? ".txt" : ".csv",
            Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary));
        if (file is null) return;
        try
        {
            _dictionary ??= await DefinitionDictionary.LoadAsync();
            string Meaning(string w) => WordList.Meaning(_dictionary?.Lookup(w));
            var count = anki ? WordList.Anki(file, words, Meaning) : WordList.Csv(file, words, Meaning);
            ResultText.Text = anki
                ? $"{count:N0} cards saved to {Path.GetFileName(file)}. In Anki, choose File, Import and pick that file."
                : $"{count:N0} lookups saved to {Path.GetFileName(file)}.";
        }
        catch (Exception ex)
        {
            Log.Write("words: export failed: " + ex.Message);
            ResultText.Text = "The words could not be saved: " + ex.Message;
        }
        ResultText.Visibility = Visibility.Visible;
    }
}
