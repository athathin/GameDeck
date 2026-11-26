using System.IO;
using System.Text;
using GameDeck.Models;
using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>
/// What kind of Steam app each installed one is, from Steam's own file on this PC (appcache\appinfo.vdf, nothing goes online):
/// a game, a demo, a tool or application, or an add-on of a game (DLC, a soundtrack) with the app id of that game. Steam
/// installs some soundtracks and DLC as apps of their own, which would otherwise show up as games.
/// </summary>
public static class SteamAppInfo
{
    /// <summary>One app: its type as Steam writes it ("Game", "DLC", "Music", "Tool", "Application", "Demo"...) and its game, if any.</summary>
    public sealed record App(string Type, string? Parent)
    {
        /// <summary>DLC or a soundtrack: it belongs to a game and isn't played by itself.</summary>
        public bool IsAddOn => Type.Equals("DLC", StringComparison.OrdinalIgnoreCase) || Type.Equals("Music", StringComparison.OrdinalIgnoreCase);
    }

    private const uint Version27 = 0x07564427, Version28 = 0x07564428, Version29 = 0x07564429;

    /// <summary>The apps of <paramref name="appIds"/> found in Steam's file (an empty list when Steam or the file isn't there).</summary>
    public static Dictionary<string, App> Read(IEnumerable<string> appIds)
    {
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(steam)) return [];

            var file = Path.Combine(steam.Replace('/', '\\'), "appcache", "appinfo.vdf");
            return File.Exists(file) ? Parse(File.ReadAllBytes(file), appIds.ToHashSet()) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return [];   // (a file Steam is writing, or a newer format: the add-ons just show as games)
        }
    }

    /// <summary>
    /// Reads appinfo.vdf: a header (version, universe and, since version 29, where the table of names starts), then one entry per
    /// app (its id, the entry's size, 56 or 36 bytes of checksums and dates, then its values), ended by an app id of 0.
    /// Only the apps asked for are decoded; the others are skipped by their size.
    /// </summary>
    internal static Dictionary<string, App> Parse(byte[] data, IReadOnlySet<string> wanted)
    {
        var apps = new Dictionary<string, App>();
        if (data.Length < 8) return apps;

        var version = BitConverter.ToUInt32(data, 0);
        if (version is not (Version27 or Version28 or Version29)) throw new FormatException("Unknown appinfo.vdf version.");

        var position = 8;
        string[]? names = null;
        if (version == Version29)
        {
            var tableAt = BitConverter.ToInt64(data, position);
            position += 8;
            names = ReadNames(data, tableAt);
        }

        var skip = version == Version27 ? 4 + 4 + 8 + 20 + 4 : 4 + 4 + 8 + 20 + 4 + 20;   // (version 28 added a second checksum)

        while (position + 8 <= data.Length)
        {
            var appId = BitConverter.ToUInt32(data, position);
            if (appId == 0) break;

            var size = BitConverter.ToInt32(data, position + 4);
            var start = position + 8;
            if (size < skip || start + size > data.Length) throw new FormatException("An entry is cut short.");

            position = start + size;

            var id = appId.ToString();
            if (!wanted.Contains(id)) continue;

            var values = BinaryKeyValues.Parse(data, start + skip, size - skip, names);
            if (Section(values, "appinfo") is not { } info || Section(info, "common") is not { } common) continue;

            var type = common.TryGetValue("type", out var t) ? t.ToString() ?? "" : "";
            var parent = common.TryGetValue("parent", out var p) ? p.ToString() : null;
            apps[id] = new App(type, string.IsNullOrWhiteSpace(parent) || parent == "0" ? null : parent);
        }

        return apps;
    }

    private static Dictionary<string, object>? Section(Dictionary<string, object> values, string name) =>
        values.TryGetValue(name, out var value) ? value as Dictionary<string, object> : null;

    /// <summary>The table of names at the end of a version 29 file: a count, then that many texts each ending in a zero byte.</summary>
    private static string[] ReadNames(byte[] data, long at)
    {
        if (at < 0 || at + 4 > data.Length) throw new FormatException("The table of names is missing.");

        var position = (int)at;
        var count = BitConverter.ToInt32(data, position);
        position += 4;
        if (count < 0 || count > data.Length) throw new FormatException("The table of names is damaged.");

        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var stop = Array.IndexOf(data, (byte)0, position);
            if (stop < 0) throw new FormatException("A name has no end.");

            names[i] = Encoding.UTF8.GetString(data, position, stop - position);
            position = stop + 1;
        }

        return names;
    }

    /// <summary>The DLC and soundtracks of <paramref name="game"/> that are installed as Steam apps of their own (its page lists them).</summary>
    public static List<Game> AddOnsOf(IEnumerable<Game> library, Game game) =>
        game.SteamAppId is { } id ? library.Where(g => g.AddOnOf == id).ToList() : [];

    /// <summary>
    /// Marks the Steam games that are add-ons (DLC, soundtracks) of another game, from what <see cref="Read"/> found. Games of
    /// other launchers, and Steam apps Steam's file doesn't describe, stay games.
    /// </summary>
    public static void Apply(IEnumerable<Game> games, IReadOnlyDictionary<string, App> apps)
    {
        foreach (var game in games)
        {
            var app = game.SteamAppId is { } id && apps.TryGetValue(id, out var found) ? found : null;
            game.AddOnOf = app is { IsAddOn: true } ? app.Parent ?? "" : null;
        }
    }
}
