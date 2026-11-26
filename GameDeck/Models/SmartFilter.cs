namespace GameDeck.Models;

/// <summary>Filters worked out from what GameDeck already knows about each game (playtime, when it was added).</summary>
public enum SmartFilter
{
    NeverPlayed,
    UnderAnHour,
    RecentlyAdded
}

public static class SmartFilterExtensions
{
    /// <summary>How long a game counts as "recently added".</summary>
    public static readonly TimeSpan RecentlyAddedFor = TimeSpan.FromDays(30);

    /// <summary>The name shown in the filter panels.</summary>
    public static string Label(this SmartFilter filter) => filter switch
    {
        SmartFilter.NeverPlayed => "Never played",
        SmartFilter.UnderAnHour => "Played under 1 hour",
        SmartFilter.RecentlyAdded => "Recently added",
        _ => filter.ToString()
    };

    public static bool Matches(this SmartFilter filter, Game game) => Matches(filter, game, DateTime.UtcNow);

    /// <param name="nowUtc">The time to compare "recently added" with (the tests pass their own).</param>
    internal static bool Matches(this SmartFilter filter, Game game, DateTime nowUtc)
    {
        // (a game started from GameDeck whose playtime couldn't be counted still was played: it has a last-played date)
        var played = game.PlaytimeSeconds > 0 || game.LastPlayed is not null;

        return filter switch
        {
            SmartFilter.NeverPlayed => !played,
            SmartFilter.UnderAnHour => played && game.PlaytimeSeconds < 3600,
            SmartFilter.RecentlyAdded => game.AddedUtc is { } added && added > DateTime.MinValue && nowUtc - added <= RecentlyAddedFor,
            _ => false
        };
    }
}
