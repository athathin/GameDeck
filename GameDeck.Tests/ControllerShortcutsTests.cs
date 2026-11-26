using GameDeck.Services;

namespace GameDeck.Tests;

// Only the shortcut that runs a callback (the screenshot) is pressed here: the others press real keys (volume, Alt + Tab...).
// The tests share the shortcuts' state, so they don't run alongside each other.
[Collection("ControllerShortcuts")]
public class ControllerShortcutsTests
{
    private static HashSet<PadButton> Held(params PadButton[] buttons) => [.. buttons];

    private static int CountScreenshots(Action presses)
    {
        ControllerShortcuts.Reset();
        var taken = 0;
        ControllerShortcuts.TakeScreenshot = () => taken++;
        presses();
        ControllerShortcuts.TakeScreenshot = null;
        return taken;
    }

    [Fact]
    public void Home_pressed_and_let_go_on_its_own_is_a_plain_press()
    {
        ControllerShortcuts.Reset();

        Assert.False(ControllerShortcuts.Update(Held(PadButton.Home)));
        Assert.True(ControllerShortcuts.HomeHeld);
        Assert.True(ControllerShortcuts.Update(Held()));   // let go: Home's own job happens now
        Assert.False(ControllerShortcuts.HomeHeld);
    }

    [Fact]
    public void Home_and_RT_takes_one_screenshot_and_Home_is_then_no_plain_press()
    {
        var tapped = true;
        var taken = CountScreenshots(() =>
        {
            ControllerShortcuts.Update(Held(PadButton.Home));
            ControllerShortcuts.Update(Held(PadButton.Home, PadButton.RightTrigger));
            ControllerShortcuts.Update(Held(PadButton.Home, PadButton.RightTrigger));   // still held: not again
            ControllerShortcuts.Update(Held(PadButton.Home));
            tapped = ControllerShortcuts.Update(Held());
        });

        Assert.Equal(1, taken);
        Assert.False(tapped);
    }

    [Fact]
    public void Pressing_RT_twice_while_holding_Home_takes_two()
    {
        var taken = CountScreenshots(() =>
        {
            ControllerShortcuts.Update(Held(PadButton.Home));
            ControllerShortcuts.Update(Held(PadButton.Home, PadButton.RightTrigger));
            ControllerShortcuts.Update(Held(PadButton.Home));
            ControllerShortcuts.Update(Held(PadButton.Home, PadButton.RightTrigger));
            ControllerShortcuts.Update(Held());
        });

        Assert.Equal(2, taken);
    }

    [Fact]
    public void RT_already_held_before_Home_is_no_shortcut()
    {
        var tapped = false;
        var taken = CountScreenshots(() =>
        {
            ControllerShortcuts.Update(Held(PadButton.RightTrigger));
            ControllerShortcuts.Update(Held(PadButton.RightTrigger, PadButton.Home));
            tapped = ControllerShortcuts.Update(Held(PadButton.RightTrigger));   // Home let go
            ControllerShortcuts.Update(Held());
        });

        Assert.Equal(0, taken);
        Assert.True(tapped);   // no shortcut was used, so Home did its own job
    }

    [Fact]
    public void RT_without_Home_is_no_shortcut()
    {
        var taken = CountScreenshots(() =>
        {
            ControllerShortcuts.Update(Held(PadButton.RightTrigger));
            ControllerShortcuts.Update(Held());
        });

        Assert.Equal(0, taken);
    }

    [Fact]
    public void Home_with_a_button_that_has_no_shortcut_is_no_plain_press_and_the_button_is_swallowed()
    {
        ControllerShortcuts.Reset();

        ControllerShortcuts.Update(Held(PadButton.Home));
        ControllerShortcuts.Update(Held(PadButton.Home, PadButton.Confirm));   // A has no shortcut
        Assert.True(ControllerShortcuts.Swallows(PadButton.Confirm));          // (Big Picture doesn't get it either)

        Assert.False(ControllerShortcuts.Update(Held(PadButton.Confirm)));      // Home let go: no Power menu
        Assert.True(ControllerShortcuts.Swallows(PadButton.Confirm));          // still held: still not Big Picture's

        ControllerShortcuts.Update(Held());
        Assert.False(ControllerShortcuts.Swallows(PadButton.Confirm));         // let go: a normal button again
    }

    [Fact]
    public void A_button_pressed_without_Home_is_never_swallowed()
    {
        ControllerShortcuts.Reset();

        ControllerShortcuts.Update(Held(PadButton.Down));

        Assert.False(ControllerShortcuts.Swallows(PadButton.Down));
        Assert.True(ControllerShortcuts.Swallows(PadButton.Home));
    }
}
