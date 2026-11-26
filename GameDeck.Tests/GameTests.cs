using GameDeck.Models;

namespace GameDeck.Tests;

public class GameTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "★☆☆☆☆")]
    [InlineData(4, "★★★★☆")]
    [InlineData(5, "★★★★★")]
    public void A_rating_is_shown_as_stars(int rating, string expected)
    {
        var game = new Game { Name = "Test", Rating = rating };

        Assert.Equal(expected, game.RatingText);
        Assert.Equal(rating > 0, game.HasRating);
    }

    [Fact]
    public void Notes_are_only_there_when_they_have_text()
    {
        var game = new Game { Name = "Test" };
        Assert.False(game.HasNotes);

        game.Notes = "   ";
        Assert.False(game.HasNotes);

        game.Notes = "Finish the second act";
        Assert.True(game.HasNotes);
    }

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
