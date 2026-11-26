using GameDeck.Services;

namespace GameDeck.Tests;

public class ScreenshotServiceTests
{
    [Theory]
    [InlineData("Overwatch 2", "Overwatch 2")]
    [InlineData("Half-Life: Alyx", "Half-Life_ Alyx")]
    [InlineData("What?/Why*", "What__Why_")]
    [InlineData("...", "game")]
    [InlineData("   ", "game")]
    public void A_game_name_becomes_a_safe_folder_name(string name, string expected) =>
        Assert.Equal(expected, ScreenshotService.SafeName(name));
}
