using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Runs a game's launch routine: asks chosen programs to close, starts others, switches the power plan, and undoes
/// what it can when the game ends. Safety rules:
///  - programs are only ever asked to close the normal way (they can offer to save); nothing is force-killed;
///  - system programs, game launchers and GameDeck are never closed (<see cref="ProcessGuard"/>);
///  - only programs GameDeck started itself are closed again afterwards;
///  - the previous power plan is written to disk first, so it is restored even after a crash.
/// </summary>
public class LaunchRoutineService
{
    private const string HighPerformanceScheme = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(8);

    private static readonly string PendingPowerFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck", "pending-power-plan.txt");

    /// <summary>What a running routine has to undo when its game ends.</summary>
    private class Session
    {
        public List<Process> Started { get; } = [];     // programs GameDeck started
        public List<string> Closed { get; } = [];       // programs GameDeck closed (paths), to reopen
        public bool CloseStartedAfter { get; init; }
        public bool ReopenAfter { get; init; }
        public bool UsesHighPerformance { get; set; }
    }

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _highPerformanceUsers = new(StringComparer.OrdinalIgnoreCase);
    private string? _previousPlan;

    // ---------- before the game ----------

    /// <summary>
    /// What <see cref="PrepareAsync"/> did. <see cref="Proceed"/> is false when the user cancelled (the game must not start).
    /// <see cref="Summary"/> says what was done, e.g. "Closed 2 apps · High performance power"; null when nothing was done.
    /// </summary>
    public record PrepareResult(bool Proceed, string? Summary = null);

