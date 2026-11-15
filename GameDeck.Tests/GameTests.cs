using GameDeck.Models;

namespace GameDeck.Tests;

public class GameTests
{
    [Theory]
    [InlineData(0, "< 1 min")]
    [InlineData(59, "< 1 min")]
    [InlineData(60, "1 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h 0 min")]
    [InlineData(5400, "1 h 30 min")]
    [InlineData(45000, "12 h 30 min")]
    public void Playtime_is_written_in_hours_and_minutes(long seconds, string expected) =>
        Assert.Equal(expected, Game.FormatPlaytime(seconds));
}
