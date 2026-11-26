using GameDeck.Models;
using GameDeck.Services;
using Xunit;

namespace GameDeck.Tests;

public class MediaTests
{
    private static readonly Game Portal = new() { Name = "Portal", LaunchTarget = "steam://rungameid/400" };
    private static readonly Game Portal2 = new() { Name = "Portal 2", LaunchTarget = "steam://rungameid/620" };
    private static readonly Game Witcher = new() { Name = "The Witcher 3: Wild Hunt", LaunchTarget = "w3" };
    private static readonly Game Renamed = new() { Name = "FINAL FANTASY VII REMAKE", CustomName = "FF7 Remake", LaunchTarget = "ff7" };

    private static readonly List<(string Name, Game Game)> Names = ScreenshotService.NamesOf([Portal, Portal2, Witcher, Renamed]);

    [Theory]
    [InlineData("Portal 2 2026-11-26 14-03-11.png", "Portal 2")]           // the longer name wins over "Portal"
    [InlineData("Portal 2026-11-26 14-03-11.png", "Portal")]
    [InlineData("portal 2 Screenshot 2026.10.01.png", "Portal 2")]         // any case
    [InlineData("The Witcher 3_ Wild Hunt 2026-11-26.png", "The Witcher 3: Wild Hunt")]   // ':' saved as '_' in a file name
    [InlineData("FF7 Remake 2026-11-26.png", "FINAL FANTASY VII REMAKE")]  // the name the user gave it
    public void A_capture_belongs_to_the_game_its_name_starts_with(string file, string game) =>
        Assert.Equal(game, ScreenshotService.MatchGame(file, Names)?.Name);

    [Theory]
    [InlineData("Portal 2 2026-11-26 14-03-11.png", false)]   // Portal 2's capture is not on Portal's page
    [InlineData("Portal 2026-11-26 14-03-11.png", true)]
    [InlineData("Portal - level 3.png", true)]
    public void A_game_page_shows_only_its_own_captures(string file, bool onPortalsPage) =>
        Assert.Equal(onPortalsPage, ScreenshotService.BelongsTo(file, ["Portal"], Names));

    [Fact]
    public void Two_copies_of_one_game_both_show_its_captures()
    {
        var steam = new Game { Name = "Hades", LaunchTarget = "steam://rungameid/1145360" };
        var epic = new Game { Name = "Hades", LaunchTarget = "com.epicgames.launcher://apps/hades" };
        var names = ScreenshotService.NamesOf([steam, epic]);

        Assert.True(ScreenshotService.BelongsTo("Hades 2026-11-26.png", ["Hades"], names));   // (for either copy's page)
    }

    [Theory]
    [InlineData("Desktop 2026-11-26 10-00-00.png")]
    [InlineData("Screenshot 2026-11-26 101500.png")]
    public void A_capture_of_something_else_belongs_to_no_game(string file) =>
        Assert.Null(ScreenshotService.MatchGame(file, Names));
}
