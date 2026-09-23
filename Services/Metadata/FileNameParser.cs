using System.Globalization;
using System.Text.RegularExpressions;
using AryanEbookLibrary.Models;

namespace AryanEbookLibrary.Services.Metadata;

/// <summary>
/// Title, author, series, year and publisher from a book's file name, for books whose own metadata has
/// none. The rules come from a real 2,114-book library and from how other catalogues name books:
///  - "A - B" is Title - Author (Calibre's default pattern, and the user's preference) when B looks like a
///    person; Author - Title only when A is clearly a person and B is not;
///  - Library Genesis "Author - Title-Publisher (Year)" and "Author_Names_Title_Publisher_Year", Anna's
///    Archive "Title -- Author -- Year -- Publisher", Z-Library "Title (Author) (Z-Library)" and
///    "Title_Author_Z_Library" are recognised;
///  - download-site tags, archive.org ids, timestamps and copy numbers go; "_ " (a colon Windows would not
///    allow in a file name) becomes ": ";
///  - zero-padded catalogue numbers and "#013" issue numbers become the series index;
///  - Myanmar "Author၊ Title" / "Title၊Author" (the space after ၊ tells the order) and "A - B" names, where the
///    author is the side the library already knows, the side with a name prefix (ဦး, ဒေါ်, မောင် ...), or the
///    only name-length side.
/// What the whole library knows (declared authors, their given names, names that recur across file names)
/// comes from <see cref="Context"/>. When nothing says which part is the author, the name stays whole: a
/// wrong author is worse than none, and "Find details online" is there for the rest.
/// </summary>
public static class FileNameParser
{
    /// <summary>What the library knows about names. Set by the scanner before it reads files.</summary>
    public static NameContext Context { get; set; } = NameContext.Empty;

    private const string AmarChitraKatha = "Amar Chitra Katha";

    private static readonly Regex[] Noise =
    {
        new(@"\((z-?lib(rary|\.org)?|anna'?s archive|libgen[^)]*)\)", RegexOptions.IgnoreCase),
        new(@"\s-\s*pdf\s*room$", RegexOptions.IgnoreCase),
        new(@"^pdfcoffee(\.|\s)com\s*", RegexOptions.IgnoreCase),
        new(@"[\s-]*NoRestriction$", RegexOptions.IgnoreCase),   // Myanmar ebook site tag
        new(@"\s\(\d\)$"),                                     // "name (1)" copies
        new(@"^copy of\s+|\s-\s*copy(\s*\(\d+\))?$", RegexOptions.IgnoreCase),   // Windows copies: "Copy of X", "X - Copy (2)"
        new(@"[\s_-]+\d{12,14}$"),                             // "-20260212144009" download timestamps
        new(@"\s(decrypted|unlocked|ocr(ed)?|compressed)$", RegexOptions.IgnoreCase),
        new(@"\(\s*(etc\.?|et al\.?)?\s*\)|\[\s*\]"),         // brackets emptied by the removals above, "( etc.)"
        new(@"\s*[(\[][\s\d-]*$")                              // a dangling "(" or "( -1" left at the end
    };

    /// <summary>Download sites that stamp their name into file names, with or without the dot before "com".</summary>
    private static readonly Regex SiteTag = new(
        @"(^|[\s_(\[-])-?(www[\s.])?(oceanofpdf|pdfdrive|44books|dokumen|epdf|ebook3000|allitebooks|it-?ebooks|freebookspot|bookzz|b-ok|1lib|z[\s_-]?library|z-?lib|libgen|pdfroom|pdfcoffee|pdfbooksworld|sanet|avaxhome)([\s._](com|org|net|pub|in|info|tips|cc|st|ws|li|rs|is|gs))?(?=$|[\s_)\]-])[\s_)\]-]*",
        RegexOptions.IgnoreCase);

    /// <summary>Any web address in brackets is a site's stamp, whatever the site: "[smtebooks.com]", "(By azamworld.blogspot.com)".</summary>
    private static readonly Regex BracketedSite = new(
        @"\s*[\[(]\s*(by\s+)?(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|net|org|in|info|co|io|me|pk|ru|to|cc|biz|xyz)\s*[\])]",
        RegexOptions.IgnoreCase);

