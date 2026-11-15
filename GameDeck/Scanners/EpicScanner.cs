using System.IO;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Scanners;

public class EpicScanner : IGameScanner
{
    private record Manifest(string Name, string AppName, string? MainApp, string Namespace, string Item,
                            string? Executable, string? InstallDir);

    public List<Game> Scan()
    {
        var games = new List<Game>();

        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifests))
            return games; // Epic launcher not installed

        var all = Directory.EnumerateFiles(manifests, "*.item")
            .Select(ReadManifest)
            .OfType<Manifest>()
            .ToList();

        var installedApps = all.Select(m => m.AppName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var m in all)
        {
            // DLC / add-ons point at a main game. Only skip them when that main game is installed too:
            // a manifest that merely has a different MainGameAppName can still be a real game.
            if (m.MainApp is not null &&
                !m.MainApp.Equals(m.AppName, StringComparison.OrdinalIgnoreCase) &&
                installedApps.Contains(m.MainApp))
                continue;

            if (string.IsNullOrEmpty(m.Executable)) continue; // not a playable game

            games.Add(new Game
            {
                Name = m.Name,
                Source = "Epic",
                // silent=true starts Epic's client hidden in the background (needed for its DRM/login)
                LaunchTarget = $"com.epicgames.launcher://apps/{m.Namespace}%3A{m.Item}%3A{m.AppName}?action=launch&silent=true",
                InstallDir = m.InstallDir
            });
        }

        return games;
    }

    private static Manifest? ReadManifest(string file)
    {
        try
        {
            // Epic's launcher may have the file open while it updates a game: allow reading it anyway
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var name = Str(root, "DisplayName");
            var appName = Str(root, "AppName");
            var ns = Str(root, "CatalogNamespace");
            var item = Str(root, "CatalogItemId");

            if (name is null || appName is null || ns is null || item is null) return null;

            return new Manifest(name, appName, Str(root, "MainGameAppName"), ns, item,
                                Str(root, "LaunchExecutable"), Str(root, "InstallLocation"));
        }
        catch
        {
            return null; // skip unreadable manifest
        }
    }

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
