using System.Text.Json.Serialization;

namespace GameDeck.Models;

/// <summary>
/// A game's "launch routine": what GameDeck does around starting and ending it. Saved per game in userdata.json.
/// Only runs when the game is started from GameDeck.
/// </summary>
public class LaunchProfile
{
    /// <summary>Programs (.exe paths) to start before the game.</summary>
    public List<string> StartApps { get; set; } = [];

    /// <summary>When the game ends, ask the programs GameDeck started to close.</summary>
    public bool CloseStartedAppsAfter { get; set; } = true;

    /// <summary>Programs (.exe paths) to ask to close before the game.</summary>
    public List<string> CloseApps { get; set; } = [];

    /// <summary>Show what is about to be closed and let the user say yes / no / cancel.</summary>
    public bool AskBeforeClosing { get; set; } = true;

    /// <summary>When the game ends, start the programs that were closed again.</summary>
    public bool ReopenClosedAppsAfter { get; set; } = true;

    /// <summary>Switch Windows to the High performance power plan while the game runs.</summary>
    public bool HighPerformancePower { get; set; }

    [JsonIgnore]
    public bool IsEmpty => StartApps.Count == 0 && CloseApps.Count == 0 && !HighPerformancePower;

    public LaunchProfile Clone() => new()
    {
        StartApps = [.. StartApps],
        CloseStartedAppsAfter = CloseStartedAppsAfter,
        CloseApps = [.. CloseApps],
        AskBeforeClosing = AskBeforeClosing,
        ReopenClosedAppsAfter = ReopenClosedAppsAfter,
        HighPerformancePower = HighPerformancePower
    };
}
