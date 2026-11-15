using System.IO;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Finds games that are probably the same game listed twice: installed through two launchers, or added by hand
/// although a launcher already found it. Deliberately careful: the user confirms every group anyway.
/// Two games are linked when
///  - their names are the same once capitals, spaces and punctuation are ignored, or
///  - one's program lives inside the other's install folder (or both share the same install folder).
/// Games that are already hidden are ignored, so a resolved group doesn't come back.
/// </summary>
public static class DuplicateFinder
{
    public static List<List<Game>> Find(IEnumerable<Game> library)
    {
        var games = library.Where(g => !g.IsHidden && !g.IsRandomTile).ToList();

        // union-find: parent[i] points towards the group's representative
        var parent = Enumerable.Range(0, games.Count).ToArray();

        int Root(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        void Link(int a, int b) => parent[Root(a)] = Root(b);

        // same name
        var byName = new Dictionary<string, int>();
        for (var i = 0; i < games.Count; i++)
        {
            var key = Normalize(games[i].Name);
            if (key.Length == 0) continue;

            if (byName.TryGetValue(key, out var first))
                Link(i, first);
            else
                byName[key] = i;
        }

        // same install folder, or a program inside another game's folder
        for (var i = 0; i < games.Count; i++)
        {
            for (var j = i + 1; j < games.Count; j++)
            {
                if (SameFolder(games[i], games[j]))
                    Link(i, j);
            }
        }

        return Enumerable.Range(0, games.Count)
            .GroupBy(Root)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(i => games[i])
                          .OrderByDescending(x => x.PlaytimeSeconds)
                          .ThenBy(x => x.Source == "Manual")   // the launcher's own entry before a hand-added one
                          .ToList())
            .OrderBy(g => g[0].DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Lower case, letters and digits only: "The Witcher 3: Wild Hunt™" and "the witcher 3 wild hunt" match.</summary>
    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool SameFolder(Game a, Game b)
    {
        var folderA = Folder(a.InstallDir);
        var folderB = Folder(b.InstallDir);

        if (folderA is not null && folderA == folderB)
            return true;

        return IsInside(a.LaunchTarget, folderB) || IsInside(b.LaunchTarget, folderA);
    }

    /// <summary>The folder with a closing backslash and no case, so folders can be compared as text. Null if there is none.</summary>
    private static string? Folder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant() + "\\";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when a file path (not a URI like steam://...) is inside the folder.</summary>
    private static bool IsInside(string launchTarget, string? folder)
    {
        if (folder is null || !Path.IsPathRooted(launchTarget)) return false;

        try
        {
            return Path.GetFullPath(launchTarget).ToLowerInvariant().StartsWith(folder, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
