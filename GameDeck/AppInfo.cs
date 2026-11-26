namespace GameDeck;

/// <summary>
/// Which GameDeck this is: the normal one, or the Beta (built with <c>-p:GameDeckBeta=true</c>, see beta.ps1).
/// The Beta is a separate program: its own name, its own data folder, its own "one at a time" lock, and it never updates
/// itself, so it can run next to the normal GameDeck without touching its settings or library.
/// </summary>
public static class AppInfo
{
#if BETA
    public const bool IsBeta = true;
    public const string DisplayName = "GameDeck Beta";
    public const string DataFolderName = "GameDeck Beta";   // %AppData%\GameDeck Beta
    public const string InstanceSuffix = "-Beta";
    public const string IconUri = "pack://application:,,,/Assets/beta-icon.ico";
#else
    public const bool IsBeta = false;
    public const string DisplayName = "GameDeck";
    public const string DataFolderName = "GameDeck";        // %AppData%\GameDeck
    public const string InstanceSuffix = "";
    public const string IconUri = "pack://application:,,,/Assets/icon.ico";
#endif

    /// <summary>
    /// True while the normal GameDeck is running (the Beta looks for its "one at a time" lock). The Beta then leaves the controller's
    /// Home button to it; when the normal GameDeck is not running, the Beta answers the button itself.
    /// </summary>
    public static bool NormalGameDeckRunning
    {
        get
        {
#if BETA
            // asked many times a second by the controller listener: look again only every couple of seconds
            var now = Environment.TickCount64;
            if (now - _checkedAt < 2000) return _normalRunning;

            _checkedAt = now;
            return _normalRunning = LookForNormalGameDeck();
#else
            return false;   // (the normal GameDeck is the one running)
#endif
        }
    }

#if BETA
    private static long _checkedAt = long.MinValue / 2;
    private static bool _normalRunning;

    private static bool LookForNormalGameDeck()
    {
        try
        {
            using var lockHandle = System.Threading.Mutex.OpenExisting(@"Local\GameDeck-SingleInstance");
            return true;
        }
        catch (System.Threading.WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;   // it exists, we just may not open it
        }
    }
#endif
}
