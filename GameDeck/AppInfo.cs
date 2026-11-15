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
#else
    public const bool IsBeta = false;
    public const string DisplayName = "GameDeck";
    public const string DataFolderName = "GameDeck";        // %AppData%\GameDeck
    public const string InstanceSuffix = "";
#endif
}
