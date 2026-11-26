using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- power menu ----------

    // true while the "exit Big Picture?" question (from the Back button) is showing instead of the Power menu
    private bool _exitQuestion;

    private void AskToExit()
    {
        _powerQuestion = null;
        _exitQuestion = true;
        _powerConfirming = new PowerEntry("Exit Big Picture Mode", CloseScreen, "Exit Big Picture mode?", "Exit");
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    private void OpenPower()
    {
        _powerQuestion = null;
        _exitQuestion = false;
        _powerConfirming = null;
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    /// <summary>
    /// The menu, in groups like Steam's: the running game (Resume / Close) when there is one, the controller's shortcuts, the PC,
    /// GameDeck, and Cancel. Dangerous ones ask again first.
    /// </summary>
    private List<List<PowerEntry>> PowerMenu()
    {
        var groups = PowerMenuGroups();

        if (NowPlayingEntries() is { Count: > 0 } game)
            groups.Insert(0, game);

        return groups;
    }

    private List<List<PowerEntry>> PowerMenuGroups() =>
    [
        [
            new PowerEntry("Show Controller Shortcuts", OpenShortcuts)
        ],
        [
            new PowerEntry("Suspend System", () => { CloseOverlay(); SystemPower.Suspend(); }),
            new PowerEntry("Turn Off System", SystemPower.TurnOff, "Turn off this PC?", "Turn off now"),
            new PowerEntry("Restart System", SystemPower.Restart, "Restart this PC?", "Restart now")
        ],
        [
            new PowerEntry("Minimize GameDeck", () => CloseThen(_minimizeApp)),
            new PowerEntry("Exit Big Picture Mode", CloseScreen),
            new PowerEntry("Exit GameDeck", () => CloseThen(_exitApp), "Exit GameDeck?", "Exit GameDeck")
        ],
        [
            new PowerEntry("Cancel", CloseOverlay)
        ]
    ];

    // a question with its own answers, shown in the Power menu's card (e.g. "the game is still running": Resume / Close anyway)
    private (string Title, List<List<(string Text, Action Run)>> Groups)? _powerQuestion;

    /// <summary>Asks something in the Power menu's card, which works with the controller (a Windows dialog doesn't). Back = the last answer.</summary>
    private void AskInPowerCard(string title, List<(string Text, Action Run)> answers) =>
        AskInPowerCard(title, answers.Select(a => new List<(string Text, Action Run)> { a }).ToList());

    /// <summary>The same with the answers in groups (a line between the groups). Empty groups are left out.</summary>
    private void AskInPowerCard(string title, List<List<(string Text, Action Run)>> groups)
    {
        _exitQuestion = false;
        _powerConfirming = null;
        _powerQuestion = (title, groups.Where(g => g.Count > 0).ToList());
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    private void RenderPower()
    {
        PowerList.Children.Clear();

        if (_powerQuestion is { } question)
        {
            PowerTitle.Text = question.Title;
            _powerGroups = question.Groups
                .Select(g => g.Select(a => (a.Text, (Action)(() => { _powerQuestion = null; CloseOverlay(); a.Run(); }))).ToList())
                .ToList();
        }
        else if (_powerConfirming is { } entry)
        {
            PowerTitle.Text = entry.ConfirmTitle ?? "Are you sure?";
            _powerGroups =
            [
                [(entry.ConfirmButton ?? "Yes", () => { CloseOverlay(); entry.Run(); })],
                [("Cancel", () =>
                {
                    if (_exitQuestion) { CloseOverlay(); return; }   // asked from Back: there is no menu to go back to

                    _powerConfirming = null;
                    _powerIndex = 0;
                    RenderPower();
                })]
            ];
        }
        else
        {
            PowerTitle.Text = "Power";
            _powerGroups = PowerMenu()
                .Select(group => group.Select(e => (e.Text, Run: e.ConfirmTitle is null
                    ? e.Run
                    : () => { _powerConfirming = e; _powerIndex = 0; RenderPower(); })).ToList())
                .ToList();
        }

        var index = 0;
        for (var g = 0; g < _powerGroups.Count; g++)
        {
            if (g > 0)
                PowerList.Children.Add(new Border { Height = 2, Background = Divider });

            foreach (var (text, _) in _powerGroups[g])
            {
                var selected = index == _powerIndex;
                var itemIndex = index;

                var cell = new Border
                {
                    Background = selected ? ThemeService.SelectBg : Brushes.Transparent,
                    Padding = new Thickness(24, 15, 24, 15),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = text, FontSize = 19, Foreground = selected ? Dark : TextGrey }
                };

                cell.MouseEnter += (_, _) =>
                {
                    if (!PointerIsChoosing || _powerIndex == itemIndex) return;

                    _powerIndex = itemIndex;   // the mouse selects what it points at, like the stick does
                    RenderPower();
                };

                cell.MouseLeftButtonUp += (_, _) =>
                {
                    _powerIndex = itemIndex;
                    ActivatePower();
                };

                PowerList.Children.Add(cell);
                index++;
            }
        }
    }

    private void HandlePower(PadButton button)
    {
        var count = _powerGroups.Sum(g => g.Count);

        switch (button)
        {
            case PadButton.Up:
                _powerIndex = Math.Max(0, _powerIndex - 1);
                RenderPower();
                break;

            case PadButton.Down:
                _powerIndex = Math.Min(count - 1, _powerIndex + 1);
                RenderPower();
                break;

            case PadButton.Confirm:
                ActivatePower();
                break;

            case PadButton.Back or PadButton.Home:   // Home opens the menu, so it closes it again too
                if (_powerQuestion is { } question)
                {
                    ActivateAnswer(question.Groups.Sum(g => g.Count) - 1);   // the last answer is the "leave it" one
                }
                else if (_powerConfirming is not null && !_exitQuestion)
                {
                    _powerConfirming = null;   // back from "are you sure?" to the menu
                    _powerIndex = 0;
                    RenderPower();
                }
                else
                {
                    CloseOverlay();
                }
                break;
        }
    }

    private void ActivatePower() => _powerGroups.SelectMany(g => g).ElementAt(_powerIndex).Run();

    private void ActivateAnswer(int index)
    {
        _powerIndex = index;
        ActivatePower();
    }
}