    private static readonly Regex ZLibraryTag = new(@"z[\s_-]?lib(rary|\.org)?\b", RegexOptions.IgnoreCase);
    private static readonly Regex ArchiveOrgId = new(@"^\d{4}\.\d{4,}\.");
    private static readonly Regex CatalogueNumber = new(@"^(0\d{1,5})\s+");
    private static readonly Regex IssueNumber = new(@"^#(\d{1,4})\s*(\(#\d+\))?\s*");
    private static readonly Regex AckPrefix = new(@"^(ack|amar chitra katha)\s*(-\s*)?", RegexOptions.IgnoreCase);
    private static readonly Regex AckSuffix = new(
        @"\(\s*amar chitra katha(\s+comics)?\s*\)|\s*(-\s*)?amar chitra katha\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex AckNumber = new(@"^(\d{1,5})(?!\d)\s*(-\s*)?");
    private static readonly Regex TrailingParenthesis = new(@"^(?<title>.+?)\s*\((?<author>[^()]+)\)$");
    private static readonly Regex ByAuthor = new(@"^(?<title>.+?)\s+by\s+(?<author>[A-Z][\w.'-]*(\s+[A-Z][\w.'-]*){0,3})$");
    private static readonly Regex AnnasArchive = new(@"\s+--\s+");
    private static readonly Regex Brackets = new(@"\s*\[[^\]]*\]");
    private static readonly Regex MyanmarDigit = new(@"[၀-၉႐-႙]");
    private static readonly Regex YearSuffix = new(@"\s*\((?<year>1[5-9]\d\d|20\d\d)\)$");
    private static readonly Regex MonthYearSuffix = new(@"\s*\(\d{2}\.(?<year>1[5-9]\d\d|20\d\d)\)$");
    private static readonly Regex LeadingSeries = new(@"^\((?<series>[^()]{3,60})\)\s*(?=\S.*\s[-–—]\s)");
    private static readonly Regex SpacedDash = new(@"\s+[-–—]\s+");
    private const string MonthNames = @"\b(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sep(t(ember)?)?|oct(ober)?|nov(ember)?|dec(ember)?)\b";
    private static readonly Regex IssueDate = new(
        @"\b(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sep(t(ember)?)?|oct(ober)?|nov(ember)?|dec(ember)?)\b\.?\s+(\d{1,2},?\s+)?(19|20)\d\d",
        RegexOptions.IgnoreCase);
    private static readonly Regex AuthorSuffixes = new(@"(,?\s+(M\s?D|Ph\s?D|HIN|Hin|ENG|Eng))+$");

    /// <summary>Publishers that libgen-style names end with ("…_Taylor_&amp;_Francis_2017").</summary>
    private static readonly string[] Publishers =
    {
        "Oxford University Press", "Cambridge University Press", "Princeton University Press", "Harvard University Press",
        "Yale University Press", "Columbia University Press", "University of Chicago Press", "MIT Press", "The MIT Press",
        "Princeton University", "Princeton Univ Pr", "Princeton", "State University of New York Press",
        "State University of New", "Routledge", "Springer Nature", "Springer", "Wiley", "Wiley-Blackwell", "Elsevier",
        "Palgrave Macmillan", "Macmillan", "Penguin Random House", "Penguin", "HarperCollins", "Harper Collins",
        "Harper Perennial India", "Harper Perennial", "Bloomsbury", "Manning Publications", "Manning", "Packt Publishing",
        "Packt", "Apress", "O'Reilly Media", "O'Reilly", "No Starch Press", "CRC Press", "Taylor & Francis",
        "Taylor and Francis", "De Gruyter", "BPB Publications", "Notion Press", "BenBella Books", "Zed Books",
        "Allen & Unwin", "Himalayan Yoga Publications", "World Scientific Publishing", "World Scientific",
        "Elliott and Thompson", "Gerald Duckworth & Co Ltd", "Future Publishing Limited", "Hay House", "Scholastic Inc",
        "Scholastic", "Sage", "Dover", "Pearson", "McGraw Hill", "McGraw-Hill", "Addison Wesley", "Prentice Hall",
        "Vintage", "Random House", "Simon & Schuster", "Hachette", "Picador", "Motilal Banarsidass", "Advaita Ashrama",
        "Jaico", "Westland", "Rupa", "Speaking Tiger", "Aleph", "Rheinwerk Computing", "Rheinwerk"
    };

    private static readonly Regex TrailingPublisher = new(
        @"[\s,]+(?:(?<y1>(?:1[5-9]|20)\d\d)[\s,]+)?(?<pub>" + string.Join("|", Publishers.Select(Regex.Escape).Select(p => p.Replace(@"\ ", @"\s"))) +
        @")(?:[\s,]+(?<y2>(?:1[5-9]|20)\d\d))?$", RegexOptions.IgnoreCase);

    private static readonly Regex TrailingYear = new(@"[\s,]+(?<year>(?:1[5-9]|20)\d\d)$");

    /// <summary>Words that show a " - X" part is a publisher.</summary>
    private static readonly Regex PublisherWords = new(
        @"\b(press|publishing|publishers?|publications|routledge|springer|wiley|blackwell|macmillan|palgrave|elsevier|penguin|harper(collins)?|ashrama|university|limited|ltd|inc|o'?reilly|apress|packt|manning|crc|francis|pearson|mcgraw|hachette|bloomsbury|scholastic|sage)\b",
        RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "to", "for", "with", "at", "by", "from"
    };

    public static BookMetadata Parse(string path)
    {
        var md = new BookMetadata();
        var ctx = Context;
        // A Burmese file name written in Zawgyi is read in Unicode; the file itself is untouched.
        var name = Zawgyi.Fix(Path.GetFileNameWithoutExtension(path));
        var hadUnderscores = name.Contains('_');

        name = Regex.Replace(name, @"(?<=[A-Za-z])_s(?=[_ ]|$)", "'s");   // "valmiki_s_ramayana", "The Queen_s Necklace"
        name = Regex.Replace(name, @"(?<=\S)_ ", ": ");              // "Neuroscience_ Exploring the Brain"
        name = Regex.Replace(name, @"\)-\d$", ")");                   // "(Z-Library)-1" copies
        name = Squash(name.Replace('_', ' '));
        var zLibrary = ZLibraryTag.IsMatch(name);
        name = Squash(BracketedSite.Replace(SiteTag.Replace(name, " "), " "));
        foreach (var rx in Noise) name = Squash(rx.Replace(name, " "));
        name = ArchiveOrgId.Replace(name, "");
        md.Isbn = Isbn.Find(name);   // "isbn_0671818325", "9780596008949"

        if (CatalogueNumber.Match(name) is { Success: true } number)
        {
            md.SeriesIndex = int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture);
            name = name[number.Length..];
        }
        else if (IssueNumber.Match(name) is { Success: true } issue)
        {
            md.SeriesIndex = int.Parse(issue.Groups[1].Value, CultureInfo.InvariantCulture);
            name = name[issue.Length..];
        }

        var isAck = AckPrefix.IsMatch(name) || AckSuffix.IsMatch(name);
        if (isAck)
        {
            // Amar Chitra Katha titles are people's names ("ack 121 - Veer Dhaval"): never read an author here.
            md.Series = AmarChitraKatha;
            name = Squash(AckSuffix.Replace(AckPrefix.Replace(name, ""), ""));
            if (AckNumber.Match(name) is { Success: true } ackNo && ackNo.Length < name.Length)
            {
                md.SeriesIndex ??= int.Parse(ackNo.Groups[1].Value, CultureInfo.InvariantCulture);
                name = name[ackNo.Length..];
            }
        }

        // Library Genesis shape: "(Series) Author - Title-Publisher (Year)". Each part is optional.
        var libgenShape = false;
        if (LeadingSeries.Match(name) is { Success: true } series)
        {
            md.Series ??= series.Groups["series"].Value.Trim();
            name = name[series.Length..];
            libgenShape = true;
        }
        var year = YearSuffix.Match(name);
        if (!year.Success) year = MonthYearSuffix.Match(name);   // "(03.2010)"
        if (year.Success)
        {
            md.Year = int.Parse(year.Groups["year"].Value, CultureInfo.InvariantCulture);
            name = name[..year.Index].TrimEnd();
            libgenShape = true;
        }
        var dashPublisher = Regex.Match(name, @"^(?<rest>.*\S)-(?<pub>[^-]{3,60})$");
        if (dashPublisher.Success && PublisherWords.IsMatch(dashPublisher.Groups["pub"].Value)
            && dashPublisher.Groups["pub"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5)
        {
            md.Publisher = dashPublisher.Groups["pub"].Value.Trim();
            name = dashPublisher.Groups["rest"].Value.Trim();
            libgenShape = true;
        }
        if (hadUnderscores)
        {
            // "…_Taylor_&_Francis_2017", "…_2024,_BenBella_Books", "…_feeling_2021"
            if (TrailingPublisher.Match(name) is { Success: true } pub && pub.Index > 0)
            {
                md.Publisher ??= pub.Groups["pub"].Value.Trim(':', ' ');
                var y = pub.Groups["y1"].Success ? pub.Groups["y1"].Value : pub.Groups["y2"].Success ? pub.Groups["y2"].Value : null;
                if (y is not null) md.Year ??= int.Parse(y, CultureInfo.InvariantCulture);
                name = name[..pub.Index].Trim(' ', ',');
            }
            // "…_feeling_2021", but not a date ("29_June_2026", "18_06_2022") or the end of a range ("1852_1940")
            if (TrailingYear.Match(name) is { Success: true } ty && name[..ty.Index].Split(' ').Length >= 3
                && !Regex.IsMatch(name[..ty.Index], @"(\b\d{1,2}|\b(1[5-9]|20)\d\d|" + MonthNames + @")\W*$", RegexOptions.IgnoreCase))
            {
                md.Year ??= int.Parse(ty.Groups["year"].Value, CultureInfo.InvariantCulture);
                name = name[..ty.Index].Trim(' ', ',');
            }
        }

        if (!isAck) ReadAuthor(ref name, md, ctx, zLibrary, hadUnderscores, libgenShape);
        else if (ByAuthor.Match(name) is { Success: true } ackBy)   // an explicit "by Anant Pai" is still the author
        {
            md.Author = ackBy.Groups["author"].Value;
            name = ackBy.Groups["title"].Value;
        }

        if (!HasMyanmar(name) && !name.Contains(' ') && name.Contains('-')) name = name.Replace('-', ' ');
        name = Squash(string.Join(' ', name.Split(' ').Select(SplitCamelCase))).Trim(' ', '-', ',', '.', ':');
        if (IsSingleCaseLatin(name)) name = TitleCase(name);

        md.Title = name.Length > 0 ? name : Zawgyi.Fix(Path.GetFileNameWithoutExtension(path));
        if (md.Author is not null)
        {
            md.Author = AuthorSuffixes.Replace(md.Author, "");
            md.Author = ctx.Canonical(PeopleParser.Tidy(md.Author));   // "Rotman, Andy" → "Andy Rotman", "JO BOALER" → "Jo Boaler"
            if (md.Author.Length == 0) md.Author = null;
        }
        return md;
    }

    /// <summary>Finds the author in one of the known name shapes and removes it from <paramref name="name"/>.</summary>
    private static void ReadAuthor(ref string name, BookMetadata md, NameContext ctx, bool zLibrary, bool hadUnderscores, bool libgenShape)
    {
        (string Author, string Title)? found = null;

        if (zLibrary && TrailingParenthesis.Match(name) is { Success: true } zl)
        {
            found = (Brackets.Replace(zl.Groups["author"].Value, "").Trim(), zl.Groups["title"].Value);   // "Title (Author) (Z-Library)"
        }
        else if (AnnasArchive.IsMatch(name))
        {
            // "Title -- Author -- Year -- Publisher -- ..."
            var parts = AnnasArchive.Split(name);
            found = (Brackets.Replace(parts[1], "").Trim(), parts[0]);
            if (parts.Length > 2 && Regex.IsMatch(parts[2].Trim(), @"^(1[5-9]|20)\d\d$"))
            {
                md.Year ??= int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                if (parts.Length > 3 && parts[3].Any(char.IsLetter)) md.Publisher ??= parts[3].Trim(' ', ':', '.');
            }
        }
        else if (ByAuthor.Match(name) is { Success: true } by && Score(by.Groups["author"].Value, ctx) >= 1)
        {
            // "Title by A Person", but not "World War II Map by Map" or "Step by Step"
            found = (by.Groups["author"].Value, by.Groups["title"].Value);
        }
        else if (SplitMyanmarComma(name) is { } mm)
        {
            found = mm;
        }
        else if (SplitDash(name, libgenShape, ctx, md) is { } dash)
        {
            found = dash;
        }
        else if (TrailingParenthesis.Match(name) is { Success: true } paren
                 && Brackets.Replace(paren.Groups["author"].Value, "").Trim() is var inParens
                 && (HasMyanmar(inParens) ? KnownMyanmarAuthor(inParens, ctx) : Score(inParens, ctx) >= 2))
        {
            found = (inParens, paren.Groups["title"].Value);   // "1,001 Kitchen Tips and Tricks (Quigg, Mary Rose)"
        }
        else if (zLibrary && TrailingAuthor(name, ctx) is { } tail)
        {
            found = tail;   // "Hungry_Ghosts_The_Karma_of_Meanness_Rotman,_Andy_Z_Library"
        }
        else if (hadUnderscores && SplitLeadingAuthors(name, ctx) is { } lead
                 && !(lead.Author.Contains(',') && !lead.Title.Trim().Contains(' ')))
        {
            // "Chris_MacDonald_and_Lewis_Vaughn_The_Power_of_Critical_Thinking". A name that is only a list of
            // people ("Sendker,_Jan_Philipp_Karnath,_Lorie_…_Liesener") has no title in it and is left alone.
            found = lead;
        }
        if (found is { } f && IsUsableTitle(f.Title, ctx) && !NotAnAuthor.IsMatch(f.Author.Trim()))
        {
            md.Author = f.Author.Trim(' ', ',', '-');
            name = f.Title.Trim(' ', ',', '-', ':');
        }
    }

    private static readonly Regex NotAnAuthor = new(@"^(etc\.?|et al\.?|others|various|anonymous|unknown|n/?a)$", RegexOptions.IgnoreCase);

    /// <summary>A title left after taking the author out must still read like a title.</summary>
    private static bool IsUsableTitle(string title, NameContext ctx)
    {
        title = title.Trim(' ', ',', '-', ':');
        // Myanmar vowel signs are not letters to .NET, so "ကြာ" (lotus) is counted by code point.
        if (title.Count(c => char.IsLetterOrDigit(c) || c is >= 'က' and <= '႟') < 2) return false;
        var first = title.Split(' ')[0].Trim(',').ToLowerInvariant();
        if (first is "editor" or "editors" or "ed" or "eds" or "and" or "&" or "with" or "by") return false;
        return !(HasMyanmar(title) ? false : Score(title, ctx) >= 2);
    }

    // ------------------------------------------------------------ "A - B" names

    /// <summary>
    /// "A - B": Title - Author when B looks like a person (the preferred order); Author - Title only when B
    /// does not and A clearly is a person (known, a given name or initial, or a Library Genesis shaped name).
    /// Myanmar names follow <see cref="MyanmarAuthorSide"/>.
    /// </summary>
    private static (string Author, string Title)? SplitDash(string name, bool libgenShape, NameContext ctx, BookMetadata md)
    {
        var parts = SpacedDash.Split(name);
        if (parts.Length < 2)
        {
            // Myanmar names often join the two parts with a bare hyphen: "ထင်လင်း-အဆိပ်"
            if (!HasMyanmar(name)) return null;
            var i = name.IndexOf('-');
            if (i <= 0 || i >= name.Length - 1 || name.IndexOf('-', i + 1) >= 0) return null;
            parts = new[] { name[..i], name[(i + 1)..] };
        }
        parts = parts.Select(p => p.Trim()).ToArray();
        if (parts.Any(p => p.Length == 0)) return null;

        if (HasMyanmar(parts[0]) || HasMyanmar(parts[^1]))
        {
            if (parts.Length != 2) return null;
            return MyanmarAuthorSide(parts[0], parts[1], ctx) switch
            {
                Side.Left => (parts[0], parts[1]),
                Side.Right => (parts[1], parts[0]),
                _ => null
            };
        }

        // " - Oxford University Press" at the end is the publisher; look again at what is left.
        var last = parts[^1];
        if (PublisherWords.IsMatch(last) && last.Split(' ').Length <= 5 && !ctx.IsKnownAuthor(last))
        {
            md.Publisher ??= last;
            parts = parts[..^1];
            if (parts.Length < 2) return null;
        }

        // A magazine issue ("PC Gamer - December 2025 UK") has no author in its name.
        if (parts.Any(p => IssueDate.IsMatch(p))) return null;

        var first = parts[0];
        var right = parts[^1];
        var rightScore = Score(right, ctx);
        if (rightScore >= 1) return (right, string.Join(" - ", parts[..^1]));

        var leftScore = Score(first, ctx);
        if (leftScore >= 2 || libgenShape && leftScore >= 1) return (first, string.Join(" - ", parts[1..]));
        return null;
    }

    /// <summary>How sure it is that <paramref name="s"/> is one or more people: 3 known, 2 a given name or
    /// initial, 1 only name-shaped, 0 not a person.</summary>
    private static int Score(string s, NameContext ctx)
    {
        s = Brackets.Replace(s, " ").Trim(' ', ',');
        if (s.Length is 0 or > 80 || s.Any(char.IsDigit)) return 0;
        if (ctx.IsKnownAuthor(s)) return 3;
        var people = PeopleParser.Parse(s);
        if (people.Count == 0) return 0;
        var worst = 3;
        foreach (var p in people)
        {
            var score = ctx.IsKnownAuthor(p) ? 3 : PersonShape(p, ctx);
            worst = Math.Min(worst, score);
        }
        return worst;
    }

    private enum Side { None, Left, Right }

    private static readonly string[] MyanmarNamePrefixes =
        { "ဦး", "ဒေါ်", "မောင်", "ကို", "ဗိုလ်", "ဒေါက်တာ", "ဆရာ", "သခင်" };

    /// <summary>Myanmar words that are never an author: volume, part, complete, combined, various authors, translation.</summary>
    private static readonly string[] MyanmarNotNames =
        { "တွဲ", "အုပ်", "ပိုင်း", "စဆုံး", "ပေါင်း", "ကလောင်စုံ", "ဘာသာပြန်", "အမှတ်", "ပြည်ထောင်စု", "ခုနှစ်" };

    /// <summary>Which side of a Myanmar "A - B" name is the author, or None when the name does not say.</summary>
    private static Side MyanmarAuthorSide(string left, string right, NameContext ctx)
    {
        var lk = KnownMyanmarAuthor(left, ctx);
        var rk = KnownMyanmarAuthor(right, ctx);
        if (lk != rk) return lk ? Side.Left : Side.Right;

        // "ဘာသာပြန် - အရိုင်းခေါ်သံ" is "Translation - Title": no author in the name at all.
        if (MyanmarNotNames.Any(w => left.Contains(w, StringComparison.Ordinal) || right.Contains(w, StringComparison.Ordinal)))
            return Side.None;

        bool Prefixed(string s) => IsMyanmarName(s) && MyanmarNamePrefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));
        var lp = Prefixed(left);
        var rp = Prefixed(right);
        if (lp != rp) return lp ? Side.Left : Side.Right;

