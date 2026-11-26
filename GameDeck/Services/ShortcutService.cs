using System.IO;
using System.IO.Pipes;
using System.Text;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Desktop shortcuts that start a game. The shortcut runs GameDeck with <c>--launch=&lt;the game's launch target&gt;</c>, so the
/// game starts the way it does from the library: playtime is counted, its launch routine runs, Riot games get Vanguard, and
/// its arguments / "run as administrator" apply. GameDeck starts in the tray for it, or the running GameDeck is asked
/// (through a named pipe) to start the game.
/// </summary>
public static class ShortcutService
{
    /// <summary>The command-line argument a game shortcut passes: <c>--launch=steam://rungameid/220</c>.</summary>
    public const string LaunchArgument = "--launch=";

    /// <summary>The pipe the running GameDeck listens on for "start this game" (per Windows session; Debug and the Beta have their own).</summary>
    public static readonly string PipeName =
#if DEBUG
        $"GameDeck-Launch-Debug{AppInfo.InstanceSuffix}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
#else
        $"GameDeck-Launch{AppInfo.InstanceSuffix}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
#endif

    /// <summary>The launch target a shortcut asked for, or null when GameDeck wasn't started by a game shortcut.</summary>
    public static string? LaunchTargetFrom(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(LaunchArgument, StringComparison.OrdinalIgnoreCase)) is { } arg &&
        arg.Length > LaunchArgument.Length
            ? arg[LaunchArgument.Length..]
            : null;

    /// <summary>
    /// Puts a shortcut for the game on the desktop. A shortcut of the same name that GameDeck made for this game is replaced;
    /// a different file with that name is left alone (the new one gets " (GameDeck)" added to its name).
    /// </summary>
    /// <returns>The shortcut's file, or null when it couldn't be made.</returns>
    public static string? CreateDesktopShortcut(Game game) =>
        Environment.ProcessPath is { } exe
            ? CreateShortcut(game, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), exe)
            : null;

    /// <summary>Makes the shortcut in <paramref name="desktop"/>, starting <paramref name="exe"/> (the tests use a folder of their own).</summary>
    internal static string? CreateShortcut(Game game, string desktop, string exe)
    {
        try
        {
            var arguments = $"\"{LaunchArgument}{game.LaunchTarget}\"";

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                string file = FreeFileName(shell, desktop, SafeName(game.DisplayName), exe, arguments);

                dynamic link = shell.CreateShortcut(file);
                link.TargetPath = exe;
                link.Arguments = arguments;
                link.WorkingDirectory = Path.GetDirectoryName(exe);
                link.IconLocation = IconFor(game, exe);
                link.Description = $"Play {game.DisplayName} with {AppInfo.DisplayName}";
                link.Save();

                return file;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write(ex, "Desktop shortcut");
            return null;
        }
    }

    /// <summary>"Name.lnk", unless a different shortcut already uses that name: then "Name (GameDeck).lnk", "Name (GameDeck 2).lnk"...</summary>
    private static string FreeFileName(dynamic shell, string folder, string name, string exe, string arguments)
    {
        for (var i = 1; ; i++)
        {
            var suffix = i switch { 1 => "", 2 => " (GameDeck)", _ => $" (GameDeck {i - 1})" };
            var file = Path.Combine(folder, name + suffix + ".lnk");
            if (!File.Exists(file)) return file;

            // ours for this game (made before): replace it
            dynamic existing = shell.CreateShortcut(file);
            if (string.Equals((string)existing.TargetPath, exe, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)existing.Arguments, arguments, StringComparison.OrdinalIgnoreCase))
                return file;
        }
    }

    /// <summary>The game's own icon when its program can be found, otherwise GameDeck's.</summary>
    private static string IconFor(Game game, string gameDeckExe)
    {
        if (game.IsDirectExe)
            return game.LaunchTarget + ",0";

        if (game.InstallDir is { } folder && Directory.Exists(folder) && ExeFinder.FindMainExe(folder) is { } exe)
            return exe + ",0";

        return gameDeckExe + ",0";
    }

    /// <summary>The name as a file name: characters Windows doesn't allow are left out.</summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Game" : safe;
    }

    // ---------- a second start of GameDeck (from a shortcut) hands the game to the running one ----------

    /// <summary>Asks the GameDeck that is already running to start the game.</summary>
    /// <returns>False when no running GameDeck answered.</returns>
    public static bool SendToRunningInstance(string launchTarget) => SendToRunningInstance(launchTarget, PipeName, 15000);

    /// <param name="pipeName">The pipe (the tests use one of their own, so they never reach a GameDeck that is running).</param>
    internal static bool SendToRunningInstance(string launchTarget, string pipeName, int timeoutMs)
    {
        try
        {
            // (the running GameDeck may still be starting up, checking for an update: give it a while to open the pipe)
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(timeoutMs);

            var bytes = Encoding.UTF8.GetBytes(launchTarget);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits (on a background thread) for other starts of GameDeck to hand over a game to start, and passes each one to
    /// <paramref name="launch"/> (called on that background thread: switch to the UI thread there).
    /// </summary>
    public static void Listen(Action<string> launch) => Listen(launch, PipeName);

    internal static void Listen(Action<string> launch, string pipeName)
    {
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();

                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var target = reader.ReadToEnd().Trim();
                    if (target.Length > 0)
                        launch(target);
                }
                catch (IOException)
                {
                    Thread.Sleep(500);   // the pipe couldn't be made (or the sender went away): try again
                }
                catch (UnauthorizedAccessException)
                {
                    return;   // another program owns that name: shortcuts then start GameDeck afresh only
                }
            }
        })
        {
            IsBackground = true,
            Name = "GameDeck shortcut listener"
        };
        listener.Start();
    }
}
