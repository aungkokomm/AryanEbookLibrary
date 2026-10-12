using AryanEbookLibrary.Models;
using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Tests;

/// <summary>A cover the reader chose: shown first, replaced by a new choice, kept by Rebuild covers, and kept by a moved book.</summary>
public class ChosenCoverTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public ChosenCoverTests() => AppPaths.Init();   // AryanLibrary-Data beside the test assembly

    [Fact]
    public async Task A_chosen_cover_is_kept_replaced_moved_and_removed()
    {
        var key = "TEST|" + Guid.NewGuid().ToString("N");
        var moved = "TEST|" + Guid.NewGuid().ToString("N");

        var first = await CoverStore.SaveMineAsync(Png, ".png", key);
        Assert.Equal(first, CoverStore.MineFor(CoverStore.Mine(), key));

        await Task.Delay(5);
        var second = await CoverStore.SaveMineAsync(Png, ".png", key);
        Assert.NotEqual(first, second);   // a new name, so every view loads the new picture
        Assert.False(File.Exists(Path.Combine(AppPaths.Covers, first)));
        Assert.Equal(second, CoverStore.MineFor(CoverStore.Mine(), key));

        CoverStore.ClearAll();   // Rebuild covers
        Assert.True(File.Exists(Path.Combine(AppPaths.Covers, second)));

        CoverStore.MoveMine(key, moved);
        Assert.Null(CoverStore.MineFor(CoverStore.Mine(), key));
        var after = CoverStore.MineFor(CoverStore.Mine(), moved);
        Assert.NotNull(after);

        CoverStore.RemoveMine(moved);
        Assert.Null(CoverStore.MineFor(CoverStore.Mine(), moved));
    }

    [Fact]
    public void The_chosen_cover_comes_before_the_files_own()
    {
        var book = new Book { Title = "Hari Maut", Author = "A. Writer", RelPath = "a.pdf", CoverFile = "own.png", CoverWeak = true };
        book.KeepFileDetails();
        Assert.True(book.NeedsDetails);   // a page of text as its cover

        book.SetMyCover("my-cover.png");
        Assert.Equal("my-cover.png", book.CoverFile);
        Assert.False(book.NeedsDetails);

        book.SetMyCover(null);
        Assert.Equal("own.png", book.CoverFile);
    }
}
