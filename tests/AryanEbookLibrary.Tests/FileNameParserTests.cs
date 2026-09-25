using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// File names as download sites, Calibre, Library Genesis, Anna's Archive and Z-Library write them. FileNameParser.Context
/// is shared, so every test here sets it; xUnit runs the tests of one class one after another.
/// </summary>
public class FileNameParserTests
{
    public FileNameParserTests() => FileNameParser.Context = NameContext.Empty;

    [Fact]
    public void Title_then_author()
    {
        var md = FileNameParser.Parse(@"E:\Books\Sapiens - Yuval Noah Harari.pdf");
        Assert.Equal("Sapiens", md.Title);
        Assert.Equal("Yuval Noah Harari", md.Author);
    }

    [Fact]
    public void Author_then_title_with_and_in_it()
    {
        // Was author "Prejudice Pride", title "Jane Austen": the "and" made the title look like "Surname, Given".
        FileNameParser.Context = NameContext.Build(["Jane Goodall"], []);
        var md = FileNameParser.Parse(@"E:\Books\Jane Austen - Pride and Prejudice.pdf");
        Assert.Equal("Pride and Prejudice", md.Title);
        Assert.Equal("Jane Austen", md.Author);
    }

    [Fact]
    public void Annas_archive_names()
    {
        var md = FileNameParser.Parse(
            @"E:\Books\Becoming the Hacker -- Adrian Pruteanu -- 2019 -- Packt Publishing -- 9781788627962 -- 2daef529d976ba12232b439d905d9b45 -- Anna’s Archive.pdf");
        Assert.Equal("Becoming the Hacker", md.Title);
        Assert.Equal("Adrian Pruteanu", md.Author);
        Assert.Equal(2019, md.Year);
        Assert.Equal("Packt Publishing", md.Publisher);
    }

    [Fact]
    public void Z_library_names()
    {
        var md = FileNameParser.Parse(@"E:\Books\The Art of War (Sun Tzu) (Z-Library).epub");
        Assert.Equal("The Art of War", md.Title);
        Assert.Equal("Sun Tzu", md.Author);
    }

    [Theory]
    [InlineData(@"E:\Books\[smtebooks.com] Becoming the Hacker 1st Edition.pdf", "Becoming the Hacker 1st Edition")]
    [InlineData(@"E:\Books\Deep Work - Copy (2).epub", "Deep Work")]
    public void Download_stamps_and_copy_marks_go(string path, string title) =>
        Assert.Equal(title, FileNameParser.Parse(path).Title);

    [Fact]
    public void Amar_chitra_katha_numbers_are_the_series()
    {
        var md = FileNameParser.Parse(@"E:\Books\ack 11 Krishna.pdf");
        Assert.Equal("Krishna", md.Title);
        Assert.Equal("Amar Chitra Katha", md.Series);
        Assert.Equal(11, md.SeriesIndex);
    }

    [Fact]
    public void A_comic_story_name_is_not_its_author()
    {
        // "120 Nagraj - Hari Maut": the left side repeats across the shelf (the hero), the right side is the story.
        FileNameParser.Context = NameContext.Build([], [
            "120 Nagraj - Hari Maut", "121 Nagraj - Khooni Khel", "122 Nagraj - Zehreela Barood", "123 Nagraj - Mamber"]);
        var md = FileNameParser.Parse(@"E:\Comics\124 Nagraj - Mritudand.pdf");
        Assert.NotEqual("Mritudand", md.Author);
        Assert.NotEqual("Nagraj", md.Author);
    }

    [Theory]
    [InlineData("00095 Jasma of Odes", 95, "Jasma of Odes")]
    [InlineData("0011 Krishna", 11, "Krishna")]
    public void Zero_padded_catalogue_numbers_split_off(string title, int number, string rest)
    {
        var split = FileNameParser.SplitCatalogueNumber(title);
        Assert.NotNull(split);
        Assert.Equal(number, split.Value.Number);
        Assert.Equal(rest, split.Value.Title);
    }

    [Fact]
    public void Other_numbers_in_front_stay_in_the_title() =>
        Assert.Null(FileNameParser.SplitCatalogueNumber("1001 Magic Tricks"));
}
