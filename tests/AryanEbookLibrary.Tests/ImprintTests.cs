using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Tests;

/// <summary>Copyright pages as real PDFs lay them out, including the scanning and letter-spacing noise they carry.</summary>
public class ImprintTests
{
    [Fact]
    public void Reads_first_publication_and_publisher()
    {
        var facts = Imprint.Read("""
            Copyright © 2019 Packt Publishing
            All rights reserved.
            First published: January 2019
            Production reference: 1310119
            Published by Packt Publishing Ltd.
            Livery Place
            35 Livery Street
            """);
        Assert.Equal(2019, facts.Year);
        Assert.Equal("Packt Publishing", facts.Publisher);
    }

    [Fact]
    public void First_published_beats_a_later_edition()
    {
        var facts = Imprint.Read("""
            First published in Great Britain in 1997 by Bloomsbury Publishing Plc
            Copyright © J.K. Rowling 1997
            This edition published 2014
            """);
        Assert.Equal(1997, facts.Year);
        Assert.Equal("Bloomsbury Publishing", facts.Publisher);
    }

    [Fact]
    public void The_earliest_copyright_year_wins_and_a_person_is_not_a_publisher()
    {
        var facts = Imprint.Read("""
            Copyright © 2011, 2015 by John Smith
            All rights reserved.
            """);
        Assert.Equal(2011, facts.Year);
        Assert.Null(facts.Publisher);
    }

    [Fact]
    public void A_copyright_sign_drawn_as_c_still_counts()
    {
        var facts = Imprint.Read("Copyright c 2019 by Sams Publishing\nAll rights reserved.");
        Assert.Equal(2019, facts.Year);
        Assert.Equal("Sams Publishing", facts.Publisher);
    }

    [Theory]
    [InlineData("Published by Prince ton University Press", "Princeton University Press")]
    [InlineData("Published by H.G. Mirchandani for IBH Publishers Pvt Ltd", "IBH Publishers")]
    [InlineData("Published by PENGUIN BOOKS", "Penguin Books")]
    [InlineData("Published by Taylor & Francis and available online", "Taylor & Francis")]
    public void Publisher_names_come_out_clean(string line, string publisher) =>
        Assert.Equal(publisher, Imprint.Read(line + "\nAll rights reserved.").Publisher);

    [Theory]
    [InlineData("Published by Indi;a Book House")]           // a scan's misread inside a word
    [InlineData("Published by Amar Chitra K3tha")]            // a digit inside a word
    [InlineData("Publisher: William Pollock")]               // the person who runs it, not the company
    [InlineData("Published by permission of the author")]
    public void Doubtful_publishers_are_left_out(string line) =>
        Assert.Null(Imprint.Read(line + "\nAll rights reserved.").Publisher);

    [Fact]
    public void A_page_that_is_not_a_copyright_page_gives_nothing()
    {
        var facts = Imprint.Read("Chapter 1\nIt was 1984 when the house on Main Street was sold by Penguin Books.");
        Assert.Null(facts.Year);
        Assert.Null(facts.Publisher);
    }

    [Fact]
    public void An_impossible_year_is_ignored() =>
        Assert.Null(Imprint.Read("Copyright © 1066 Norman Press\nAll rights reserved.").Year);
}
