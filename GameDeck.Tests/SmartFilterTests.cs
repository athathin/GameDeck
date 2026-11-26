using GameDeck.Models;
using GameDeck.Services;
using Xunit;

namespace GameDeck.Tests;

public class SmartFilterTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, false, true, false)]      // never started
    [InlineData(0, true, false, true)]       // started from GameDeck, but its time couldn't be counted
    [InlineData(1800, true, false, true)]    // half an hour
    [InlineData(3599, true, false, true)]
    [InlineData(3600, true, false, false)]   // a full hour is not "under 1 hour"
    public void Playtime_filters(long seconds, bool hasLastPlayed, bool neverPlayed, bool underAnHour)
    {
        var game = new Game { PlaytimeSeconds = seconds, LastPlayed = hasLastPlayed ? Now.AddDays(-1) : null };

        Assert.Equal(neverPlayed, SmartFilter.NeverPlayed.Matches(game, Now));
        Assert.Equal(underAnHour, SmartFilter.UnderAnHour.Matches(game, Now));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Recently_added_means_the_last_30_days(int daysAgo, bool expected) =>
        Assert.Equal(expected, SmartFilter.RecentlyAdded.Matches(new Game { AddedUtc = Now.AddDays(-daysAgo) }, Now));

    [Fact]
    public void A_game_with_no_known_date_is_not_recently_added()
    {
        Assert.False(SmartFilter.RecentlyAdded.Matches(new Game { AddedUtc = null }, Now));
        Assert.False(SmartFilter.RecentlyAdded.Matches(new Game { AddedUtc = DateTime.MinValue }, Now));
    }

    [Fact]
    public void The_first_time_games_get_their_install_folder_date_or_unknown_not_today()
    {
        var entries = new Dictionary<string, UserDataService.Entry>(StringComparer.OrdinalIgnoreCase);
        var withFolder = new Game { LaunchTarget = "a" };
        var withoutFolder = new Game { LaunchTarget = "b" };
        var folderDate = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var changed = UserDataService.DateNewGames(entries, [withFolder, withoutFolder], Now,
            g => g == withFolder ? folderDate : null);

        Assert.True(changed);
        Assert.Equal(folderDate, entries["a"].AddedUtc);
        Assert.Equal(DateTime.MinValue, entries["b"].AddedUtc);
    }

    [Fact]
    public void After_that_a_new_game_is_dated_now_and_old_dates_stay()
    {
        var old = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = new Dictionary<string, UserDataService.Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new() { AddedUtc = old }
        };

        UserDataService.DateNewGames(entries, [new Game { LaunchTarget = "a" }, new Game { LaunchTarget = "new" }], Now, _ => old);

        Assert.Equal(old, entries["a"].AddedUtc);
        Assert.Equal(Now, entries["new"].AddedUtc);
        Assert.False(UserDataService.DateNewGames(entries, [new Game { LaunchTarget = "a" }], Now, _ => null));   // nothing new
    }

    [Fact]
    public void Nothing_is_dated_while_the_library_is_empty() =>
        Assert.False(UserDataService.DateNewGames(new(), [], Now, _ => null));
}
