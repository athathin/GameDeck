namespace GameDeck.Services;

/// <summary>
/// What happens when the game that is running closes: chosen during the game (Big Picture's Now Playing, the tray menu), for
/// that one game only. Sleep and shut down first count down for a minute, which can be cancelled.
/// </summary>
public enum AfterGame
{
    ReturnToGameDeck,
    Sleep,
    ShutDown
}

public static class AfterGameExtensions
{
    /// <summary>"Go back to GameDeck", "Put the PC to sleep", "Turn off the PC".</summary>
    public static string Label(this AfterGame choice) => choice switch
    {
        AfterGame.Sleep => "Put the PC to sleep",
        AfterGame.ShutDown => "Turn off the PC",
        _ => "Go back to " + AppInfo.DisplayName
    };

    /// <summary>The next choice (Go back → Sleep → Turn off → Go back).</summary>
    public static AfterGame Next(this AfterGame choice) => (AfterGame)(((int)choice + 1) % 3);

    /// <summary>"The PC goes to sleep in 45 seconds" / "The PC turns off in 45 seconds".</summary>
    public static string Countdown(this AfterGame choice, int seconds)
    {
        var time = seconds == 1 ? "1 second" : $"{seconds} seconds";
        return choice == AfterGame.ShutDown ? $"The PC turns off in {time}" : $"The PC goes to sleep in {time}";
    }

    /// <summary>"Sleep now" / "Turn off now".</summary>
    public static string NowText(this AfterGame choice) => choice == AfterGame.ShutDown ? "Turn off now" : "Sleep now";

    /// <summary>Does it: sleep, or turn the PC off (programs are asked to close normally, see SystemPower).</summary>
    public static void Run(this AfterGame choice)
    {
        if (choice == AfterGame.Sleep)
            SystemPower.Suspend();
        else if (choice == AfterGame.ShutDown)
            SystemPower.TurnOff();
    }
}
