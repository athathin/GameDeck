using System.IO;
using System.Text;
using System.Xml.Linq;
using GameDeck.Models;
using Microsoft.Win32;

namespace GameDeck.Scanners;

/// <summary>
/// Finds games installed by the Xbox app / Microsoft Store (including Game Pass). The Xbox app puts each game in
/// "&lt;drive&gt;:\XboxGames\&lt;Game&gt;\Content" together with a MicrosoftGame.config describing it.
/// Games are started through their app id (shell:AppsFolder\...), the same way the Start menu does it.
/// </summary>
public class XboxScanner : IGameScanner
{
    private const string PackagesKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public List<Game> Scan()
    {
        var games = new List<Game>();
        var packages = ReadInstalledPackageNames();

        foreach (var root in FindLibraryRoots())
        {
            foreach (var gameFolder in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var content = Path.Combine(gameFolder, "Content");
                    var configFile = Path.Combine(content, "MicrosoftGame.config");
                    if (!File.Exists(configFile)) continue;   // e.g. the "GameSave" folder

                    var config = XDocument.Load(configFile).Root;
                    var identity = config?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
                    var packageName = identity?.Attribute("Name")?.Value;
                    if (string.IsNullOrWhiteSpace(packageName)) continue;

                    var publisherId = packages
                        .Where(p => p.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase))
                        .Select(PublisherId)
                        .FirstOrDefault(id => id is not null);
                    if (publisherId is null) continue;        // not installed for this user any more

                    var appId = config!.Descendants()
                                    .Where(e => e.Name.LocalName == "Executable")
                                    .Select(e => e.Attribute("Id")?.Value)
                                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? "Game";

                    games.Add(new Game
                    {
                        Name = DisplayName(config, Path.GetFileName(gameFolder)),
                        Source = "Xbox",
                        LaunchTarget = $@"shell:AppsFolder\{packageName}_{publisherId}!{appId}",
                        InstallDir = content
                    });
                }
                catch
                {
                    // unreadable or broken config: skip this game
                }
            }
        }

        return games;
    }

    /// <summary>Every "XboxGames" folder: the default one on each drive plus whatever the drive's .GamingRoot file says.</summary>
    private static IEnumerable<string> FindLibraryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Fixed and removable: the Xbox app can install to external / USB drives, which Windows may report as removable
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames"));

            try
            {
                // .GamingRoot: "RGBX", 4 bytes of version, then the folder name as UTF-16 (e.g. "XboxGames")
                var file = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (!File.Exists(file)) continue;

                var bytes = File.ReadAllBytes(file);
                if (bytes.Length <= 8) continue;

                var folder = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).Trim('\0', '\\', '/', ' ');
                if (folder.Length > 0)
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, folder));
            }
            catch
            {
                // keep the default folder
            }
        }

        return roots.Where(Directory.Exists);
    }

    /// <summary>Names of the packages registered for the current user, e.g. "Microsoft.Limitless_1.2.3.0_x64__8wekyb3d8bbwe".</summary>
    private static List<string> ReadInstalledPackageNames()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PackagesKey);
        return key?.GetSubKeyNames().ToList() ?? [];
    }

    /// <summary>
    /// "Name_1.2.3.0_x64__8wekyb3d8bbwe" -> "8wekyb3d8bbwe". The package family name (what shell:AppsFolder needs)
    /// is the package Name + "_" + this publisher id. Packages with a resource id in that spot have no "__": null.
    /// </summary>
    private static string? PublisherId(string fullName)
    {
        var i = fullName.LastIndexOf("__", StringComparison.Ordinal);
        return i > 0 ? fullName[(i + 2)..] : null;
    }

    private static string DisplayName(XElement config, string folderName)
    {
        // Often "ms-resource:..." (a reference to a localized string we can't read), then the folder name is the game's name
        var name = config.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")
            ?.Attribute("DefaultDisplayName")?.Value;

        return string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? folderName
            : name;
    }
}
