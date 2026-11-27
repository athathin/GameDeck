using System.IO;
using GameDeck.Models;
using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>One line of the Stats tab's "Launchers" card.</summary>
public record LauncherCount(string Name, string Count);

/// <summary>
/// How many games each launcher on this PC has, for the Stats tab. A launcher is listed when it is installed (found in Windows'
/// list of installed programs, or by its own files), or when some games in the library come from it. So a launcher that isn't on
/// the PC never shows up as "0 games".
/// </summary>
public static class LauncherStats
{
    /// <summary>
    /// The launchers Windows lists among installed programs (their Source names, and how they are listed there). Riot and the
    /// Xbox app aren't in that list: InstalledLaunchers finds them by their own file and package.
    /// </summary>
    private static readonly (string Source, Func<string, bool> IsListed)[] Listed =
    [
        ("Steam", n => n.Equals("Steam", StringComparison.OrdinalIgnoreCase)),
        ("Epic", n => n.Equals("Epic Games Launcher", StringComparison.OrdinalIgnoreCase)),
        ("GOG", n => n.StartsWith("GOG GALAXY", StringComparison.OrdinalIgnoreCase)),
        ("Ubisoft", n => n.StartsWith("Ubisoft Connect", StringComparison.OrdinalIgnoreCase) || n.Equals("Uplay", StringComparison.OrdinalIgnoreCase)),
        ("Battle.net", n => n.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)),
        ("EA", n => n.Equals("EA app", StringComparison.OrdinalIgnoreCase)),
        ("Rockstar", n => n.Equals("Rockstar Games Launcher", StringComparison.OrdinalIgnoreCase)),
        ("itch.io", n => n.Equals("itch", StringComparison.OrdinalIgnoreCase)),
        ("Amazon Games", n => n.Equals("Amazon Games", StringComparison.OrdinalIgnoreCase))
    ];

    private const string ManualSource = "Manual";   // games added by hand: shown as "Other"

    /// <summary>
    /// One line per launcher that is installed or has games, most games first: "Steam" / "34 games". Hidden games and DLC aren't
    /// counted. Games added by hand come last as "Other", only when there are some.
    /// </summary>
    public static List<LauncherCount> Lines(IEnumerable<Game> library, IReadOnlySet<string> installed)
    {
        var counts = library.Where(g => !g.IsHidden && !g.IsAddOn)
            .GroupBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var sources = counts.Keys.Where(s => s != ManualSource)
            .Union(installed, StringComparer.OrdinalIgnoreCase)
            .Select(s => (Name: s, Count: counts.GetValueOrDefault(s)))
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (counts.GetValueOrDefault(ManualSource) is > 0 and var manual)
            sources.Add(("Other", manual));

        return sources.Select(s => new LauncherCount(s.Name, s.Count == 1 ? "1 game" : $"{s.Count} games")).ToList();
    }

    private static Task<HashSet<string>>? _installed;

    /// <summary>
    /// The launchers installed on this PC, read once in the background and shared by the desktop and Big Picture Stats tabs.
    /// <paramref name="refresh"/> (their Refresh) reads them again, for a launcher installed since.
    /// </summary>
    private static Task<HashSet<string>> InstalledLaunchersAsync(bool refresh = false)
    {
        if (refresh || _installed is null || _installed.IsFaulted)
            _installed = Task.Run(InstalledLaunchers);
        return _installed;
    }

    /// <summary>
    /// The Launchers card's lines right now: with the installed launchers once they are known, otherwise only the launchers
    /// that have games (and then <paramref name="whenKnown"/> runs, on the UI thread, once they are known: draw the card again).
    /// </summary>
    public static List<LauncherCount> LinesNow(IEnumerable<Game> library, Action whenKnown, bool refresh = false)
    {
        var task = InstalledLaunchersAsync(refresh);
        if (task.IsCompletedSuccessfully)
            return Lines(library, task.Result);

        task.ContinueWith(t => { if (t.IsCompletedSuccessfully) whenKnown(); }, TaskScheduler.FromCurrentSynchronizationContext());
        return Lines(library, new HashSet<string>());
    }

    /// <summary>The launchers installed on this PC (their Source names). Reads the registry and a few files: call off the UI thread.</summary>
    public static HashSet<string> InstalledLaunchers()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in InstalledProgramNames())
            foreach (var (source, isListed) in Listed)
                if (isListed(name))
                    found.Add(source);

        try
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                         "Riot Games", "RiotClientInstalls.json")))
                found.Add("Riot");
        }
        catch { }

        if (XboxAppInstalled())
            found.Add("Xbox");

        return found;
    }

    /// <summary>The names in Windows' "Installed apps" list (both registry views of this PC, and the user's own).</summary>
    private static IEnumerable<string> InstalledProgramNames()
    {
        const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var names = new List<string>();

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default)
                 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(Root);
                if (key is null) continue;

                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    if (app?.GetValue("DisplayName") is string name && name.Length > 0)
                        names.Add(name.Trim());
                }
            }
            catch
            {
                // (a key Windows won't let us read: skip it)
            }
        }

        return names;
    }

    /// <summary>True when the Xbox app (the Store package Microsoft.GamingApp) is installed for this user.</summary>
    private static bool XboxAppInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return key?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }
}