    /// <summary>
    /// Runs the "before" steps.
    /// <paramref name="confirmClose"/> is asked (on the UI thread) with the names of the programs about to be closed.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(Game game, Func<Game, IReadOnlyList<string>, MessageBoxResult> confirmClose)
    {
        var profile = game.Profile;

        // no routine, or no way to tell when the game ends (so the "after" steps could never run): do nothing
        if (profile is null || profile.IsEmpty || game.TrackingFolder is null) return new PrepareResult(true);
        if (_sessions.ContainsKey(game.LaunchTarget)) return new PrepareResult(true);   // already running: it was prepared at its start

        var session = new Session { CloseStartedAfter = profile.CloseStartedAppsAfter, ReopenAfter = profile.ReopenClosedAppsAfter };

        // 1) close programs
        if (profile.CloseApps.Count > 0)
        {
            var running = await Task.Run(() => FindRunning(profile.CloseApps, game));
            if (running.Count > 0)
            {
                var close = true;

                if (profile.AskBeforeClosing)
                {
                    var names = running.Select(r => Path.GetFileNameWithoutExtension(r.Path)).ToList();
                    switch (confirmClose(game, names))
                    {
                        case MessageBoxResult.Cancel:
                            foreach (var group in running) group.Processes.ForEach(p => p.Dispose());
                            return new PrepareResult(false);
                        case MessageBoxResult.No:
                            close = false;
                            break;
                    }
                }

                foreach (var (path, processes) in running)
                {
                    if (close && await CloseGracefullyAsync(processes))
                        session.Closed.Add(path);

                    processes.ForEach(p => p.Dispose());
                }
            }
        }

        // 2) start programs
        var startedAny = false;
        foreach (var path in profile.StartApps)
        {
            if (!File.Exists(path) || ProcessGuard.IsProtected(path)) continue;
            if ((await Task.Run(() => FindRunning([path], game))).Count > 0) continue;   // already open: not ours to manage

            try
            {
                var process = Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? ""
                });

                if (process is not null)
                    session.Started.Add(process);

                startedAny = true;
            }
            catch
            {
                // cancelled UAC prompt, broken file...: the game still starts
            }
        }

        if (startedAny)
            await Task.Delay(1500);   // give helper programs a moment to come up before the game starts

        // 3) power plan
        if (profile.HighPerformancePower)
            session.UsesHighPerformance = await EnterHighPerformanceAsync(game.LaunchTarget);

        _sessions[game.LaunchTarget] = session;

        // only claim what really happened
        var done = new List<string>();
        if (session.Closed.Count > 0)
            done.Add(session.Closed.Count == 1 ? "Closed 1 app" : $"Closed {session.Closed.Count} apps");
        if (session.Started.Count > 0)
            done.Add(session.Started.Count == 1 ? "Opened 1 app" : $"Opened {session.Started.Count} apps");
        if (session.UsesHighPerformance)
            done.Add("High performance power");

        return new PrepareResult(true, done.Count > 0 ? string.Join(" · ", done) : null);
    }

    // ---------- after the game ----------

    /// <summary>Runs the "after" steps (also used when the game failed to start). Safe to call for any game.</summary>
    public async Task FinishAsync(Game game)
    {
        if (!_sessions.Remove(game.LaunchTarget, out var session)) return;

        try
        {
            if (session.CloseStartedAfter)
                await CloseGracefullyAsync(session.Started.Where(p => !HasExited(p)).ToList());

            session.Started.ForEach(p => p.Dispose());

            if (session.ReopenAfter)
            {
                foreach (var path in session.Closed.Where(File.Exists))
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); }
                    catch { }
                }
            }
        }
        finally
        {
            if (session.UsesHighPerformance)
                await LeaveHighPerformanceAsync(game.LaunchTarget);
        }
    }

    // ---------- programs ----------

    /// <summary>The running programs that match the paths, minus anything protected or belonging to the game. Call from a background thread.</summary>
    private static List<(string Path, List<Process> Processes)> FindRunning(IEnumerable<string> paths, Game game)
    {
        var wanted = paths
            .Where(p => !ProcessGuard.IsProtected(p) && !ProcessGuard.IsTheGame(game, p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];

        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                var path = PlaytimeTracker.GetImagePath(process.Id);
                if (path is not null && wanted.Contains(path))
                {
                    if (!found.TryGetValue(path, out var list))
                        found[path] = list = [];

                    list.Add(process);
                    keep = true;
                }
            }
            catch
            {
                // ended meanwhile / not readable: skip it
            }

            if (!keep)
                process.Dispose();
        }

        return found.Select(f => (f.Key, f.Value)).ToList();
    }

    /// <summary>
    /// Asks every process to close like clicking the window's X (the program may ask to save first) and waits a few
    /// seconds. Never force-kills. True if everything has closed.
    /// </summary>
    private static async Task<bool> CloseGracefullyAsync(List<Process> processes)
    {
        foreach (var process in processes)
        {
            try { process.CloseMainWindow(); }
            catch { }
        }

        var deadline = DateTime.UtcNow + CloseWait;
        while (DateTime.UtcNow < deadline && processes.Any(p => !HasExited(p)))
            await Task.Delay(250);

        return processes.All(HasExited);
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return false; }   // can't tell (access denied): treat as still running, so it's never reopened twice
    }

    // ---------- power plan ----------

    /// <summary>Restores the power plan if GameDeck stopped (crash, closed while playing) before it could. Call at start-up.</summary>
    public static void RestorePendingPowerPlan()
    {
        try
        {
            if (!File.Exists(PendingPowerFile)) return;

            var guid = File.ReadAllText(PendingPowerFile).Trim();
            if (IsGuid(guid))
                SetPowerPlan(guid);

            File.Delete(PendingPowerFile);
        }
        catch
        {
            // nothing more to do
        }
    }

    private async Task<bool> EnterHighPerformanceAsync(string key)
    {
        if (_highPerformanceUsers.Count > 0)
        {
            _highPerformanceUsers.Add(key);   // another game already switched it: just share it
            return true;
        }

        var current = await Task.Run(GetActivePowerPlan);
        if (current is null || current.Equals(HighPerformanceScheme, StringComparison.OrdinalIgnoreCase))
            return false;   // unknown, or already on High performance: nothing to change or restore

        if (!await Task.Run(() => PowerPlanExists(HighPerformanceScheme)))
            return false;   // some PCs don't offer it

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPowerFile)!);
            File.WriteAllText(PendingPowerFile, current);   // written first: restored at next start-up even after a crash
        }
        catch
        {
            return false;   // can't promise to restore it, so don't change it
        }

        if (!await Task.Run(() => SetPowerPlan(HighPerformanceScheme)))
        {
            try { File.Delete(PendingPowerFile); } catch { }
            return false;
        }

        _previousPlan = current;
        _highPerformanceUsers.Add(key);
        return true;
    }

    private async Task LeaveHighPerformanceAsync(string key)
    {
        _highPerformanceUsers.Remove(key);
        if (_highPerformanceUsers.Count > 0 || _previousPlan is null) return;   // another game still needs it

        var previous = _previousPlan;
        _previousPlan = null;

        await Task.Run(() => SetPowerPlan(previous));
        try { File.Delete(PendingPowerFile); } catch { }
    }

    private static bool IsGuid(string text) => Regex.IsMatch(text, @"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$");

    private static string? GetActivePowerPlan()
    {
        var output = RunPowerCfg("/getactivescheme");
        var match = output is null ? null : Regex.Match(output, @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        return match is { Success: true } ? match.Value : null;
    }

    private static bool PowerPlanExists(string guid) =>
        RunPowerCfg("/list")?.Contains(guid, StringComparison.OrdinalIgnoreCase) == true;

    private static bool SetPowerPlan(string guid) => IsGuid(guid) && RunPowerCfg($"/setactive {guid}") is not null;

    /// <summary>Runs powercfg without a window. Returns its output, or null if it failed.</summary>
    private static string? RunPowerCfg(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
