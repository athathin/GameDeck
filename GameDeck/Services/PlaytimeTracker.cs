using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Counts how long a game runs. After a launch it looks for the game's process for a short while
/// (every 3 seconds, at most 5 minutes). Once found, it simply waits for Windows to say the process
/// has ended, so there is no polling while you play. Games behind anti-cheat hide where they run from and refuse that wait:
/// they are recognised by their programs' names (or, on Steam, by Steam's own "running" flag) and checked every 10 seconds.
/// </summary>
public class PlaytimeTracker(UserDataService userData, SessionLog sessions)
{
    /// <summary>Raised when a launched game is over (or was never found running): the launch routine's "after" steps run now.</summary>
    public event Action<Game>? Ended;

    /// <summary>
    /// Raised only when the game was really seen running and has now been closed (not when it was never found), with how long
    /// the session lasted.
    /// </summary>
    public event Action<Game, TimeSpan>? Closed;

    private readonly HashSet<string> _watching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Game Game, DateTime Start)> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a game started from GameDeck is being waited for or is running.</summary>
    public bool IsTracking => _watching.Count > 0;

    /// <summary>The game from GameDeck that is running now (the one started last when there are several), or null.</summary>
    public Game? RunningGame => _active.Count == 0 ? null : _active.Values.MaxBy(a => a.Start).Game;

    /// <summary>When the running game (<see cref="RunningGame"/>) was seen starting (UTC), or null.</summary>
    public DateTime? RunningSinceUtc => _active.Count == 0 ? null : _active.Values.MaxBy(a => a.Start).Start;

    /// <summary>Start tracking right after launching a game (call from the UI thread).</summary>
    public void Track(Game game) => _ = TrackAsync(game);

    /// <summary>Saves the time played so far for games still running (call when GameDeck closes).</summary>
    public void FlushAll()
    {
        foreach (var (game, start) in _active.Values.ToList())
            Finish(game, start, DateTime.UtcNow - start);

        _active.Clear();
    }

    /// <summary>Adds the time to the game's total and remembers it as a dated session.</summary>
    private void Finish(Game game, DateTime startUtc, TimeSpan length)
    {
        userData.AddPlaytime(game, length);
        sessions.Add(game, startUtc, length);
    }

    private async Task TrackAsync(Game game)
    {
        var folder = game.TrackingFolder;
        var key = game.LaunchTarget;
        if (folder is null || !_watching.Add(key)) return;

        var wasRunning = false;
        var length = TimeSpan.Zero;

        try
        {
            var target = await Task.Run(() => new Target(game, folder));

            // 1) wait for the game's process to appear (launchers can take a while to start it). For a Steam game whose
            //    program can't be recognised (anti-cheat hides where it runs from), Steam's own "running" flag counts too.
            Process? process = null;
            var steamRunning = false;
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (process is null && !steamRunning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                process = await Task.Run(() => FindGameProcess(target, 0));
                steamRunning = process is null && target.SteamSaysRunning();
            }

            if (process is null && !steamRunning) return; // never started (or couldn't be recognised): nothing to count

            wasRunning = true;
            var start = DateTime.UtcNow;
            var end = start;
            _active[key] = (game, start);

            // 2) wait until it closes: Windows tells us when it ends (or, when it won't, we look every few seconds)
            while (process is not null || steamRunning)
            {
                if (process is not null)
                {
                    await WaitForExitAsync(process);
                    process.Dispose();
                }
                else
                {
                    while (await Task.Run(target.SteamSaysRunning))
                        await Task.Delay(TimeSpan.FromSeconds(10));
                }

                end = DateTime.UtcNow;

                // some games hand over to a second program (launcher -> game), so look once more
                await Task.Delay(TimeSpan.FromSeconds(5));
                process = await Task.Run(() => FindGameProcess(target, 100L * 1024 * 1024));
                steamRunning = process is null && target.SteamSaysRunning();
            }

            length = end - start;
            if (_active.Remove(key))
                Finish(game, start, length);
        }
        catch
        {
            // Windows refused access to the process, etc.: save what we have counted so far
            if (_active.Remove(key, out var session))
            {
                length = DateTime.UtcNow - session.Start;
                Finish(game, session.Start, length);
            }
        }
        finally
        {
            _watching.Remove(key);
            Ended?.Invoke(game);

            if (wasRunning)
                Closed?.Invoke(game, length);
        }
    }

    /// <summary>
    /// Waits until the program ends. Anti-cheat often refuses the handle Windows needs to tell us; then we check every
    /// 10 seconds whether it still exists (that only needs the list of running programs).
    /// </summary>
    private static async Task WaitForExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            return;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }

        var id = process.Id;
        var name = process.ProcessName;
        while (await Task.Run(() => IsRunning(id, name)))
            await Task.Delay(TimeSpan.FromSeconds(10));
    }

    private static bool IsRunning(int id, string name)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // gone
        }
    }

    /// <summary>What a launched game looks like while it runs: its folders, its programs' names and its Steam app id.</summary>
    private sealed class Target
    {
        /// <summary>The Minecraft Launcher (Xbox) runs the Java game from its own app-data folder, not from its install folder.</summary>
        private const string MinecraftLauncherPackage = "Microsoft.4297127D64EC6_8wekyb3d8bbwe";

        public List<string> Prefixes { get; } = [];
        public HashSet<string> ExeNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _steamAppId;

        public Target(Game game, string folder)
        {
            var folders = new List<string> { folder };
            if (game.LaunchTarget.Contains(MinecraftLauncherPackage, StringComparison.OrdinalIgnoreCase))
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "Packages", MinecraftLauncherPackage, "LocalCache", "Local", "runtime"));

            foreach (var f in folders)
            {
                Prefixes.Add(f.TrimEnd('\\') + "\\");
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    foreach (var exe in Directory.EnumerateFiles(f, "*.exe", options))
                    {
                        var name = Path.GetFileNameWithoutExtension(exe);
                        if (IsGameExeName(name))
                            ExeNames.Add(name);
                    }
                }
                catch
                {
                    // folder missing or unreadable: only the path check works for it
                }
            }

            _steamAppId = game.SteamAppId;
        }

        /// <summary>True when the program's path is in the game's folders, or (path hidden) its name is one of the game's programs.</summary>
        public bool Owns(Process process, string? path) =>
            path is not null
                ? Prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                : ExeNames.Contains(process.ProcessName);

        /// <summary>Steam's own flag (HKCU\Software\Valve\Steam\Apps\{id}\Running), trusted only while Steam itself is running.</summary>
        public bool SteamSaysRunning()
        {
            if (_steamAppId is null) return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Valve\Steam\Apps\{_steamAppId}");
                if (key?.GetValue("Running") is not int running || running == 0) return false;

                var steam = Process.GetProcessesByName("steam");
                foreach (var p in steam) p.Dispose();
                return steam.Length > 0; // a flag left behind by a Steam crash doesn't count
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Helper programs that ship with many games (crash reporters, uninstallers, installers) can't tell games apart by name.</summary>
    internal static bool IsGameExeName(string name) =>
        !(name.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
          name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
          name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
          name.Contains("redist", StringComparison.OrdinalIgnoreCase));

    /// <summary>The biggest (by memory) running program that belongs to the game (see <see cref="Target.Owns"/>).</summary>
    private static Process? FindGameProcess(Target target, long minWorkingSetBytes)
    {
        Process? best = null;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = GetImagePath(process.Id);
                if (target.Owns(process, path) &&
                    process.WorkingSet64 >= minWorkingSetBytes &&
                    (best is null || process.WorkingSet64 > best.WorkingSet64))
                {
                    best?.Dispose();
                    best = process;
                    continue;
                }
            }
            catch
            {
                // process ended or can't be read: skip it
            }

            process.Dispose();
        }

        return best;
    }

    /// <summary>The .exe path of a running process, or null if Windows won't tell.</summary>
    internal static string? GetImagePath(int processId)
    {
        const uint QueryLimitedInformation = 0x1000; // works even for programs running as administrator

        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
