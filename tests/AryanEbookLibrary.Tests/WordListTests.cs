using AryanEbookLibrary.Services;
using Microsoft.Data.Sqlite;

namespace AryanEbookLibrary.Tests;

/// <summary>Words looked up: the sentence around a word, one count for the same lookup again, and the Anki and CSV files.</summary>
public sealed class WordListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aryan-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public WordListTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    [Fact]
    public void The_sentence_runs_from_the_last_stop_to_the_next()
    {
        Assert.Equal("The ship sailed on through the night.",
            WordList.Sentence("It was late. The ship ", "sailed", " on through\nthe   night. Dawn came."));
        Assert.Equal("Was it gone?", WordList.Sentence("No! Was it ", "gone", "? Yes."));
    }

    [Fact]
    public void Burmese_and_Hindi_full_stops_end_a_sentence()
    {
        Assert.Equal("ကား သွား။", WordList.Sentence("ပို။ ", "ကား", " သွား။ နောက်"));
        Assert.Equal("वह गया।", WordList.Sentence("हाँ। ", "वह", " गया। फिर"));
    }

    [Fact]
    public void A_very_long_sentence_is_cut_around_the_word()
    {
        var sentence = WordList.Sentence(new string('a', 500) + " ", "word", " " + new string('b', 500));
        Assert.True(sentence.Length <= 310);
        Assert.Contains(" word ", sentence);
        Assert.StartsWith("…", sentence);
        Assert.EndsWith("…", sentence);
    }

    [Fact]
    public void The_same_word_in_the_same_sentence_counts_once_more()
    {
        using var db = new Database(Path.Combine(_dir, "library.db"));
        var repo = new LibraryRepository(db);
        WordLookup Look(string word, string sentence, DateTime when) =>
            new() { Word = word, Sentence = sentence, BookKey = "E1|a.epub", BookTitle = "A Book", LookedUtc = when };

        var day = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        repo.AddLookup(Look("ephemeral", "An ephemeral thing.", day));
        repo.AddLookup(Look("Ephemeral", "An ephemeral thing.", day.AddHours(1)));
        repo.AddLookup(Look("ephemeral", "Another ephemeral one.", day.AddHours(2)));

        var all = repo.GetLookups();
        Assert.Equal(2, all.Count);
        Assert.Equal("Another ephemeral one.", all[0].Sentence);
        Assert.Equal(2, all[1].Times);
        Assert.Equal(day.AddHours(1), all[1].LookedUtc);
        Assert.Single(WordList.Grouped(all));

        Assert.False(repo.MergeLookup(Look("ephemeral", "An ephemeral thing.", day)));   // a backup's copy of it
        repo.DeleteLookups("EPHEMERAL");
        Assert.Empty(repo.GetLookups());
    }

    [Fact]
    public void Anki_gets_one_card_a_word_with_the_word_in_bold()
    {
        var words = new List<WordLookup>
        {
            new() { Word = "candid", Sentence = "She was candid & kind.", BookTitle = "Book <One>", LookedUtc = DateTime.UtcNow },
            new() { Word = "Candid", Sentence = "A candid photo.", BookTitle = "Book Two", LookedUtc = DateTime.UtcNow.AddDays(-1) },
        };
        var file = Path.Combine(_dir, "anki.txt");
        Assert.Equal(1, WordList.Anki(file, words, _ => "adj: openly straightforward"));
        var lines = File.ReadAllLines(file);
        Assert.Equal("#separator:tab", lines[0]);
        Assert.Equal("#html:true", lines[1]);
        var card = lines[2].Split('\t');
        Assert.Equal("candid", card[0]);
        Assert.Contains("adj: openly straightforward", card[1]);
        Assert.Contains("She was <b>candid</b> &amp; kind.", card[1]);
        Assert.Contains("A <b>candid</b> photo.", card[1]);
        Assert.Contains("Book &lt;One&gt;", card[1]);
    }

    [Fact]
    public void The_csv_quotes_what_needs_quoting()
    {
        var file = Path.Combine(_dir, "words.csv");
        WordList.Csv(file, [new() { Word = "quip", Sentence = "A quip, \"sharp\".", BookTitle = "B", LookedUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc) }],
            _ => "noun: a witty remark");
        var lines = File.ReadAllLines(file);
        Assert.Equal("Word,Meaning,Sentence,Book,Where,Looked up,Times", lines[0].TrimStart('﻿'));
        Assert.Equal("quip,noun: a witty remark,\"A quip, \"\"sharp\"\".\",B,,2026-01-02,1", lines[1]);
    }
}
