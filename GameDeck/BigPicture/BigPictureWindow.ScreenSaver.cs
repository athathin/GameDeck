using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- when the screen saver (ScreenSaverView) starts and stops: after a while without input, and on any input ----------

    private long _lastInputAt = Environment.TickCount64;

    /// <summary>A button, a key or the mouse was used: the time without input starts again.</summary>
    private void NoteInput() => _lastInputAt = Environment.TickCount64;

    /// <summary>Input arrived: notes it, and ends the screen saver if it is on.</summary>
    /// <returns>True when the input only woke the screen (it must do nothing else: the user couldn't see what it would do).</returns>
    private bool WakeFromScreenSaver()
    {
        NoteInput();
        if (!ScreenSaver.IsOn) return false;

        ScreenSaver.Stop();
        return true;
    }

    /// <summary>Called every second: starts the screen saver once the screen has been left alone long enough.</summary>
    private void CheckScreenSaver()
    {
        var minutes = SettingsService.ScreenSaverMinutes;
        if (ScreenSaver.IsOn || _closing || minutes == 0 || Environment.TickCount64 - _lastInputAt < minutes * 60_000L) return;

        // the covers on this PC (looked for once: the screen saver may run for hours)
        ScreenSaver.Start(_library.Where(g => !g.IsHidden).Select(CoverService.FindCachedCover).OfType<string>().ToList());
    }
}
