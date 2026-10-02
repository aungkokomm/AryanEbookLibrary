using System.Text.Json;
using AryanEbookLibrary.Services;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// The update bubble is only as good as its answer to "is that release newer than this one". A wrong yes nags the user
/// about their own version; a wrong no hides a real update.
/// </summary>
public class UpdateVersionsTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("V1.3.0", "1.3.0")]
    [InlineData("1.2.0", "1.2.0")]
    public void A_release_tag_reads_as_its_version(string tag, string version)
    {
        Assert.Equal(version, UpdateVersions.Normalize(tag));
    }

    [Theory]
    [InlineData("1.2.0", "1.1.0", true)]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.0.10", "1.0.9", true)]     // compared as numbers, not as text
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2", "1.2.0", false)]       // the same release written shorter
    [InlineData("1.1.0", "1.2.0", false)]     // never offer an older build
    public void Only_a_later_release_is_newer(string latest, string current, bool newer)
    {
        Assert.Equal(newer, UpdateVersions.IsNewer(latest, current));
    }

    [Theory]
    [InlineData("nightly", "1.2.0")]
    [InlineData("1.3.0", "")]
    [InlineData("", "1.2.0")]
    public void A_version_that_cannot_be_read_is_never_newer(string latest, string current)
    {
        Assert.False(UpdateVersions.IsNewer(latest, current));
    }

    [Fact]
    public void No_version_is_skipped_until_the_user_skips_one()
    {
        Assert.Equal("", new AppSettings().SkippedUpdateVersion);
        var read = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { SkippedUpdateVersion = "1.3.0" }))!;
        Assert.Equal("1.3.0", read.SkippedUpdateVersion);
    }
}
