using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Tests;

public class DuplicateFinderTests
{
    private static Game Make(string name, string source, string target) =>
        new() { Name = name, Source = source, LaunchTarget = target };

    [Fact]
    public void The_same_name_from_two_launchers_is_one_group()
    {
        var groups = DuplicateFinder.Find([
            Make("Portal 2", "Steam", "steam://rungameid/620"),
            Make("PORTAL-2", "Epic", "com.epicgames.launcher://apps/portal")
        ]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void Different_games_are_not_grouped()
    {
        var groups = DuplicateFinder.Find([
            Make("Portal", "Steam", "steam://rungameid/400"),
            Make("Portal 2", "Steam", "steam://rungameid/620")
        ]);

        Assert.Empty(groups);
    }

    [Fact]
    public void A_hidden_game_is_ignored()
    {
        var hidden = Make("Portal 2", "Epic", "epic://portal");
        hidden.IsHidden = true;

        Assert.Empty(DuplicateFinder.Find([Make("Portal 2", "Steam", "steam://rungameid/620"), hidden]));
    }
}
