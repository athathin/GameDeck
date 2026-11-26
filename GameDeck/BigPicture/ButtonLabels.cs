using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// The names Big Picture shows for the buttons, as printed on the controller in use ("A", "✕", "LB", "L1"...), or the keyboard's
/// keys when no controller is used. Confirm and Back are swapped when the "Confirm with B" setting is on.
/// </summary>
internal sealed record ButtonLabels(string Confirm, string Back, string X, string Y, string Left, string Right,
                                    string LeftTrigger, string RightTrigger, string Select, string Start, string Home)
{
    // built once: the labels are asked for every time a hint bar, a tab row or a panel is drawn
    private static readonly ButtonLabels PlayStation = new("✕", "○", "□", "△", "L1", "R1", "L2", "R2", "Share", "Options", "PS");
    private static readonly ButtonLabels Nintendo = new("A", "B", "X", "Y", "L", "R", "ZL", "ZR", "−", "+", "Home");
    private static readonly ButtonLabels Xbox = new("A", "B", "X", "Y", "LB", "RB", "LT", "RT", "View", "Menu", "Xbox");

    // no controller: the keys of the main screen (PgUp / PgDn move the text cursor in the search box, see Window_PreviewKeyDown)
    private static readonly ButtonLabels Keyboard = new("Enter", "Esc", "F", "S", "PgUp", "PgDn", "PgUp", "PgDn", "Tab", "M", "P");

    // the same with the confirm and back buttons swapped (the keyboard is never swapped)
    private static readonly ButtonLabels PlayStationSwapped = PlayStation with { Confirm = PlayStation.Back, Back = PlayStation.Confirm };
    private static readonly ButtonLabels NintendoSwapped = Nintendo with { Confirm = Nintendo.Back, Back = Nintendo.Confirm };
    private static readonly ButtonLabels XboxSwapped = Xbox with { Confirm = Xbox.Back, Back = Xbox.Confirm };

    /// <summary>The button names of a controller family (the keyboard's keys for None), with confirm and back swapped if set.</summary>
    public static ButtonLabels For(ControllerKind kind)
    {
        var swap = SettingsService.SwapConfirmBack;

        return kind switch
        {
            ControllerKind.PlayStation => swap ? PlayStationSwapped : PlayStation,
            ControllerKind.Nintendo => swap ? NintendoSwapped : Nintendo,
            ControllerKind.Xbox or ControllerKind.Generic => swap ? XboxSwapped : Xbox,
            _ => Keyboard
        };
    }
}
