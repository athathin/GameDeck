using GameDeck.Models;

namespace GameDeck.Views;

/// <summary>One game of a home shelf, with the short line under its name ("Today", "Added 3 Nov").</summary>
public sealed record ShelfItem(Game Game, string Note);

/// <summary>A row of games on Big Picture's home screen: "Continue playing", "Recently added", a category...</summary>
public sealed record Shelf(string Title, IReadOnlyList<ShelfItem> Items);

/// <summary>
/// The shelves Big Picture's home row can show, in order: Continue playing, Up next, Recently added, Favorites, then one per
/// category. Hidden games are left out, and so is a shelf with no games.
/// </summary>
public static class HomeShelves
{
    public static List<Shelf> Build(IEnumerable<Game> library, int count, DateTime nowUtc)
    {
        var games = library.Where(g => !g.IsHidden).ToList();
        var today = DateOnly.FromDateTime(nowUtc.ToLocalTime());

        // the games played last first, then by name: the order of the shelves without an order of their own
        IEnumerable<Game> Usual(IEnumerable<Game> some) => some
            .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
            .ThenBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        string Played(Game g) => g.LastPlayed is { } t ? Day(t, today) : "Not played yet";

        var shelves = new List<Shelf>
        {
            // (with "where I left off" in place of the date when there is one)
            Make("CONTINUE PLAYING", games.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed),
                g => g.HasLeftOff ? "Left off: " + g.LeftOff : Played(g)),
            Make("UP NEXT", Usual(games.Where(g => g.Status == GameStatus.UpNext)), Played),
            Make("RECENTLY ADDED", games
                    .Where(g => SmartFilter.RecentlyAdded.Matches(g, nowUtc))
                    .OrderByDescending(g => g.AddedUtc),
                g => "Added " + Day(g.AddedUtc!.Value, today, "today", "yesterday", "d MMM")),
            Make("FAVORITES", Usual(games.Where(g => g.IsFavorite)), Played)
        };

        var categories = games.SelectMany(g => g.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase);
        foreach (var category in categories)
            shelves.Add(Make(category.ToUpper(), Usual(games.Where(g => g.Tags.Contains(category, StringComparer.CurrentCultureIgnoreCase))), Played));

        return shelves.Where(s => s.Items.Count > 0).ToList();

        Shelf Make(string title, IEnumerable<Game> some, Func<Game, string> note) =>
            new(title, some.Take(count).Select(g => new ShelfItem(g, note(g))).ToList());
    }

    /// <summary>"Today", "Yesterday", or the date ("3 Nov 2026").</summary>
    internal static string Day(DateTime utc, DateOnly today,
        string todayText = "Today", string yesterdayText = "Yesterday", string format = "d MMM yyyy")
    {
        var day = DateOnly.FromDateTime(utc.ToLocalTime());
        return day == today ? todayText : day == today.AddDays(-1) ? yesterdayText : utc.ToLocalTime().ToString(format);
    }
}
