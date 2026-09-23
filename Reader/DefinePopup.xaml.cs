using AryanEbookLibrary.Reader.Define;
using AryanEbookLibrary.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Define (from Ayaan PDF): a glance at an English word, not a dictionary entry. Up to three parts of speech, two
/// senses each, one example, then the Myanmar and Hindi meanings of the same headword and part of speech. The view
/// that shows it says where the word is; the popup sits above it, below it when there is no room, inside the view.
/// </summary>
public sealed partial class DefinePopup : UserControl
{
    private int _request;
    private Rect? _box;
    private Size _area;

    public DefinePopup()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Shows <paramref name="word"/>'s definition. The first use reads the dictionaries off the UI thread, so the
    /// popup says it is looking rather than holding the click; a lookup overtaken by another word is dropped.
    /// </summary>
    public async void Show(string word)
    {
        var request = ++_request;
        WordText.Text = word;
        var english = DefinitionDictionary.LoadAsync();
        var myanmar = AppServices.Settings.DefineShowsMyanmar ? DefinitionDictionary.LoadMyanmarAsync() : Task.FromResult<MyanmarGlosses?>(null);
        var hindi = AppServices.Settings.DefineShowsHindi ? DefinitionDictionary.LoadHindiAsync() : Task.FromResult<HindiGlosses?>(null);
        if (!english.IsCompleted || !myanmar.IsCompleted || !hindi.IsCompleted)
        {
            DefinitionText.Text = "Looking up...";
            Rule.Visibility = MyanmarText.Visibility = HindiText.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Visible;
            Place();
        }

        var dictionary = await english;
        var glosses = await myanmar;
        var hindiGlosses = await hindi;
        if (request != _request) return;

        Visibility = Visibility.Visible;
        Fill(word, dictionary, glosses, hindiGlosses);
        Place();
    }

