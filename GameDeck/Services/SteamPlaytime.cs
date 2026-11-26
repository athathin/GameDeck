using System.IO;
using System.Text;
using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>
/// The playtime Steam counted for each game, read from Steam's own files on this PC (nothing goes online): every Steam account
/// that signed in here has a localconfig.vdf with, per game, "Playtime" (minutes) and "LastPlayed" (a Unix time). With several
/// accounts, the highest playtime and the latest date win.
/// </summary>
public static class SteamPlaytime
{
    /// <summary>One game's playtime as Steam counted it.</summary>
    public record Played(long Seconds, DateTime? LastPlayedUtc);

    /// <summary>
    /// Steam's playtime per game, keyed by GameDeck's launch target ("steam://rungameid/440"). Empty when Steam isn't installed
    /// or its files can't be read. Call from a background thread (it reads files).
    /// </summary>
    public static Dictionary<string, Played> Read()
    {
        var result = new Dictionary<string, Played>(StringComparer.OrdinalIgnoreCase);

        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return result;

        var userData = Path.Combine(steam.Replace('/', '\\'), "userdata");
        if (!Directory.Exists(userData)) return result;

        foreach (var account in Directory.EnumerateDirectories(userData))
        {
            var file = Path.Combine(account, "config", "localconfig.vdf");
            try
            {
                if (!File.Exists(file)) continue;

                foreach (var (appId, played) in Parse(File.ReadAllText(file)))
                    result[$"steam://rungameid/{appId}"] = result.TryGetValue($"steam://rungameid/{appId}", out var known)
                        ? new Played(Math.Max(known.Seconds, played.Seconds), Latest(known.LastPlayedUtc, played.LastPlayedUtc))
                        : played;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Steam is writing it right now, or it can't be read: that account is left out this time
            }
        }

        return result;
    }

    /// <summary>The games of one localconfig.vdf (path "...\Software\Valve\Steam\apps\&lt;app id&gt;") that have a playtime.</summary>
    internal static Dictionary<string, Played> Parse(string vdf)
    {
        var played = new Dictionary<string, Played>(StringComparer.OrdinalIgnoreCase);

        var root = ParseVdf(vdf);
        var apps = Child(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps")
                   ?? Child(root, "Software", "Valve", "Steam", "apps");
        if (apps is null) return played;

        foreach (var (appId, value) in apps)
        {
            if (value is not Dictionary<string, object> app || !long.TryParse(Text(app, "Playtime"), out var minutes) || minutes <= 0)
                continue;

            DateTime? last = long.TryParse(Text(app, "LastPlayed"), out var unix) && unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : null;

            played[appId] = new Played(minutes * 60, last);
        }

        return played;
    }

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static string? Text(Dictionary<string, object> node, string key) =>
        node.TryGetValue(key, out var value) ? value as string : null;

    private static Dictionary<string, object>? Child(Dictionary<string, object> node, params string[] path)
    {
        var current = node;
        foreach (var key in path)
        {
            if (!current.TryGetValue(key, out var next) || next is not Dictionary<string, object> child) return null;
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Steam's text format (VDF): "key" "value" pairs and "key" { ... } blocks, with \" and \\ inside quotes. Keys are compared
    /// without case, as Steam writes the same key in different cases ("apps" / "Apps").
    /// </summary>
    internal static Dictionary<string, object> ParseVdf(string text)
    {
        var position = 0;
        return ReadBlock(text, ref position);
    }

    private static Dictionary<string, object> ReadBlock(string text, ref int position)
    {
        var block = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (ReadToken(text, ref position) is { } token)
        {
            if (token == "}") break;   // the end of this block
            if (token == "{") continue;   // (a stray brace)

            var key = token;
            var next = ReadToken(text, ref position);
            if (next is null) break;

            block[key] = next == "{" ? ReadBlock(text, ref position) : next;
        }

        return block;
    }

    /// <summary>The next quoted string, "{" or "}"; null at the end. Comments (//) are skipped.</summary>
    private static string? ReadToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];

            if (char.IsWhiteSpace(c)) { position++; continue; }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }

            if (c is '{' or '}')
            {
                position++;
                return c.ToString();
            }

            if (c == '"')
            {
                var value = new StringBuilder();
                position++;
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                        position++;   // \" and \\: the next character as it is
                    value.Append(text[position++]);
                }

                position++;   // the closing quote
                return value.ToString();
            }

            // an unquoted word (rare): up to the next space or brace
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
                position++;
            return text[start..position];
        }

        return null;
    }
}
