using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameDeck.Services;

/// <summary>
/// Keeps GameDeck up to date from its GitHub releases, the way Steam does: when GameDeck starts it looks for a newer
/// release, downloads it, swaps it in for the running program and starts again as the new version.
/// It only runs once per start (never while you are playing or using the app), and only reaches github.com.
/// </summary>
public static class UpdateService
{
    private const string Repo = "athathin/GameDeck";

    /// <summary>A newer version that can be installed.</summary>
    public record UpdateInfo(Version Version, string Url, long Size, string? Sha256, string? Notes = null);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameDeck-Updater");   // GitHub requires some User-Agent
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The version of the running program, e.g. 1.0.0.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentVersionText => VersionText(CurrentVersion);

    /// <summary>True when a release tag such as "v1.0.6" names a newer version than <paramref name="current"/>.</summary>
    internal static bool IsNewer(string? tag, Version current) => TryGetNewer(tag, current, out _);

    /// <summary>Reads a release tag (such as "v1.0.6") once, and says whether it is newer than <paramref name="current"/>.</summary>
    private static bool TryGetNewer(string? tag, Version current, out Version latest)
    {
        if (Version.TryParse(tag?.TrimStart('v', 'V'), out var parsed))
        {
            latest = Normalize(parsed);
            return latest > Normalize(current);
        }

        latest = new Version(0, 0, 0, 0);
        return false;
    }

    /// <summary>The release file that fits this PC: the 64-bit or the 32-bit program.</summary>
    private static string AssetName => Environment.Is64BitProcess ? "GameDeck-win-x64.exe" : "GameDeck-win-x86.exe";