    public void Hide()
    {
        if (!IsOpen) return;
        _request++;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Where the word is, in the coordinates of the area the popup is laid over, and that area's size. A null box
    /// puts the popup in the upper middle; a box outside the area hides it until the word is back in view.
    /// </summary>
    public void PlaceNear(Rect? box, Size area)
    {
        _box = box;
        _area = area;
        Place();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Place();

    private void Place()
    {
        if (!IsOpen) return;
        var w = ActualWidth;
        var h = ActualHeight;
        var box = _box ?? new Rect(_area.Width / 2 - w / 2, _area.Height / 3, 0, 0);
        if (_box is not null && (box.Bottom < 0 || box.Top > _area.Height || box.Right < 0 || box.Left > _area.Width))
        {
            Opacity = 0;
            return;
        }

        var left = Math.Clamp(box.X, 12, Math.Max(12, _area.Width - w - 12));
        var top = box.Y - h - 10;
        if (top < 12) top = box.Bottom + 10;
        top = Math.Clamp(top, 12, Math.Max(12, _area.Height - h - 12));
        Translation = new System.Numerics.Vector3((float)left, (float)top, 32);
        Opacity = w > 0 ? 1 : 0;
    }

    private void Fill(string word, WordDefinitions? dictionary, MyanmarGlosses? glosses, HindiGlosses? hindi)
    {
        MyanmarText.Inlines.Clear();
        HindiText.Inlines.Clear();
        Rule.Visibility = MyanmarText.Visibility = HindiText.Visibility = Visibility.Collapsed;

        if (dictionary is null)
        {
            DefinitionText.Text = "The dictionary could not be loaded.";
            return;
        }

        void AddLine(TextBlock block, string label, IReadOnlyList<string> meanings)
        {
            var isMyanmar = ReferenceEquals(block, MyanmarText);
            if (block.Inlines.Count > 0) block.Inlines.Add(new LineBreak());
            block.Inlines.Add(new Run { Text = label + ": ", FontWeight = FontWeights.SemiBold });
            block.Inlines.Add(new Run
            {
                Text = string.Join(isMyanmar ? "\u104A " : ", ", meanings),
                FontFamily = new FontFamily(isMyanmar ? "Pyidaungsu, Myanmar Text" : "Nirmala UI"),
            });
        }

        void ShowTranslations(int myanmarLines, int hindiLines)
        {
            Rule.Visibility = myanmarLines + hindiLines > 0 ? Visibility.Visible : Visibility.Collapsed;
            MyanmarText.Visibility = myanmarLines > 0 ? Visibility.Visible : Visibility.Collapsed;
            HindiText.Visibility = hindiLines > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (dictionary.Lookup(word) is not { } found)
        {
            // No English entry, but the Myanmar and Hindi lists may still know the word. Grammar words stay refused.
            var grammarWord = WordDefinitions.IsGrammarWord(word);
            var none = Array.Empty<(string PartOfSpeech, IReadOnlyList<string> Meanings)>();
            var myanmarOnly = grammarWord || glosses is null ? none : glosses.ForWord(word);
            var hindiOnly = grammarWord || hindi is null ? none : hindi.ForWord(word);
            if (myanmarOnly.Count + hindiOnly.Count == 0)
            {
                DefinitionText.Text = $"No definition found for \u201C{word}\u201D.";
                return;
            }
            DefinitionText.Text = "No English definition.";
            foreach (var (pos, meanings) in myanmarOnly) AddLine(MyanmarText, pos, meanings);
            foreach (var (pos, meanings) in hindiOnly) AddLine(HindiText, pos, meanings);
            ShowTranslations(myanmarOnly.Count, hindiOnly.Count);
            return;
        }

        string LabelFor(WordSense sense) =>
            string.Equals(sense.Headword, word, StringComparison.OrdinalIgnoreCase) ? sense.PartOfSpeech : $"{sense.PartOfSpeech}, {sense.Headword}";

        // Inlines.Clear, not Text = "": empty text leaves an empty Run behind that counts as a first line.
        DefinitionText.Inlines.Clear();
        foreach (var sense in found.Senses)
        {
            if (DefinitionText.Inlines.Count > 0) DefinitionText.Inlines.Add(new LineBreak());
            var meaning = sense.Definitions.Count > 1 ? $"{sense.Definitions[0]}; {sense.Definitions[1]}" : sense.Definitions[0];
            DefinitionText.Inlines.Add(new Run { Text = LabelFor(sense) + ": ", FontWeight = FontWeights.SemiBold });
            DefinitionText.Inlines.Add(new Run { Text = meaning });
            if (ReferenceEquals(sense, found.Senses[0]) && sense.Example is { Length: > 0 } example)
            {
                DefinitionText.Inlines.Add(new LineBreak());
                DefinitionText.Inlines.Add(new Run { Text = "\u201C" + example + "\u201D", FontStyle = Windows.UI.Text.FontStyle.Italic });
            }
        }

        var myanmarLines = 0;
        foreach (var sense in found.Senses)
        {
            if (glosses?.For(sense.Headword, sense.PartOfSpeech) is not { Count: > 0 } meanings) continue;
            myanmarLines++;
            AddLine(MyanmarText, LabelFor(sense), meanings);
        }
        var hindiLines = 0;
        foreach (var sense in found.Senses)
        {
            if (hindi?.For(sense.Headword, sense.PartOfSpeech) is not { Count: > 0 } meanings) continue;
            hindiLines++;
            AddLine(HindiText, LabelFor(sense), meanings);
        }
        ShowTranslations(myanmarLines, hindiLines);
    }
}

/// <summary>The right-click menu on a word, the same in every reader: Define, Copy, Find.</summary>
internal static class WordMenu
{
    /// <summary>
    /// A menu for the <paramref name="selection"/> when there is one, else the <paramref name="word"/> under the
    /// pointer; null when there is nothing to offer (a picture, a margin).
    /// </summary>
    public static MenuFlyout? Build(string selection, string word, Action<string> define, Action<string> copy, Action<string> find)
    {
        var menu = new MenuFlyout();
        var define_ = EnglishWord.TryNormalize(selection, out var s) ? s
            : selection.Length == 0 && EnglishWord.TryNormalize(word, out var w) ? w : null;
        if (define_ is not null)
        {
            var item = new MenuFlyoutItem { Text = $"Define \u201C{define_}\u201D", Icon = new FontIcon { Glyph = "\uE82D" } };
            item.Click += (_, _) => define(define_);
            menu.Items.Add(item);
        }

        var copyText = selection.Length > 0 ? selection : word;
        if (copyText.Length > 0)
        {
            var item = new MenuFlyoutItem
            {
                Text = selection.Length > 0 ? "Copy" : $"Copy \u201C{Shorten(word)}\u201D",
                Icon = new SymbolIcon(Symbol.Copy),
            };
            if (selection.Length > 0) item.KeyboardAcceleratorTextOverride = "Ctrl+C";
            item.Click += (_, _) => copy(copyText);
            menu.Items.Add(item);
        }

        var findText = selection.Length is > 0 and <= 80 && !selection.Contains('\n') ? selection : word;
        if (!string.IsNullOrWhiteSpace(findText))
        {
            var item = new MenuFlyoutItem { Text = $"Find \u201C{Shorten(findText)}\u201D in this book", Icon = new SymbolIcon(Symbol.Find) };
            item.Click += (_, _) => find(findText.Trim());
            menu.Items.Add(item);
        }

        return menu.Items.Count == 0 ? null : menu;
    }

    private static string Shorten(string text) => text.Length <= 24 ? text : text[..22] + "...";

    public static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}
