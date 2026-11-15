using System.IO;

namespace GameDeck.Services;

public static class ExeFinder
{
    // Executables that are almost never the game itself
    private static readonly string[] Ignore =
    [
        "unins", "crash", "report", "redist", "vcredist", "dxsetup", "setup",
        "installer", "updater", "helper", "easyanticheat", "battleye", "cleanup"
    ];

    /// <summary>Looks up to 2 folders deep and returns the largest plausible .exe (usually the game).</summary>
    public static string? FindMainExe(string directory)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 2,
                IgnoreInaccessible = true
            };

            return Directory.EnumerateFiles(directory, "*.exe", options)
                .Where(f => !Ignore.Any(i => Path.GetFileName(f).Contains(i, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
