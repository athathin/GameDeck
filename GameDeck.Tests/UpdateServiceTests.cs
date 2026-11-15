using GameDeck.Services;

namespace GameDeck.Tests;

public class UpdateServiceTests
{
    private static readonly Version Current = new(1, 0, 5, 0);

    [Theory]
    [InlineData("v1.0.6", true)]
    [InlineData("1.0.6", true)]
    [InlineData("v1.1.0", true)]
    [InlineData("v2.0", true)]
    [InlineData("v1.0.5.1", true)]    // a fourth number counts
    [InlineData("v1.0.5", false)]     // the same version
    [InlineData("v1.0.4", false)]     // an older one
    [InlineData("v1.0", false)]       // 1.0 is older than 1.0.5
    [InlineData("", false)]
    [InlineData("latest", false)]     // not a version at all
    [InlineData(null, false)]
    public void A_release_tag_is_newer_only_when_its_version_is_higher(string? tag, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewer(tag, Current));

    [Theory]
    [InlineData(1, 0, 6, 0, "1.0.6")]
    [InlineData(1, 0, 6, 1, "1.0.6.1")]
    [InlineData(2, 10, 0, 0, "2.10.0")]
    public void A_version_is_written_with_three_numbers_or_four_when_needed(int major, int minor, int build, int revision, string expected) =>
        Assert.Equal(expected, UpdateService.VersionText(new Version(major, minor, build, revision)));

    [Fact]
    public void Three_and_four_part_versions_compare_as_equal()
    {
        Assert.False(UpdateService.IsNewer("v1.0.5", new Version(1, 0, 5)));
        Assert.False(UpdateService.IsNewer("v1.0.5.0", new Version(1, 0, 5)));
    }
}