    /// <summary>
    /// Only a published (Release) build that sits in a folder the user can write to updates itself. A build started from
    /// Visual Studio must never replace its own files.
    /// </summary>
    public static bool CanUpdate
    {
        get
        {
#if DEBUG || BETA
            return false;
#else
            try
            {
                // The program may carry any name (the release files are called GameDeck-win-x64.exe and so on), but when it
                // is started through "dotnet GameDeck.dll" the running program is dotnet.exe, which must never be touched.
                var exe = Environment.ProcessPath;
                if (exe is null || string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                    return false;

                // renaming the file is how the swap works, so check that the folder allows it
                var probe = Path.Combine(Path.GetDirectoryName(exe)!, ".update-test-" + Environment.ProcessId);
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
#endif
        }
    }

    /// <summary>The answer of a check: a newer version, or none (<see cref="Failed"/> says whether GitHub couldn't be reached).</summary>
    public record CheckResult(UpdateInfo? Update, bool Failed);

    /// <summary>Asks GitHub for the latest release. Null when this version is the newest, or when it can't be checked (offline...).</summary>
    public static async Task<UpdateInfo?> CheckAsync(TimeSpan timeout) => (await CheckDetailedAsync(timeout)).Update;

    /// <summary>The same check, but telling "this is the newest version" apart from "couldn't check" (for the Settings button).</summary>
    public static async Task<CheckResult> CheckDetailedAsync(TimeSpan timeout)
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout);

            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancel.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new CheckResult(null, false);   // no release yet
            if (!response.IsSuccessStatusCode) return new CheckResult(null, true);

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel.Token), cancellationToken: cancel.Token);
            var root = json.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return new CheckResult(null, false);
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return new CheckResult(null, false);

            if (!TryGetNewer(root.GetProperty("tag_name").GetString(), CurrentVersion, out var latest))
                return new CheckResult(null, false);

            // a version that did not start and was rolled back is not offered again (until a newer one is released)
            if (LoadState().SkipVersion == VersionText(latest)) return new CheckResult(null, false);

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            if (notes is { Length: > 1500 }) notes = notes[..1500] + "…";

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;

                var url = asset.GetProperty("browser_download_url").GetString();
                if (url is null || !url.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase))
                    return new CheckResult(null, true);   // only ever download from this project's own releases

                string? sha = null;
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } text &&
                    text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = text["sha256:".Length..];

                return new CheckResult(new UpdateInfo(latest, url, asset.GetProperty("size").GetInt64(), sha, notes), false);
            }

            return new CheckResult(null, true);   // a newer release without a file for this PC
        }
        catch
        {
            // no internet, GitHub busy, an unexpected answer: just start the version that is installed
            return new CheckResult(null, true);
        }
    }

    /// <summary>
    /// Downloads the new version and puts it where the running program is (the running file is renamed out of the way,
    /// which Windows allows). Returns false when anything went wrong; then nothing has been changed.
    /// </summary>
    public static async Task<bool> InstallAsync(UpdateInfo update, IProgress<double>? progress)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        var download = exe + ".new";
        var backup = exe + ".old";

        try
        {
            using (var response = await Http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(download);

                var buffer = new byte[128 * 1024];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;

                    if (update.Size > 0)
                        progress?.Report((double)done / update.Size);
                }
            }

            // a damaged or cut-off download must never replace a working program
            if (new FileInfo(download).Length != update.Size) throw new IOException("The download is incomplete.");

            if (update.Sha256 is not null)
            {
                await using var file = File.OpenRead(download);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The download doesn't match its checksum.");
            }

            if (File.Exists(backup))
                File.Delete(backup);

            File.Move(exe, backup);

            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(backup, exe);   // put the working program back
                throw;
            }

            // the new version must show that it starts (MarkHealthy); until then the old file is kept to go back to
            SaveState(new UpdateState { PendingVersion = VersionText(update.Version), Notes = update.Notes });

            return true;
        }
        catch
        {
            try { File.Delete(download); } catch { }
            return false;
        }
    }

    /// <summary>The start-up argument that makes the new program wait until the old one has closed (it still holds the single-instance lock).</summary>
    public const string WaitForExitArgument = "--wait-for-exit=";

    /// <summary>
    /// Starts the (new) program again with the same arguments. When <paramref name="waitForThisProgram"/> is true the new one waits
    /// for this one to close first (use it when this program is closed right after, not before).
    /// </summary>
    public static void Restart(IEnumerable<string> args, bool waitForThisProgram = false)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };

        foreach (var arg in args.Where(a => !a.StartsWith(WaitForExitArgument, StringComparison.OrdinalIgnoreCase)))
            info.ArgumentList.Add(arg);

        if (waitForThisProgram)
            info.ArgumentList.Add(WaitForExitArgument + Environment.ProcessId);

        Process.Start(info);
    }

    /// <summary>
    /// Removes what an update leaves behind, so only the new version's files remain: the previous program file, a download
    /// that never finished, and the unpacked copies of the earlier versions' built-in libraries. Runs in the background.
    /// </summary>
    public static void CleanUpOldVersion()
    {
        _ = Task.Run(async () =>
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            // Right after an update the previous version is still closing and its file is locked for a moment: try again
            // for a while (a locked file that is still there at the end is removed at the next start).
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var pending = false;

                var unproven = LoadState().PendingVersion == CurrentVersionText;   // keep the file to roll back to

                foreach (var leftover in unproven ? new[] { exe + ".new", exe + ".bad" } : [exe + ".old", exe + ".new", exe + ".bad"])
                {
                    try
                    {
                        if (File.Exists(leftover))
                            File.Delete(leftover);
                    }
                    catch
                    {
                        pending = true;
                    }
                }

                if (!pending) break;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            RemoveOldUnpackedLibraries();
        });
    }

    /// <summary>
    /// A single-file program unpacks its native libraries into a folder of its own in %TEMP%\.net\&lt;name&gt;\&lt;id&gt;, a new
    /// one for every version. The folders of the versions that were replaced are no longer needed.
    /// </summary>
    private static void RemoveOldUnpackedLibraries()
    {
        try
        {
            var (appFolder, current) = UnpackedFolders();
            if (appFolder is null) return;

            foreach (var folder in Directory.EnumerateDirectories(appFolder))
            {
                if (folder.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;

                try { Directory.Delete(folder, recursive: true); }
                catch { /* in use: left for the next start */ }
            }
        }
        catch
        {
            // not a single-file run, or no access: nothing to clean
        }
    }

    /// <summary>
    /// Where this single-file program unpacked its native libraries: the app's folder %TEMP%\.net\&lt;name&gt; (named after
    /// the .exe, so it changes when the file is renamed) and the version's folder &lt;id&gt; in it. Both null when nothing was
    /// unpacked (not a single-file run).
    /// </summary>
    internal static (string? AppFolder, string? Current) UnpackedFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), ".net") + Path.DirectorySeparatorChar;

        // the folder this version runs from: wherever one of its own loaded libraries lives
        string? current = null;
        using (var process = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (module.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    current = Path.GetDirectoryName(module.FileName);
                    break;
                }
            }
        }

        // only ever this program's own folder: %TEMP%\.net\<name>\<id>, one level below the app's name
        var appFolder = current is null ? null : Path.GetDirectoryName(current);
        if (appFolder is null || !Path.GetDirectoryName(appFolder)!.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return (null, null);

        return (appFolder, current);
    }

    // ---------- a version that doesn't start is rolled back ----------

    private sealed class UpdateState
    {
        public string? PendingVersion { get; set; }   // an installed version that has not shown yet that it starts
        public int Starts { get; set; }                // how many times it was started without showing it
        public bool Crashed { get; set; }              // it hit an error that was written to the crash log
        public string? NotesFor { get; set; }          // the version the saved notes belong to (kept until they have been shown)
        public string? SkipVersion { get; set; }       // a version that was rolled back: not offered again
        public string? Notes { get; set; }             // what's new in the pending version
    }

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "update-state.json");

    private static UpdateState LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath)) ?? new UpdateState()
                : new UpdateState();
        }
        catch
        {
            return new UpdateState();
        }
    }

    private static void SaveState(UpdateState state)
    {
        try { AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(state)); }
        catch { }
    }

    /// <summary>1.0.6, or 1.0.6.1 when there is a fourth number.</summary>
    internal static string VersionText(Version v) =>
        v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>
    /// Called first thing at start-up. Right after an update the new version has to prove itself: when it was started before
    /// and never got as far as <see cref="MarkHealthy"/>, it is broken (crashing at start) and the previous version comes back.
    /// Returns true when that has been done and the previous version is ready to start: the caller starts it and closes this one.
    /// </summary>
    public static bool RollBackIfBroken()
    {
        try
        {
            var state = LoadState();
            if (state.PendingVersion != CurrentVersionText) return false;

            var exe = Environment.ProcessPath;
            var previous = exe + ".old";

            // Broken = it was started before, never got to show it works, and either wrote a crash log or has now failed three
            // times in a row. (Being closed from Task Manager, or a power cut, in the first seconds must not count as broken.)
            var broken = state.Starts >= 1 && (state.Crashed || state.Starts >= 3);

            if (!broken || exe is null || !File.Exists(previous))
            {
                state.Starts++;
                SaveState(state);
                return false;
            }

            // swap the old file back in
            var bad = exe + ".bad";
            if (File.Exists(bad)) File.Delete(bad);

            File.Move(exe, bad);
            try
            {
                File.Move(previous, exe);
            }
            catch
            {
                File.Move(bad, exe);
                return false;
            }

            SaveState(new UpdateState { SkipVersion = CurrentVersionText });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The running version hit an error: if it is a version that was just installed, remember that it crashed.</summary>
    public static void NoteCrash()
    {
        try
        {
            var state = LoadState();
            if (state.PendingVersion != CurrentVersionText || state.Crashed) return;

            state.Crashed = true;
            SaveState(state);
        }
        catch
        {
            // the crash log is what matters
        }
    }

    /// <summary>
    /// The new version is running fine (it stayed up a while, or was closed normally): the update is final and the previous
    /// version's file is not needed any more. What's new in it stays saved until it has been shown (<see cref="TakeWhatsNew"/>).
    /// </summary>
    public static void MarkHealthy(bool cleanUp = true)
    {
        try
        {
            var state = LoadState();
            if (state.PendingVersion != CurrentVersionText) return;

            SaveState(new UpdateState { SkipVersion = state.SkipVersion, Notes = state.Notes, NotesFor = CurrentVersionText });

            if (cleanUp)
                CleanUpOldVersion();
        }
        catch
        {
            // nothing to do: it is tried again at the next start
        }
    }

    /// <summary>The release notes of the version that was just installed, once. Null when there are none (or they were shown).</summary>
    public static (string Version, string Notes)? TakeWhatsNew()
    {
        try
        {
            var state = LoadState();
            if (state.NotesFor != CurrentVersionText || string.IsNullOrWhiteSpace(state.Notes)) return null;

            var notes = state.Notes.Trim();
            SaveState(new UpdateState { SkipVersion = state.SkipVersion });
            return (CurrentVersionText, notes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>1.2 and 1.2.0.0 are the same version: always compare with four numbers.</summary>
    private static Version Normalize(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
