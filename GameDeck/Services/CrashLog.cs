using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GameDeck.Services;

/// <summary>
/// Writes what went wrong to a file (in %AppData%\GameDeck\logs) and builds a pre-filled bug report for GitHub. Nothing is sent
/// anywhere by GameDeck itself: the report only opens in the browser, and nothing happens until the user presses Submit there.
/// </summary>
public static class CrashLog
{
    private const int KeepLogs = 10;
    private const int MaxUrlLength = 6500;   // browsers and GitHub accept long addresses, but not unlimited ones

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck", "logs");

    public static string LogFolder => Folder;

    /// <summary>Saves the error and returns the file's path (null if even that failed). Never throws.</summary>
    public static string? Write(Exception? error, string where)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, Describe(error, where));

            // keep the newest few, so the folder never grows
            foreach (var old in Directory.EnumerateFiles(Folder, "crash-*.log").OrderByDescending(f => f).Skip(KeepLogs))
            {
                try { File.Delete(old); } catch { }
            }

            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What a log or a report says: version, system and the error, with the user's name taken out.</summary>
    internal static string Describe(Exception? error, string where) => Sanitize(string.Join(Environment.NewLine,
        $"GameDeck {UpdateService.CurrentVersionText} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})",
        $"Windows: {RuntimeInformation.OSDescription}",
        $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        $"Where: {where}",
        "",
        error?.ToString() ?? "(no details)"));

    /// <summary>The bug report form of the project, with what is known already filled in. Nothing is sent from here.</summary>
    public static string ReportUrl(Exception? error)
    {
        var shortError = error is null ? "unexpected error" : $"{error.GetType().Name}: {error.Message}";

        var details = new StringBuilder("GameDeck stopped working and showed its error window.\n\nError: ")
            .Append(Sanitize(shortError))
            .Append("\n\n```\n");

        var stack = Sanitize(error?.StackTrace ?? "");
        foreach (var line in stack.Split('\n').Take(8))
            details.Append(line.TrimEnd()).Append('\n');

        details.Append("```\n\n(The full details are in the newest file of %AppData%\\GameDeck\\logs.)");

        string Url(string text) =>
            "https://github.com/athathin/GameDeck/issues/new?template=bug_report.yml" +
            "&title=" + Uri.EscapeDataString("[Bug] " + Sanitize(error?.GetType().Name ?? "Error")) +
            "&version=" + Uri.EscapeDataString(UpdateService.CurrentVersionText) +
            "&bits=" + Uri.EscapeDataString(Environment.Is64BitProcess ? "GameDeck-win-x64.exe (64-bit)" : "GameDeck-win-x86.exe (32-bit)") +
            "&windows=" + Uri.EscapeDataString(RuntimeInformation.OSDescription) +
            "&what-happened=" + Uri.EscapeDataString(text);

        // too long for an address: cut the details until it fits
        var body = details.ToString();
        var url = Url(body);
        while (url.Length > MaxUrlLength && body.Length > 200)
        {
            body = body[..(body.Length * 3 / 4)];
            url = Url(body);
        }

        return url;
    }

    /// <summary>Replaces the user's name and profile folder, so a log or a report can be shared without them.</summary>
    internal static string Sanitize(string text)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            // the name as a folder in a path (\name\ or /name/), never inside another word ("Game" in "GameDeck")
            var user = Environment.UserName;
            if (!string.IsNullOrEmpty(user))
                text = Regex.Replace(text, @"(?<=[\\/])" + Regex.Escape(user) + @"(?=[\\/]|$)", "<user>", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
        catch
        {
            // the text is still usable without it
        }

        return text;
    }
}
