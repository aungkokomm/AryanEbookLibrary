using System.Text;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Burmese text a file carries in the wrong encoding, shown in Unicode. Two things go wrong in real books:
/// ZAWGYI, which uses the Myanmar block its own way (the asat is U+1039, every medial sits one code point
/// below Unicode's, stacked consonants have their own code points in U+1060..U+1097), and VISUAL ORDER,
/// which is proper Unicode with ေ and ြ left in front of their consonant, as a reader typed them.
/// Nothing here touches a file: only what the catalogue shows.
/// </summary>
public static class Zawgyi
{
    private const char EVowel = 'ေ';          // ေ
    private const char Virama = '္';          // ္ (Unicode: stacks the next consonant)
    private const char Asat = '်';            // ်
    private const char MedialYa = 'ျ';        // ျ
    private const char MedialRa = 'ြ';        // ြ
    private const char MedialWa = 'ွ';        // ွ
    private const char MedialHa = 'ှ';        // ှ

    /// <summary>What the text is written in.</summary>
    public enum Kind
    {
        /// <summary>Not Burmese, already right, or too mixed to touch.</summary>
        LeaveAlone,
        Zawgyi,
        /// <summary>Unicode letters, but ေ and ြ still stand in front of their consonant.</summary>
        VisualOrder
    }

    private static bool IsConsonant(char c) => c is >= 'က' and <= 'အ';
    private static bool IsMedial(char c) => c is >= MedialYa and <= MedialHa;

    public static bool Looks(string? text) => Of(text) != Kind.LeaveAlone;

    /// <summary>The same words in Unicode. Text that is already right is returned unchanged.</summary>
    public static string Fix(string? text) => Of(text) switch
    {
        Kind.Zawgyi => Reorder(Expand(Shift(text!)), raFirst: true),
        Kind.VisualOrder => Reorder(text!, raFirst: false),
        _ => text ?? ""
    };

    /// <summary>
    /// Zawgyi is certain when the text uses Zawgyi's own code points or writes the asat as U+1039 with no
    /// consonant behind it. A text that also shows Unicode's own marks is half of each, which happens in
    /// titles, and mangling the good half would be worse than leaving it.
    /// </summary>
    public static Kind Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Kind.LeaveAlone;

