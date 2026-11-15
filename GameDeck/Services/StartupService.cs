using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>
/// "Launch GameDeck when Windows starts": one value in the current user's Run key (no admin rights, no service,
/// no scheduled task). GameDeck then starts with --startup, which keeps its window hidden in the system tray.
/// </summary>
public static class StartupService
{
    public const string StartupArgument = "--startup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppInfo.DisplayName;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Turns it on or off. If Windows refuses (policy), nothing changes: check <see cref="IsEnabled"/> afterwards.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled && Environment.ProcessPath is { } path)
                key.SetValue(ValueName, $"\"{path}\" {StartupArgument}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // blocked by the system: the caller re-reads IsEnabled
        }
    }

    /// <summary>If the option is on, re-writes the entry so it follows the .exe when it has been moved or updated.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled)
            SetEnabled(true);
    }
}