        var ls = IsMyanmarName(left) && left.Length <= 14;
        var rs = IsMyanmarName(right) && right.Length <= 14;
        if (ls != rs) return ls ? Side.Left : Side.Right;
        return Side.None;
    }

    private static bool KnownMyanmarAuthor(string s, NameContext ctx) =>
        IsMyanmarName(s) && (ctx.IsKnownAuthor(s) || ctx.IsRepeatedSide(s));

    private static bool IsMyanmarName(string s) =>
        s.Length <= 20 && s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3
        && !MyanmarDigit.IsMatch(s) && !s.Any(char.IsAsciiDigit) && !s.Contains('(')
        && !MyanmarNotNames.Any(w => s.Contains(w, StringComparison.Ordinal))
        && (HasMyanmar(s) || s.Split(' ').All(w => w.Length > 1 && char.IsUpper(w[0])));   // "Min Lu"

    // ------------------------------------------------------------ "Author Names Title" (underscore names)

    private static readonly HashSet<string> AuthorGlue = new(StringComparer.OrdinalIgnoreCase)
    {
        ",", "and", "&", "with", "editor", "editors", "ed", "eds", "edi", "translator", "trans"
    };

    private static (string Author, string Title)? SplitLeadingAuthors(string name, NameContext ctx)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 1. Authors the library knows, one after another: "Charles S Prebish On cho Ng The Theory and ...".
        //    In a comma list, the next "Given Surname" is taken too: "Dominik Perler, Tony Crawford Feelings ...".
        var people = new List<string>();
        var pos = 0;
        var afterComma = false;
        while (pos < words.Length - 1)
        {
            var taken = 0;
            for (var n = Math.Min(6, words.Length - pos - 1); n >= 2; n--)
            {
                var candidate = string.Join(' ', words[pos..(pos + n)]).Trim(',');
                if (ctx.CanonicalPerson(candidate) is { } known)
                {
                    people.Add(known);
                    taken = n;
                    break;
                }
            }
            if (taken == 0 && people.Count > 0 && words.Length - pos >= 4)
            {
                // An unknown co-author. In a list ("…, Natalie M Scala, …", "… with Steve Patterson …") a name-shaped
                // "Given Surname" is enough; a three-word one needs a given name or to be followed by glue or a
                // lower-case word. Without a list, only a shared surname counts: "Christina Alvey Daniel Alvey".
                for (var n = 3; n >= 2 && taken == 0; n--)
                {
                    var candidate = string.Join(' ', words[pos..(pos + n)]);
                    var person = candidate.Trim(',');
                    if (words.Length - pos - n < 2) continue;
                    var inList = afterComma || candidate.EndsWith(',');
                    var next = words[pos + n];
                    var boundary = candidate.EndsWith(',') || AuthorGlue.Contains(next.Trim(',')) || char.IsLower(next[0]);
                    var ok = inList && (StrictPerson(person, ctx) || PersonShape(person, ctx) >= 1 && (n == 2 || boundary))
                             || n == 2 && StrictPerson(person, ctx) && people[^1].Split(' ')[^1] == person.Split(' ')[^1];
                    if (ok)
                    {
                        people.Add(person);
                        taken = n;
                    }
                }
            }
            if (taken == 0) break;
            afterComma = words[pos + taken - 1].EndsWith(',');
            pos += taken;
            while (pos < words.Length - 1 && (AuthorGlue.Contains(words[pos].Trim(',')) || IsDegree(words, pos)))
            {
                afterComma |= words[pos].EndsWith(',') || AuthorGlue.Contains(words[pos].Trim(','));
                pos += IsDegree(words, pos) && words[pos].Length == 1 ? 2 : 1;
            }
        }
        if (people.Count > 0) return (string.Join(", ", people), string.Join(' ', words[pos..]));

        // 2. "Angeline Close Scheinbaum editor The Darker Side ...", "Peter Urquhart editor, Paul Heyer editor Communication ..."
        static bool IsEditorWord(string w) => w.Trim(',').ToLowerInvariant() is "editor" or "editors" or "ed" or "eds";
        var marker = Array.FindLastIndex(words, IsEditorWord);
        if (marker > 0 && marker < words.Length - 1)
        {
            var head = Regex.Replace(string.Join(' ', words[..marker].Select(w => IsEditorWord(w) ? "," : w)), @"\s*,[\s,]*", ", ");
            if (Score(head, ctx) >= 1) return (head, string.Join(' ', words[(marker + 1)..]));
        }

        // 3. People with given names or initials, then a title starting with an article:
        //    "Chris MacDonald and Lewis Vaughn The Power of Critical Thinking"
        if (words.Length < 4) return null;
        var article = Array.FindIndex(words, 2, w => w is "The" or "A" or "An");
        if (article > 1 && article < words.Length - 1)
        {
            var head = string.Join(' ', words[..article]);
            if (PeopleParser.Parse(head.Replace(" and ", ", ")) is { Count: > 0 } heads
                && heads.All(p => StrictPerson(p, ctx)))
                return (head, string.Join(' ', words[article..]));
        }
        return null;
    }

    private static bool IsDegree(string[] words, int i)
    {
        var w = words[i].Trim(',', '.');
        if (w is "MD" or "PhD" or "Jr" or "Sr") return true;
        return i + 1 < words.Length && (w, words[i + 1].Trim(',', '.')) is ("M", "D") or ("Ph", "D");
    }

    /// <summary>
    /// People at the very end of a Z-Library name: "…_Rotman,_Andy_Z_Library", "…_Owen_Barfield_Z_Library",
    /// "…_Drew_Farris,_Edward_Raff_etc_Z_Library". Known people first, then "Given Surname" people in a
    /// comma list, then one "Surname, Given" (a single given name, so a list is not misread as one name).
    /// </summary>
    private static (string Author, string Title)? TrailingAuthor(string name, NameContext ctx)
    {
        var words = Regex.Replace(name, @"\s+(etc|et al)\.?$", "", RegexOptions.IgnoreCase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var people = new List<string>();
        var end = words.Length;
        while (end > 2)
        {
            string? person = null;
            var taken = 0;
            for (var n = Math.Min(4, end - 2); n >= 2 && person is null; n--)
            {
                var tail = string.Join(' ', words[(end - n)..end]).Trim(',');
                if (ctx.CanonicalPerson(tail) is { } known && !people.Contains(known)) (person, taken) = (known, n);
            }
            var inList = people.Count > 0 && words[end - 1].EndsWith(',');
            for (var n = Math.Min(4, end - 2); n >= 2 && person is null && (people.Count == 0 || inList); n--)
            {
                var tail = string.Join(' ', words[(end - n)..end]).Trim(',');
                if (StrictPerson(tail, ctx) || inList && n == 2 && PersonShape(tail, ctx) >= 1) (person, taken) = (tail, n);
            }
            if (person is null && people.Count == 0)
            {
                for (var n = Math.Min(4, end - 2); n >= 2 && person is null; n--)
                {
                    var tail = string.Join(' ', words[(end - n)..end]);
                    if (Regex.IsMatch(tail, @"^[A-Z][\p{L}'’-]+,\s[A-Z][\p{L}'’-]+(\s[A-Z]\.?)*$")) (person, taken) = (PeopleParser.Tidy(tail), n);
                }
            }
            if (person is null) break;
            people.Insert(0, person);
            end -= taken;
            while (end > 2 && words[end - 1].Trim(',') is "and" or "&" or "") end--;
        }
        if (people.Count == 0) return null;
        return (string.Join(", ", people), string.Join(' ', words[..end]).Trim(' ', ','));
    }

    // ------------------------------------------------------------ people

    /// <summary>Title words that never start or make up a person's name ("History", "Guide", "Today" ...).</summary>
    private static readonly HashSet<string> NotNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "in", "on", "for", "to", "with", "at", "from", "into", "about", "your", "my", "our",
        "and", "or", "how", "what", "why", "when", "who", "is", "are", "be", "all", "every", "more", "most", "new",
        "old", "guide", "guides", "introduction", "handbook", "manual", "history", "theory", "science", "art", "book",
        "books", "volume", "vol", "part", "edition", "chapter", "series", "complete", "practical", "advanced",
        "beginner", "beginners", "essential", "essentials", "learning", "learn", "mastering", "master", "programming",
        "principles", "fundamentals", "basics", "workbook", "stories", "story", "tales", "poems", "poetry",
        "collection", "life", "world", "love", "power", "secret", "secrets", "magic", "modern", "digital", "data",
        "design", "development", "management", "analysis", "systems", "system", "applications", "application",
        "technical", "specification", "feature", "features", "brochure", "report", "magazine", "issue", "today",
        "weekly", "monthly", "daily", "dictionary", "grammar", "english", "hindi", "notes", "study", "studies", "test",
        "exam", "questions", "answers", "solutions", "tips", "tricks", "update", "free", "ebook", "pdf", "epub",
        "code", "coding", "mind", "body", "health", "money", "business", "success", "leadership", "strategy",
        "strategies", "techniques", "technology", "future", "ai", "machine", "network", "networks", "security",
        "hacking", "calculus", "mathematics", "math", "physics", "chemistry", "biology", "economics", "philosophy",
        "psychology", "yoga", "meditation", "religion", "god", "gods", "war", "peace", "empire", "india", "indian",
        "burma", "myanmar", "january", "february", "march", "april", "may", "june", "july", "august", "september",
        "october", "november", "december", "press", "university", "publishing", "publishers", "limited", "digest",
        "journal", "review", "annual", "yearbook", "summary", "overview", "reference", "course", "lessons", "lesson",
        "steps", "ways", "rules", "habits", "keys", "methods", "reading", "writing", "language", "illustrations",
        "facts", "stats", "encyclopedia", "atlas", "revised", "illustrated", "exposed", "unlimited", "photography", "gamer", "magazine"
    };

    private static readonly HashSet<string> NameParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "da", "das", "do", "dos", "van", "von", "der", "den", "bin", "binti", "ibn", "al", "el", "la", "le",
        "du", "di", "y", "del", "della", "ter", "ten", "st"
    };

    /// <summary>2 = a person with a given name the library's authors use, or an initial; 1 = only name-shaped; 0 = not a name.</summary>
    private static int PersonShape(string p, NameContext ctx)
    {
        var words = p.Replace(".", ". ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 5) return 0;
        for (var i = 0; i < words.Length; i++)
        {
            var core = words[i].Trim(',', ';').TrimEnd('.');
            if (core.Length == 0) return 0;
            if (NotNameWords.Contains(core)) return 0;
            if (NameParticles.Contains(core) && i > 0 && i < words.Length - 1) continue;
            if (!char.IsLetter(core[0]) || !char.IsUpper(core[0])) return 0;
            if (core.Length > 3 && core.All(char.IsUpper)) return 0;                                  // "KILLERPDF"
            if (core.EndsWith("'s", StringComparison.Ordinal) || core.EndsWith("’s", StringComparison.Ordinal)) return 0;
            if (core.Length >= 6 && core.EndsWith("ing", StringComparison.Ordinal)) return 0;         // "Meeting"
            if (!core.All(c => char.IsLetter(c) || c is '\'' or '’' or '-')) return 0;
        }
        var first = words[0].TrimEnd('.');
        return first.Length == 1 || ctx.IsGivenName(first) || words.Skip(1).Take(words.Length - 2).Any(w => w.TrimEnd('.').Length == 1)
            ? 2 : 1;
    }

    /// <summary>
    /// "Given [Initial|Given] Surname": starts with a given name the library's authors use (or an initial), has
    /// only initials or given names in the middle ("Raymond Andrew Noe"), and does not end in a given name
    /// (which would be "Surname Given" order, as in "Lapierre Dominique Collins Larry").
    /// </summary>
    private static bool StrictPerson(string p, NameContext ctx)
    {
        if (ctx.IsKnownPerson(p)) return true;
        var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 4 || PersonShape(p, ctx) < 1) return false;
        var first = words[0].TrimEnd('.');
        if (first.Length > 1 && !ctx.IsGivenName(first)) return false;
        if (ctx.IsGivenName(words[^1].TrimEnd('.'))) return false;
        return words.Skip(1).Take(words.Length - 2).All(w => w.TrimEnd('.').Length == 1 || ctx.IsGivenName(w));
    }

    // ------------------------------------------------------------ Myanmar ၊

    /// <summary>
    /// The Myanmar comma separates author and title, and the space after it tells the order (true for all
    /// 80 such names in a real library): "ဇော်ဂျီ၊ ရွှေမောင်းသံ" is Author၊ Title, "ကြာ၊နန္ဒာသိန်းဇံ" is
    /// Title၊Author. The name side is short (at most 17 characters there) and has no digits, which keeps
    /// volume numbers ("အတွဲ ၄၊ အမှတ် ၁") and years out.
    /// </summary>
    private static (string Author, string Title)? SplitMyanmarComma(string name)
    {
        var i = name.IndexOf('၊');   // ၊
        if (i <= 0 || i >= name.Length - 1) return null;
        var left = name[..i].Trim();
        var rest = name[(i + 1)..];
        var authorFirst = rest.StartsWith(' ');
        var right = rest.Trim();
        var (author, title) = authorFirst ? (left, right) : (right, left);

        if (!HasMyanmar(author) || title.Length == 0 || author.Length > 20) return null;
        if (author.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return null;
        if (MyanmarDigit.IsMatch(author) || author.Any(char.IsAsciiDigit)) return null;
        return (author, title);
    }

    private static bool HasMyanmar(string s) => s.Any(c => c is >= 'က' and <= '႟');

    // ------------------------------------------------------------ casing

    /// <summary>All lowercase, or ALL CAPS with a real word in it ("RANI OF JHANSI", not codes like "STJ 03-26 S").</summary>
    private static bool IsSingleCaseLatin(string s)
    {
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count == 0 || !letters.All(char.IsAscii)) return false;
        if (letters.All(char.IsLower)) return true;
        return letters.All(char.IsUpper) && s.Split(' ').Any(w => w.Count(char.IsLetter) >= 4);
    }

    /// <summary>"ShrenikAndTheHiddenTruth" → "Shrenik and the Hidden Truth". Needs two or more joins, so "iPhone" or "McDonald" stay.</summary>
    private static string SplitCamelCase(string word)
    {
        var joins = CamelJoin.Matches(word).Count;
        if (joins < 2) return word;
        var parts = CamelJoin.Replace(word, " ").Split(' ');
        for (var i = 1; i < parts.Length; i++)
            if (SmallWords.Contains(parts[i])) parts[i] = parts[i].ToLowerInvariant();
        return string.Join(' ', parts);
    }

    private static readonly Regex CamelJoin = new(@"(?<=[a-z])(?=[A-Z])");

    private static string TitleCase(string s)
    {
        var words = s.ToLowerInvariant().Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (words[i].Length > 0 && (i == 0 || !SmallWords.Contains(words[i])))
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }

    public static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>A title written inside a book can carry a download site too: "Principles of Neural Science - PDFDrive.com".</summary>
    public static string StripSiteTags(string title) =>
        Squash(BracketedSite.Replace(SiteTag.Replace(title, " "), " ")).Trim(' ', '-', ':', '|');

    /// <summary>
    /// A catalogue number written in front of a title, "00095 Jasma of Odes": the number, and the title without it.
    /// Only a number with a leading zero, so "1001 Magic Tricks" keeps its number.
    /// </summary>
    public static (int Number, string Title)? SplitCatalogueNumber(string title) =>
        CatalogueNumber.Match(title) is { Success: true } m && m.Length < title.Length
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), title[m.Length..].Trim())
            : null;
}

