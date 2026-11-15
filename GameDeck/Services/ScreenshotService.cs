using System.IO;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// A game's screenshots, for its page: the pictures in GameDeck's own folder for that game (put there with "Add screenshots"),
/// and the Windows Game Bar captures (Win + Alt + PrtScn, saved in Videos\Captures as "&lt;game&gt; &lt;date&gt;.png") that belong to it by name.
/// Nothing is uploaded or watched: the folders are only read when a page opens.
/// </summary>
public static class ScreenshotService
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "screenshots");

    private static string CapturesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    /// <summary>The folder GameDeck keeps this game's added screenshots in.</summary>
    public static string FolderOf(Game game) => Path.Combine(Root, SafeName(game.Name));

    /// <summary>The newest screenshots of the game (own folder and Game Bar captures), newest first.</summary>
    public static List<string> Find(Game game, int max = 12)
    {
        var found = new List<string>();

        found.AddRange(Pictures(FolderOf(game), "*", null));

        // Game Bar names its files after the window's title, which is nearly always the game's name. The folder can hold
        // thousands of captures of other games: only the files that start with this game's name are asked for.
        var invalid = Path.GetInvalidFileNameChars();
        var names = new[] { game.Name, game.DisplayName }
            .Select(n => n.Trim())
            .Where(n => n.Length >= 3 && n.IndexOfAny(invalid) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
            found.AddRange(Pictures(CapturesFolder, name + "*", null));

        found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return found
            .Select(f => (File: f, Time: SafeWriteTime(f)))
            .OrderByDescending(f => f.Time)
            .Select(f => f.File)
            .Take(max)
            .ToList();
    }

    /// <summary>Copies pictures into the game's own folder. Returns how many were added.</summary>
    public static int Add(Game game, IEnumerable<string> files)
    {
        var folder = FolderOf(game);
        var added = 0;

        try
        {
            Directory.CreateDirectory(folder);

            foreach (var file in files.Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                var target = Path.Combine(folder, Path.GetFileName(file));
                for (var n = 2; File.Exists(target); n++)   // the same name twice: keep both
                    target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(file)} ({n}){Path.GetExtension(file)}");

                File.Copy(file, target);
                added++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what was copied so far stays; the page shows what is there
        }

        return added;
    }

    private static IEnumerable<string> Pictures(string folder, string pattern, Func<string, bool>? filter)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];

            return Directory.EnumerateFiles(folder, pattern)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && (filter?.Invoke(f) ?? true))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTime SafeWriteTime(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return DateTime.MinValue; }
    }

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "game" : safe;
    }
}
