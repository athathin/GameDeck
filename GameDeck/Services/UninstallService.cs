using System.Diagnostics;
using System.IO;
using System.Text;

namespace GameDeck.Services;

/// <summary>
/// "Uninstall GameDeck": GameDeck has no installer, so this is how it removes itself completely.
/// Always removed:
///   - the "launch when Windows starts" entry in the registry
///   - %TEMP%\.net\GameDeck        (where the single-file .exe unpacks its native libraries)
///   - the GameDeck .exe itself (and its folder, if nothing else is in it)
/// Removed unless the user chose to keep their data:
///   - %AppData%\GameDeck          (settings, library, user data, downloaded and custom covers)
/// The files that are in use while GameDeck runs (the .exe, the unpacked libraries) and the settings GameDeck
/// writes while closing are removed by a small script that waits for GameDeck to exit first.
/// </summary>
public static class UninstallService
{
    /// <param name="keepData">true = leave %AppData%\GameDeck (library, settings, covers...) in place.</param>
    public static void Uninstall(bool keepData)
    {
        StartupService.SetEnabled(false);   // the registry entry can go right away

        var exe = Environment.ProcessPath;
        var exeFolder = exe is null ? null : Path.GetDirectoryName(exe);

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName);
        var tempNet = Path.Combine(Path.GetTempPath(), ".net");
        var extracted = Path.Combine(tempNet, "GameDeck");

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("set tries=0");

        // wait until GameDeck has really exited: only then can its .exe be deleted
        if (exe is not null)
        {
            script.AppendLine(":waitexe");
            script.AppendLine($"del /f /q \"{exe}\" >nul 2>&1");
            script.AppendLine($"if exist \"{exe}\" (");
            script.AppendLine("  set /a tries+=1");
            script.AppendLine("  if %tries% lss 60 (");
            script.AppendLine("    ping -n 2 127.0.0.1 >nul");
            script.AppendLine("    goto waitexe");
            script.AppendLine("  )");
            script.AppendLine(")");
        }

        if (!keepData)
            script.AppendLine($"rmdir /s /q \"{appData}\" >nul 2>&1");
        script.AppendLine($"rmdir /s /q \"{extracted}\" >nul 2>&1");
        script.AppendLine($"rmdir \"{tempNet}\" >nul 2>&1");                      // only succeeds when empty
        if (exeFolder is not null)
            script.AppendLine($"rmdir \"{exeFolder}\" >nul 2>&1");                // only succeeds when empty
        script.AppendLine("(goto) 2>nul & del /f /q \"%~f0\"");                    // the script deletes itself last

        var scriptFile = Path.Combine(Path.GetTempPath(), $"gamedeck-uninstall-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptFile, script.ToString(), Encoding.Default);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptFile}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