/// <summary>
/// What the whole library says about names, so one file name can be read in the light of all the others:
/// people the books declare in their own metadata (and the user typed in), their given names, and names that
/// recur beside different titles across file names ("မင်းလူ - …" four times: an author, not a title).
/// Each known person is found in either name order and comes back in the form the books write it.
/// </summary>
public sealed class NameContext
{
    public static readonly NameContext Empty = new(new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>());

    private readonly Dictionary<string, string> _people;   // normalised name (either order) → display name
    private readonly HashSet<string> _givenNames;
    private readonly HashSet<string> _repeatedSides;

    private NameContext(Dictionary<string, string> people, HashSet<string> givenNames, HashSet<string> repeatedSides)
    {
        _people = people;
        _givenNames = givenNames;
        _repeatedSides = repeatedSides;
    }

    public int PeopleCount => _people.Count;

    public static NameContext Build(IEnumerable<string> declaredAuthors, IEnumerable<string> fileNames)
    {
        var people = new Dictionary<string, string>(StringComparer.Ordinal);
        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in declaredAuthors)
        {
            var list = PeopleParser.Parse(raw);
            foreach (var person in list)
            {
                var words = person.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // A one-word "person" counts only when it was the whole field ("Osho", "Sadhguru", "ဦးနု").
                if (words.Length < 2 && (list.Count > 1 || raw.Contains(','))) continue;
                if (person.Any(char.IsDigit)) continue;
                // "JO BOALER" and "shravasti dhammika" are shown as "Jo Boaler" and "Shravasti Dhammika"
                var display = words.All(w => w.Length > 1 && w.All(char.IsUpper)) || person.All(c => !char.IsUpper(c))
                    ? TitleCaseName(person) : person;
                people.TryAdd(Norm(person), display);
                if (words.Length >= 2)
                {
                    people.TryAdd(Norm(words[^1] + " " + string.Join(' ', words[..^1])), display);   // "Arden John B"
                    var first = words[0].TrimEnd('.');
                    if (first.Length > 1 && char.IsUpper(first[0])) given.Add(first);
                }
            }
        }

