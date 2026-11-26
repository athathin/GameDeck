using System.IO;
using GameDeck.Services;
using Xunit;

namespace GameDeck.Tests;

public class RunningGameTests
{
    // a folder no program runs from: nothing must be found, asked to close or brought to the front
    private static readonly string EmptyFolder = Path.Combine(Path.GetTempPath(), "gd-no-game-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_game_that_is_not_running_counts_as_closed() =>
        Assert.True(await RunningGame.CloseAsync(EmptyFolder));

    [Fact]
    public async Task Close_anyway_ends_nothing_outside_the_game_folder() =>
        Assert.True(await RunningGame.ForceCloseAsync(EmptyFolder));   // nothing runs from there: nothing to end

    [Fact]
    public void No_window_is_found_for_a_game_that_is_not_running() =>
        Assert.False(RunningGame.BringToFront(EmptyFolder));
}
