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

    private static string PicturesScreenshots =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>The folder GameDeck keeps this game's added screenshots in.</summary>
    public static string FolderOf(Game game) => Path.Combine(Root, SafeName(game.Name));

    /// <summary>Where the controller's screenshots go, in words for the settings pages.</summary>
    public static string SaveFolderText => SettingsService.ScreenshotFolder ??
        "with each game's screenshots (on its page), or Pictures › Screenshots";

    /// <summary>
    /// Lets the user pick the folder the controller's screenshots are saved in (Windows' folder picker).
    /// </summary>
    /// <returns>True when a folder was chosen (and saved in the settings).</returns>
    public static bool ChooseSaveFolder(System.Windows.Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Save the controller's screenshots in…",
            InitialDirectory = SettingsService.ScreenshotFolder is { } chosen && Directory.Exists(chosen) ? chosen : PicturesScreenshots
        };

        if (dialog.ShowDialog(owner) != true) return false;

        SettingsService.ScreenshotFolder = dialog.FolderName;
        return true;
    }

    /// <summary>The newest screenshots of the game (own folder, Game Bar captures, the chosen screenshots folder), newest first.</summary>
    public static List<string> Find(Game game, int max = 12)
    {
        var found = new List<string>();

        found.AddRange(Pictures(FolderOf(game), "*"));

        // Game Bar names its files after the window's title, which is nearly always the game's name, and GameDeck names the
        // ones it saves in a chosen folder after the game. Those folders can hold thousands of pictures of other games: only
        // the files that start with this game's name are asked for.
        var invalid = Path.GetInvalidFileNameChars();
        var names = new[] { game.Name, game.DisplayName, SafeName(game.Name) }
            .Select(n => n.Trim())
            .Where(n => n.Length >= 3 && n.IndexOfAny(invalid) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in new[] { CapturesFolder, SettingsService.ScreenshotFolder })
        {
            if (folder is null) continue;

            foreach (var name in names)
                found.AddRange(Pictures(folder, name + "*"));
        }

        found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return found
            .Select(f => (File: f, Time: SafeWriteTime(f)))
            .OrderByDescending(f => f.Time)
            .Select(f => f.File)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Takes a picture of the screen the program in front is on (the controller's Home + RT) and saves it as a .png, named after
    /// the running game: in the folder chosen in the settings; without one, in the game's own folder when a game from GameDeck
    /// is running (so its page shows it), otherwise in Pictures\Screenshots, where Windows keeps its own. A game in exclusive
    /// full screen may come out black: most games today run borderless and are fine.
    /// </summary>
    /// <returns>The saved file, or null when it couldn't be taken or saved.</returns>
    /// <remarks>The picture is taken at once (on the calling thread); turning it into a .png and saving it happens in the background.</remarks>
    public static async Task<string?> CaptureAsync(Game? game)
    {
        System.Drawing.Bitmap? picture = null;
        try
        {
            picture = GrabScreen();

            var folder = SettingsService.ScreenshotFolder ?? (game is not null ? FolderOf(game) : PicturesScreenshots);
            var name = $"{(game is not null ? SafeName(game.Name) : "Screenshot")} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";

            var shot = picture;
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);

                var file = Path.Combine(folder, name + ".png");
                for (var n = 2; File.Exists(file); n++)   // two in the same second
                    file = Path.Combine(folder, $"{name} ({n}).png");

                shot.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                return file;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                   ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            CrashLog.Write(ex, "Screenshot");
            return null;
        }
        finally
        {
            picture?.Dispose();
        }
    }

    /// <summary>
    /// The screen the program in front is on, pixel for pixel. Measured as a per-monitor DPI aware thread: GameDeck itself
    /// isn't, and would get the size of a screen whose scaling differs from the main one's wrong (a cropped picture).
    /// </summary>
    private static System.Drawing.Bitmap GrabScreen()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var screen = System.Windows.Forms.Screen.FromHandle(GetForegroundWindow()).Bounds;

            var picture = new System.Drawing.Bitmap(screen.Width, screen.Height);
            using var graphics = System.Drawing.Graphics.FromImage(picture);
            graphics.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);
            return picture;
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    private static readonly IntPtr PerMonitorAwareV2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

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