        var burmese = false;
        var zawgyiOnly = 0;     // code points only Zawgyi uses
        var zawgyiAsat = 0;     // ္ where Unicode writes ်
        var unicodeOnly = 0;    // marks only Unicode writes
        var visual = 0;         // ေ or ြ standing in front of its consonant

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsConsonant(c)) burmese = true;
            var prev = Previous(text, i);
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c is >= 'ၠ' and <= '႗' or 'ဳ' or 'ဴ') zawgyiOnly++;
            else if (c == MedialHa) unicodeOnly++;                 // Zawgyi writes ှ with its own code points
            else if (c == Virama && !IsConsonant(next)) zawgyiAsat++;
            else if (c == EVowel)
            {
                if (IsConsonant(prev) || IsMedial(prev)) unicodeOnly++;
                else visual++;
            }
            else if (c == MedialRa && !IsConsonant(prev) && !IsMedial(prev)) visual++;
        }

        if (!burmese) return Kind.LeaveAlone;
        if (zawgyiOnly + zawgyiAsat > 0) return unicodeOnly > 0 ? Kind.LeaveAlone : Kind.Zawgyi;
        return visual > 0 ? Kind.VisualOrder : Kind.LeaveAlone;
    }

    /// <summary>The character before <paramref name="i"/>, ignoring the invisible joiners files pick up.</summary>
    private static char Previous(string text, int i)
    {
        for (var j = i - 1; j >= 0; j--)
            if (text[j] is not ('​' or '‌' or '‍')) return text[j];
        return '\0';
    }

    /// <summary>Zawgyi's medials and its asat all sit one code point below Unicode's.</summary>
    private static string Shift(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch
            {
                '္' => Asat,        // ္ is Zawgyi's killer; Unicode's killer is ်
                '်' => MedialYa,
                'ျ' => MedialRa,
                'ြ' => MedialWa,
                'ွ' => MedialHa,
                _ => c
            });
        return sb.ToString();
    }

    /// <summary>
    /// Zawgyi's own code points, written out as the letters they stand for. The stacked consonants
    /// U+1060..U+107C follow the Burmese alphabet in order, with the kinzi at U+1064.
    /// </summary>
    private static string Expand(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            var written = c switch
            {
                'ဳ' => "ု",                      // ု for a tall letter
                'ဴ' => "ူ",                      // ူ for a tall letter
                'ၠ' => "္က",                // ္က
                'ၡ' => "္ခ",                // ္ခ
                'ၢ' => "္ဂ",
                'ၣ' => "္ဃ",
                'ၤ' => "င်္",          // kinzi
                'ၥ' => "္စ",
                'ၦ' or 'ၧ' => "္ဆ",
                'ၨ' => "္ဇ",
                'ၩ' => "္ဈ",
                'ၪ' => "ဉ",                      // ဉ
                'ၫ' => "ည",                      // ည
                'ၬ' => "္ဋ",                // ္ဋ
                'ၭ' => "္ဌ",                // ္ဌ
                'ၮ' => "ဍ္ဍ",          // ဍ္ဍ
                'ၯ' => "ဍ္ဎ",          // ဍ္ဎ
                'ၰ' => "္ဏ",                // ္ဏ
                'ၱ' or 'ၲ' => "္တ",    // ္တ
                'ၳ' or 'ၴ' => "္ထ",    // ္ထ
                'ၵ' => "္ဒ",
                'ၶ' => "္ဓ",                // ္ဓ
                'ၷ' => "္န",
                'ၸ' => "္ပ",
                'ၹ' => "္ဖ",
                'ၺ' => "္ဗ",
                'ၻ' => "္ဘ",
                'ၼ' => "္မ",
                'ၽ' => "ျ",                      // ျ
                >= 'ၾ' and <= 'ႄ' => "ြ",   // ြ, in its many widths
                'ႅ' => "္လ",                // ္လ
                'ႆ' => "ဿ",                      // ဿ
                'ႇ' => "ှ",                      // ှ
                'ႈ' => "ှု",                // ှု
                'ႉ' => "ှူ",                // ှူ
                'ႊ' => "ွှ",                // ွှ
                'ႋ' => "င်္ိ",    // kinzi + ိ
                'ႌ' => "င်္ီ",
                'ႍ' => "င်္ံ",
                'ႎ' => "ိံ",                // ိံ
                'ႏ' => "န",                      // န
                '႐' => "ရ",                      // ရ
                '႑' => "ဏ္ဍ",          // ဏ္ဍ
                '႒' => "ဌ",                      // ဌ
                '႓' => "္ဘ",                // ္ဘ
                '႔' or '႕' => "့",          // ့
                '႖' => "္တွ",          // ္တွ
                '႗' => "္ဏ",                // ္ဏ
                _ => null
            };
            if (written is null) sb.Append(c);
            else sb.Append(written);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Puts each syllable in Unicode's order: the consonant, what is stacked under it, the medials ya, ra,
    /// wa, ha, then ေ and the rest. <paramref name="raFirst"/> says ြ always stands in front of its
    /// consonant, which is true of Zawgyi and not of Unicode that is merely out of order.
    /// </summary>
    private static string Reorder(string s, bool raFirst)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            var eVowel = false;
            var before = new List<char>();               // ြ (and ေ) written in front of the consonant

            while (i < s.Length && (s[i] == EVowel || s[i] == MedialRa))
            {
                if (s[i] == EVowel) eVowel = true;
                else before.Add(s[i]);
                i++;
            }

            if (i >= s.Length || !IsConsonant(s[i]))
            {
                // Nothing to attach them to: leave the text as it stands.
                sb.Append(s, start, Math.Max(1, i - start));
                if (i == start) i++;
                continue;
            }

            sb.Append(s[i++]);                            // the consonant
            if (i + 1 < s.Length && s[i] == Virama && IsConsonant(s[i + 1]))
            {
                sb.Append(s[i++]);                        // ္
                sb.Append(s[i++]);                        // the stacked consonant
            }

            var medials = new List<char>(before);
            while (i < s.Length && IsMedial(s[i]) &&
                   !(raFirst && s[i] == MedialRa && i + 1 < s.Length && IsConsonant(s[i + 1])))
                medials.Add(s[i++]);
            medials.Sort();                               // ya, ra, wa, ha, as Unicode orders them
            foreach (var m in medials.Distinct()) sb.Append(m);

            if (eVowel) sb.Append(EVowel);
            while (i < s.Length && s[i] == EVowel) i++;    // a second ေ is padding
        }
        return sb.ToString();
    }
}
