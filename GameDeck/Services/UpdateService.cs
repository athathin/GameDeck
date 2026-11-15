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
    public record UpdateInfo(Version Version, string Url, long Size, string? Sha256);

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

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

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
#if DEBUG
            return false;
#else
            try
            {
                // (started through "dotnet GameDeck.dll" the running program is dotnet.exe, which must never be touched)
                var exe = Environment.ProcessPath;
                if (exe is null || !string.Equals(Path.GetFileName(exe), "GameDeck.exe", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Asks GitHub for the latest release. Null when this version is the newest, or when it can't be checked (offline...).</summary>
    public static async Task<UpdateInfo?> CheckAsync(TimeSpan timeout)
    {
        try
        {
            using var cancel = new CancellationTokenSource(timeout);

            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancel.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel.Token), cancellationToken: cancel.Token);
            var root = json.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest) || Normalize(latest) <= CurrentVersion) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;

                var url = asset.GetProperty("browser_download_url").GetString();
                if (url is null || !url.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase))
                    return null;   // only ever download from this project's own releases

                string? sha = null;
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } text &&
                    text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = text["sha256:".Length..];

                return new UpdateInfo(Normalize(latest), url, asset.GetProperty("size").GetInt64(), sha);
            }
        }
        catch
        {
            // no internet, GitHub busy, an unexpected answer: just start the version that is installed
        }

        return null;
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

            return true;
        }
        catch
        {
            try { File.Delete(download); } catch { }
            return false;
        }
    }

    /// <summary>Starts the (new) program again with the same arguments. The caller must have released the single-instance lock.</summary>
    public static void Restart(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        Process.Start(info);
    }

    /// <summary>Removes the previous version's file that an update left behind (it can still be in use right after the swap).</summary>
    public static void CleanUpOldVersion()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            foreach (var leftover in new[] { exe + ".old", exe + ".new" })
            {
                if (File.Exists(leftover))
                    File.Delete(leftover);
            }
        }
        catch
        {
            // still in use: it is removed the next time GameDeck starts
        }
    }

    /// <summary>1.2 and 1.2.0.0 are the same version: always compare with four numbers.</summary>
    private static Version Normalize(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
