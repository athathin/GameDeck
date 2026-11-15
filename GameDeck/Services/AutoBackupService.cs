using System.IO;
using System.Text.Json;

namespace GameDeck.Services;

/// <summary>
/// Keeps the last few daily copies of the user's data files (favorites and playtime, play sessions, library, settings) in
/// %AppData%\GameDeck\backups. If one of those files ever turns out to be unreadable, the newest copy is used instead of
/// starting empty. The copies stay on this PC.
/// </summary>
public static class AutoBackupService
{
    private const int KeepDays = 7;
    private const string DoneMarker = ".complete";

    private static readonly string[] Files = ["userdata.json", "sessions.json", "library.json", "settings.json"];

    private static string DataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck");

    private static string BackupFolder => Path.Combine(DataFolder, "backups");

    /// <summary>For tests: use another folder instead of the real %AppData%\GameDeck.</summary>
    internal static void UseFolder(string folder) => DataFolder = folder;

    /// <summary>Makes today's copy if there isn't one yet, and removes the oldest ones. Never throws.</summary>
    public static void RunIfDue()
    {
        try
        {
            var today = Path.Combine(BackupFolder, DateTime.Now.ToString("yyyy-MM-dd"));
            if (File.Exists(Path.Combine(today, DoneMarker))) return;   // today's copy was finished

            Directory.CreateDirectory(today);

            var complete = true;

            foreach (var name in Files)
            {
                var source = Path.Combine(DataFolder, name);
                if (!File.Exists(source)) continue;

                try
                {
                    // only a copy that can be read back is worth keeping
                    var text = File.ReadAllText(source);
                    using var _ = JsonDocument.Parse(text);
                    File.WriteAllText(Path.Combine(today, name), text);
                }
                catch
                {
                    complete = false;   // unreadable right now (being written, or damaged): not copied, tried again later
                }
            }

            // only a finished copy counts as today's: one that was cut short, or missed a file, is made again next time
            if (complete)
                File.WriteAllText(Path.Combine(today, DoneMarker), "");

            foreach (var old in Directory.EnumerateDirectories(BackupFolder).OrderByDescending(d => d).Skip(KeepDays))
            {
                try { Directory.Delete(old, recursive: true); } catch { }
            }
        }
        catch
        {
            // a backup that can't be made must never bother the user
        }
    }

    /// <summary>
    /// Reads a JSON data file. When it can't be read (damaged), the file is set aside (kept as *.corrupt-...) and the newest
    /// backup that can be read is used instead. Returns null when the file doesn't exist or nothing can be recovered.
    /// </summary>
    public static T? LoadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            try { File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true); }
            catch { }

            return FromBackup<T>(Path.GetFileName(path));
        }
    }

    private static T? FromBackup<T>(string fileName) where T : class
    {
        try
        {
            if (!Directory.Exists(BackupFolder)) return null;

            foreach (var day in Directory.EnumerateDirectories(BackupFolder).OrderByDescending(d => d))
            {
                var file = Path.Combine(day, fileName);
                if (!File.Exists(file)) continue;

                try
                {
                    var restored = JsonSerializer.Deserialize<T>(File.ReadAllText(file));
                    if (restored is not null) return restored;
                }
                catch
                {
                    // this day's copy is damaged too: try the one before
                }
            }
        }
        catch
        {
            // nothing to recover from
        }

        return null;
    }
}
