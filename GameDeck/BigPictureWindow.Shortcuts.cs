using System.Windows;
using System.Windows.Controls;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the card with every controller shortcut (Power menu > Show Controller Shortcuts) ----------

    private void OpenShortcuts()
    {
        ShowOverlay(Overlay.Shortcuts);
        RenderShortcuts();
        ShortcutsScroll.ScrollToTop();
    }

    /// <summary>
    /// Fills the card: on the left the Home-button shortcuts (<see cref="ControllerShortcuts"/>) and opening Big Picture,
    /// on the right what each button does in here. Drawn with the buttons of the controller in use (an Xbox one when none is).
    /// </summary>
    private void RenderShortcuts()
    {
        var kind = _pad.ActiveKind == ControllerKind.None ? ControllerKind.Xbox : _pad.ActiveKind;
        var l = LabelsFor(kind);
        UIElement Home() => HomeIcon(l, onController: true, kind);
        UIElement Face(string label) => RoundButton(label, onController: true);
        var stickIn = kind == ControllerKind.PlayStation ? "L3" : "LS";

        ShortcutsLeft.Children.Clear();
        ShortcutsRight.Children.Clear();

        if (!SettingsService.ControllerShortcuts)
            ShortcutsLeft.Children.Add(new TextBlock
            {
                Text = "The Home-button shortcuts are switched off in Settings.", Foreground = Purple, FontSize = 16,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            });

        AddSection(ShortcutsLeft, "GENERAL",
            ShortcutRow("Volume Up", Home(), "+", Chip("L ↑")),
            ShortcutRow("Next Song", Home(), "+", Chip("L →")),
            ShortcutRow("Volume Down", Home(), "+", Chip("L ↓")),
            ShortcutRow("Previous Song", Home(), "+", Chip("L ←")),
            ShortcutRow("Play / Pause", Home(), "+", Chip(stickIn)));
        AddSection(ShortcutsLeft, "ACCESSIBILITY",
            ShortcutRow("Show Keyboard", Home(), "+", Chip(l.Select)));
        AddSection(ShortcutsLeft, "KEYS",
            ShortcutRow("Alt-Tab (keep holding Home, press again for the next window)", Home(), "+", Chip(l.Start)));
        AddSection(ShortcutsLeft, "SCREENSHOT",
            ShortcutRow("Take Screenshot", Home(), "+", Chip(l.RightTrigger)));
        AddSection(ShortcutsLeft, "OPEN BIG PICTURE  (while GameDeck is in the tray)",
            ShortcutRow("Open Big Picture", Home()),
            ShortcutRow("Open Big Picture", "hold", Chip(l.Select), "+", Chip(l.Start)));
        AddSection(ShortcutsLeft, "DURING A GAME STARTED FROM GAMEDECK",
            ShortcutRow("Now Playing: Resume or Close the game", Home()));

        AddSection(ShortcutsRight, "BIG PICTURE",
            ShortcutRow("Select", Face(l.Confirm)),
            ShortcutRow("Back", Face(l.Back)),
            ShortcutRow("Favorite", Face(l.X)),
            ShortcutRow("Search", Face(l.Y)),
            ShortcutRow("Previous / Next Tab", Chip(l.Left), "/", Chip(l.Right)),
            ShortcutRow("Random Game", Chip(l.RightTrigger)),
            ShortcutRow("Filters", Chip(l.Select)),
            ShortcutRow("Game Menu", Chip(l.Start)),
            ShortcutRow("Power Menu", Home()));
        AddSection(ShortcutsRight, "ON-SCREEN KEYBOARD",
            ShortcutRow("Delete", Face(l.X)),
            ShortcutRow("Space", Face(l.Y)),
            ShortcutRow("Shift / Caps", Chip(l.Left), "/", Chip(l.Right)),
            ShortcutRow("Move the Cursor", Chip(l.LeftTrigger), "/", Chip(l.RightTrigger)),
            ShortcutRow("Clear", Chip(l.Select)),
            ShortcutRow("Done", Chip(l.Start)));

        FillHints(ShortcutsHints, (Labels().Back, "Close"));
    }

    /// <summary>A heading in capitals, then its rows.</summary>
    private static void AddSection(Panel column, string title, params UIElement[] rows)
    {
        column.Children.Add(new TextBlock
        {
            Text = title, Foreground = Muted, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, column.Children.Count == 0 ? 0 : 24, 0, 4)
        });

        foreach (var row in rows)
            column.Children.Add(row);
    }

    /// <summary>One line: what it does on the left; on the right the buttons, with words in between ("+", "/", "hold").</summary>
    private static UIElement ShortcutRow(string action, params object[] buttons)
    {
        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };

        foreach (var part in buttons)
        {
            keys.Children.Add(part is UIElement element
                ? element
                : new TextBlock
                {
                    Text = (string)part, Foreground = TextGrey, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0)
                });
        }

        var line = new DockPanel();
        DockPanel.SetDock(keys, Dock.Right);
        line.Children.Add(keys);
        line.Children.Add(new TextBlock
        {
            Text = action, Foreground = TextGrey, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            BorderBrush = Divider, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 0, 9), MinHeight = 54,
            Child = line
        };
    }

    private void HandleShortcuts(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: ShortcutsScroll.ScrollToVerticalOffset(ShortcutsScroll.VerticalOffset - 90); break;
            case PadButton.Down: ShortcutsScroll.ScrollToVerticalOffset(ShortcutsScroll.VerticalOffset + 90); break;
            case PadButton.Back or PadButton.Confirm or PadButton.Home: CloseOverlay(); break;
        }
    }
}
