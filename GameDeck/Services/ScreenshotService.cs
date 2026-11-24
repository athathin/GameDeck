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

        found.AddRange(Pictures(FolderOf(game), "*"));

        // Game Bar names its files after the window's title, which is nearly always the game's name. The folder can hold
        // thousands of captures of other games: only the files that start with this game's name are asked for.
        var invalid = Path.GetInvalidFileNameChars();
        var names = new[] { game.Name, game.DisplayName }
            .Select(n => n.Trim())
            .Where(n => n.Length >= 3 && n.IndexOfAny(invalid) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
            found.AddRange(Pictures(CapturesFolder, name + "*"));

        found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return found
            .Select(f => (File: f, Time: SafeWriteTime(f)))
            .OrderByDescending(f => f.Time)
            .Select(f => f.File)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Takes a picture of the screen the program in front is on (the controller's Home + RT) and saves it as a .png: in the
    /// game's own folder when a game from GameDeck is running (so its page shows it), otherwise in Pictures\Screenshots, where
    /// Windows keeps its own. A game in exclusive full screen may come out black: most games today run borderless and are fine.
    /// </summary>
    /// <returns>The saved file, or null when it couldn't be taken or saved.</returns>
    public static string? Capture(Game? game)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.FromHandle(GetForegroundWindow()).Bounds;

            using var picture = new System.Drawing.Bitmap(screen.Width, screen.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(picture))
                graphics.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);

            var folder = game is not null
                ? FolderOf(game)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(folder);

            var name = $"{(game is not null ? SafeName(game.Name) : "Screenshot")} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            var file = Path.Combine(folder, name + ".png");
            for (var n = 2; File.Exists(file); n++)   // two in the same second
                file = Path.Combine(folder, $"{name} ({n}).png");

            picture.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                   ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            CrashLog.Write(ex, "Screenshot");
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    private static IEnumerable<string> Pictures(string folder, string pattern)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];

            return Directory.EnumerateFiles(folder, pattern)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
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