        // Names that sit beside two or more different titles: "X - Y" parts and "Title (Name)" parentheses.
        var partners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Pair(string a, string b)
        {
            a = Norm(a);
            b = Norm(b);
            if (a.Length < 2 || b.Length < 2) return;
            (partners.TryGetValue(a, out var pa) ? pa : partners[a] = new HashSet<string>()).Add(b);
        }
        foreach (var file in fileNames)
        {
            var s = FileNameParser.Squash(Path.GetFileNameWithoutExtension(file).Replace('_', ' '));
            var parts = Regex.Split(s, @"\s+[-–—]\s+|(?<=[\u1000-\u109F])-(?=[\u1000-\u109F])");
            if (parts.Length == 2)
            {
                Pair(parts[0], parts[1]);
                Pair(parts[1], parts[0]);
            }
            var paren = Regex.Match(s, @"^(?<t>.+?)\s*\((?<n>[^()]+)\)$");
            if (paren.Success) Pair(paren.Groups["n"].Value, paren.Groups["t"].Value);
        }
        var repeated = partners.Where(p => p.Value.Count >= 2).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return new NameContext(people, given, repeated);
    }

    /// <summary>Every person in <paramref name="s"/> is one the library knows.</summary>
    public bool IsKnownAuthor(string s)
    {
        if (_people.ContainsKey(Norm(s))) return true;
        var list = PeopleParser.Parse(s);
        return list.Count > 0 && list.All(IsKnownPerson);
    }

    /// <summary>One person, in either name order ("Ian Stevenson", "Stevenson Ian", "Stevenson, Ian").</summary>
    public bool IsKnownPerson(string s) => CanonicalPerson(s) is not null;

    /// <summary>The known person <paramref name="s"/> names, written the way the books write it; null if unknown.</summary>
    public string? CanonicalPerson(string s)
    {
        if (_people.TryGetValue(Norm(s), out var display)) return display;
        if (s.Contains(',') && PeopleParser.Parse(s) is { Count: 1 } one && _people.TryGetValue(Norm(one[0]), out display)) return display;
        return null;
    }

    /// <summary>Each person of an author field in its known form ("JO BOALER" → "Jo Boaler"); unknown people unchanged.</summary>
    public string Canonical(string authors) =>
        string.Join(", ", PeopleParser.Parse(authors).Select(p => CanonicalPerson(p) ?? p));

    public bool IsRepeatedSide(string s) => _repeatedSides.Contains(Norm(s));

    public bool IsGivenName(string s) => _givenNames.Contains(s);

    /// <summary>Lower case, no dots, commas or hyphens: "R.S. McGregor", "R S Mcgregor" and "On-cho Ng" / "On cho Ng" match.</summary>
    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[\s.,'’_-]+", " ").Trim();

    private static string TitleCaseName(string s) =>
        string.Join(' ', s.Split(' ').Select(w => w.Length > 1 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToUpperInvariant()));
}