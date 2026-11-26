using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Counts how long a game runs. After a launch it looks for the game's process for a short while
/// (every 3 seconds, at most 5 minutes). Once found, it simply waits for Windows to say the process
/// has ended, so there is no polling while you play.
/// </summary>
public class PlaytimeTracker(UserDataService userData, SessionLog sessions)
{
    /// <summary>Raised when a launched game is over (or was never found running): the launch routine's "after" steps run now.</summary>
    public event Action<Game>? Ended;

    /// <summary>Raised only when the game was really seen running and has now been closed (not when it was never found).</summary>
    public event Action<Game>? Closed;

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

        try
        {
            // 1) wait for the game's process to appear (launchers can take a while to start it)
            Process? process = null;
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (process is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                process = await Task.Run(() => FindGameProcess(folder, 0));
            }

            if (process is null) return; // never started (or couldn't be recognised): nothing to count

            wasRunning = true;
            var start = DateTime.UtcNow;
            var end = start;
            _active[key] = (game, start);

            // 2) wait until it closes: no polling, Windows tells us when it ends
            while (process is not null)
            {
                await process.WaitForExitAsync();
                end = DateTime.UtcNow;
                process.Dispose();

                // some games hand over to a second program (launcher -> game), so look once more
                await Task.Delay(TimeSpan.FromSeconds(5));
                process = await Task.Run(() => FindGameProcess(folder, 100L * 1024 * 1024));
            }

            if (_active.Remove(key))
                Finish(game, start, end - start);
        }
        catch
        {
            // Windows refused access to the process, etc.: save what we have counted so far
            if (_active.Remove(key, out var session))
                Finish(game, session.Start, DateTime.UtcNow - session.Start);
        }
        finally
        {
            _watching.Remove(key);
            Ended?.Invoke(game);

            if (wasRunning)
                Closed?.Invoke(game);
        }
    }

    /// <summary>The biggest (by memory) running program that lives inside the game's folder.</summary>
    private static Process? FindGameProcess(string folder, long minWorkingSetBytes)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        Process? best = null;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = GetImagePath(process.Id);
                if (path is not null &&
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
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
