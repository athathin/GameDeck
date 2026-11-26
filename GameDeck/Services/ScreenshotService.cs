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
    /// <summary>The kinds of pictures GameDeck shows and adds.</summary>
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>Where screenshots usually are, for Big Picture's picture browser: Pictures › Screenshots, Videos › Captures.</summary>
    internal static IEnumerable<(string Name, string Path)> UsualPlaces()
    {
        if (SettingsService.ScreenshotFolder is { } chosen)
            yield return ("Your screenshots folder  (Settings)", chosen);

        yield return ("Screenshots  (Pictures)", PicturesScreenshots);
        yield return ("Captures  (Game Bar, in Videos)", CapturesFolder);
        yield return ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
    }

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "screenshots");

    private static string CapturesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    private static string PicturesScreenshots =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>The folder GameDeck keeps this game's added screenshots in.</summary>
    public static string FolderOf(Game game) => Path.Combine(Root, SafeName(game.Name));

    /// <summary>Opens File Explorer on the picture's folder with the picture selected.</summary>
    /// <returns>False when it couldn't be shown (the file is gone, Explorer didn't start).</returns>
    public static bool ShowInFolder(string file)
    {
        try
        {
            if (!File.Exists(file)) return false;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

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

    /// <summary>The games of the library (set by the main window), so a game's page can tell its captures from another game's.</summary>
    public static Func<IReadOnlyCollection<Game>>? Library { get; set; }

    /// <summary>The newest screenshots of the game (own folder, Game Bar captures, the chosen screenshots folder), newest first.</summary>
    /// <param name="library">
    /// Every game: a capture belongs to the game whose whole name is the longest one it starts with ("Portal 2 ....png" is
    /// Portal 2's, not Portal's). Without it, only this game's names are compared.
    /// </param>
    public static List<string> Find(Game game, IReadOnlyCollection<Game>? library = null, int max = 12)
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

        var everyName = NamesOf(library is null ? [game] : library.Append(game));

        foreach (var folder in new[] { CapturesFolder, SettingsService.ScreenshotFolder })
        {
            if (folder is null) continue;

            foreach (var name in names)
                found.AddRange(Pictures(folder, name + "*")
                    .Where(f => BelongsTo(Path.GetFileName(f), names, everyName)));
        }

        found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return found
            .Select(f => (File: f, Time: SafeWriteTime(f)))
            .OrderByDescending(f => f.Time)
            .Select(f => f.File)
            .Take(max)
            .ToList();
    }

    // ---------- the Media tab: every game's screenshots in one place ----------

    /// <summary>One picture of the Media tab: the file, the game it belongs to (null = none matched: "Other"), and when it was taken.</summary>
    public record MediaShot(string File, Game? Game, DateTime TakenUtc);

    /// <summary>
    /// Every screenshot GameDeck knows of, newest first: each game's own folder, the Game Bar captures and the chosen
    /// screenshots folder (a picture there belongs to the game its name starts with). Hidden games' pictures are left out.
    /// Call from a background thread (it reads the folders).
    /// </summary>
    /// <param name="max">At most this many (the newest).</param>
    public static List<MediaShot> FindAll(IReadOnlyCollection<Game> games, int max = 2000)
    {
        var shots = new Dictionary<string, MediaShot>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            foreach (var file in Pictures(FolderOf(game), "*"))
                shots.TryAdd(file, new MediaShot(file, game, SafeWriteTime(file)));
        }

        var names = NamesOf(games);
        foreach (var folder in new[] { CapturesFolder, SettingsService.ScreenshotFolder })
        {
            if (folder is null) continue;

            foreach (var file in Pictures(folder, "*"))
                shots.TryAdd(file, new MediaShot(file, MatchGame(Path.GetFileName(file), names), SafeWriteTime(file)));
        }

        return shots.Values
            .Where(s => s.Game is not { IsHidden: true })
            .OrderByDescending(s => s.TakenUtc)
            .Take(max)
            .ToList();
    }

    /// <summary>The names a game's pictures can start with (its name, the name shown, the file-safe name), longest first.</summary>
    internal static List<(string Name, Game Game)> NamesOf(IEnumerable<Game> games) =>
        games.SelectMany(g => new[] { g.Name, g.DisplayName, SafeName(g.Name) }.Select(n => (Name: n.Trim(), Game: g)))
            .Where(n => n.Name.Length >= 3)
            .DistinctBy(n => (n.Name.ToUpperInvariant(), n.Game))
            .OrderByDescending(n => n.Name.Length)   // "Portal 2 ..." belongs to Portal 2, not to Portal
            .ToList();

    /// <summary>The game a picture's file name starts with, or null.</summary>
    internal static Game? MatchGame(string fileName, List<(string Name, Game Game)> names) =>
        names.FirstOrDefault(n => StartsWithName(fileName, n.Name)).Game;

    /// <summary>
    /// True when the longest game name the file starts with (of <paramref name="everyName"/>) is one of the game's own names
    /// (<paramref name="gameNames"/>). Compared by name, so two copies of one game (Steam and Epic) both show the picture.
    /// </summary>
    internal static bool BelongsTo(string fileName, IReadOnlyCollection<string> gameNames, List<(string Name, Game Game)> everyName) =>
        everyName.FirstOrDefault(n => StartsWithName(fileName, n.Name)).Name is { } matched &&
        gameNames.Contains(matched, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the file name starts with the name as a whole: what follows isn't another letter or digit
    /// ("Portal 2026-11-26.png" starts with "Portal", not with "Portal 2").
    /// </summary>
    private static bool StartsWithName(string fileName, string name) =>
        fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
        (fileName.Length == name.Length || !char.IsLetterOrDigit(fileName[name.Length]));

    /// <summary>A small copy of the picture (a 4K screenshot is not decoded in full for a thumbnail); null when it can't be read.</summary>
    /// <remarks>Frozen, so it can be made on a background thread.</remarks>
    public static System.Windows.Media.Imaging.BitmapImage? Thumbnail(string file, int width = 352)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = width;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // read now, so the file isn't kept open
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException or
                                   ArgumentException)
        {
            return null;
        }
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
