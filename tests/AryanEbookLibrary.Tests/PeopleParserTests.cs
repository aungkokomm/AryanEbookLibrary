using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Tests;

/// <summary>Author fields as real books write them, and what is not a person at all.</summary>
public class PeopleParserTests
{
    [Theory]
    [InlineData("Arden, John B.;", "John B. Arden")]
    [InlineData("Barnes, Mark, Gonzalez, Jennifer", "Mark Barnes, Jennifer Gonzalez")]
    [InlineData("Baker, Chris; Phongpaichit, Pasuk", "Chris Baker, Pasuk Phongpaichit")]
    [InlineData("A. C. Bhaktivedanta Swami Prabhupada, 1896-1977", "A. C. Bhaktivedanta Swami Prabhupada")]
    [InlineData("Campbell, Josette, author", "Josette Campbell")]
    [InlineData("Harari, Yuval Noah", "Yuval Noah Harari")]
    [InlineData("by Matthew G. Naugle, PDFed by UncleVan", "Matthew G. Naugle")]
    [InlineData("KAMALA CHANDRAKANT", "Kamala Chandrakant")]
    [InlineData("Ikenna Nwaiwu<br><i>Foreword by Melissa van der Hecht</i>", "Ikenna Nwaiwu")]
    // "and" joins two people like "&" does, never a surname to a given name ("Pride and Prejudice" was "Prejudice Pride")
    [InlineData("Laurel and Hardy", "Laurel, Hardy")]
    [InlineData("Harari, Yuval Noah and Baker, Chris", "Yuval Noah Harari, Chris Baker")]
    public void Tidies_author_fields_into_display_names(string raw, string expected) =>
        Assert.Equal(expected, PeopleParser.Tidy(raw));

    [Theory]
    [InlineData("www.oshoworld.com")]
    [InlineData("https://www.pdfmagaz.in")]
    [InlineData("savarkar.org")]
    [InlineData("someone@example.com")]
    [InlineData("ComicRack")]
    [InlineData("CamScanner")]
    [InlineData("Microsoft Office User")]
    [InlineData("JPG To PDF Converter")]
    [InlineData("Adobe Acrobat Pro")]
    public void Web_addresses_emails_and_programs_are_not_authors(string raw)
    {
        Assert.True(PeopleParser.IsNotAPerson(raw));
        Assert.Equal("", PeopleParser.Tidy(raw));
    }

    [Fact]
    public void A_person_named_beside_a_program_is_kept() =>
        Assert.Equal("Jane Doe", PeopleParser.Tidy("Jane Doe; CamScanner"));

    [Theory]
    [InlineData("www.oshoworld.com", "oshoworld.com")]
    [InlineData("Osho; www.oshoworld.com", "oshoworld.com")]
    [InlineData("https://www.pdfmagaz.in", "pdfmagaz.in")]
    public void The_site_named_as_author_becomes_the_publisher(string raw, string site) =>
        Assert.Equal(site, PeopleParser.SiteIn(raw));

    [Theory]
    [InlineData("John Smith")]
    [InlineData("someone@example.com")]
    [InlineData("")]
    public void No_site_when_the_field_names_none(string raw) => Assert.Null(PeopleParser.SiteIn(raw));
}
