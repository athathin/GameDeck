using System.IO;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- a game's launch routine, with the controller (Game menu › Launch routine) ----------
    // The same choices as the desktop's Launch routine window, as rows of the game menu. Every change is saved at once.

    /// <summary>Changes a copy of the game's routine and saves it (an empty one is removed), then shows the rows again.</summary>
    private void ChangeRoutine(Game game, Action<LaunchProfile> change)
    {
        var profile = game.Profile?.Clone() ?? new LaunchProfile();
        change(profile);
        _gameActions.SaveRoutine(game, profile);   // (the game's Profile is replaced by the saved one)

        if (_overlay == Overlay.GameMenu)
            RenderGameMenu();
    }

    private List<FilterRow> RoutineRows(Game game)
    {
        // without a known folder GameDeck can't tell when the game ends, so the "after" steps could never run
        if (game.TrackingFolder is null)
            return [new FilterRow("GameDeck can't tell when this game ends, so launch routines aren't available for it.")];

        var profile = game.Profile ?? new LaunchProfile();
        var rows = new List<FilterRow> { new("Steps GameDeck runs when you start this game from GameDeck", IsHeader: true) };

        // apps to open
        rows.Add(new FilterRow("OPEN THESE APPS BEFORE THE GAME", IsHeader: true));
        foreach (var path in profile.StartApps)
        {
            var app = path;
            rows.Add(new FilterRow($"{Path.GetFileNameWithoutExtension(app)}   (remove)",
                Activate: () => ChangeRoutine(game, p => p.StartApps.Remove(app))));
        }

        rows.Add(new FilterRow("Add an app…", Activate: () => ChooseRoutineApp(game, close: false)));
        if (profile.StartApps.Count > 0)   // (the switches below mean something once there is an app)
            rows.Add(new FilterRow("Close them when the game ends", IsOn: () => game.Profile?.CloseStartedAppsAfter ?? true,
                Activate: () => ChangeRoutine(game, p => p.CloseStartedAppsAfter = !p.CloseStartedAppsAfter)));

        // apps to close
        rows.Add(new FilterRow("CLOSE THESE APPS BEFORE THE GAME", IsHeader: true));
        foreach (var path in profile.CloseApps)
        {
            var app = path;
            rows.Add(new FilterRow($"{Path.GetFileNameWithoutExtension(app)}   (remove)",
                Activate: () => ChangeRoutine(game, p => p.CloseApps.Remove(app))));
        }

        rows.Add(new FilterRow("Add a running app  ›", Activate: () => OpenSubmenu("Close before the game", () => RunningAppRows(game))));
        rows.Add(new FilterRow("Add an app…", Activate: () => ChooseRoutineApp(game, close: true)));
        if (profile.CloseApps.Count > 0)
        {
            rows.Add(new FilterRow("Ask me before closing anything", IsOn: () => game.Profile?.AskBeforeClosing ?? true,
                Activate: () => ChangeRoutine(game, p => p.AskBeforeClosing = !p.AskBeforeClosing)));
            rows.Add(new FilterRow("Reopen them when the game ends", IsOn: () => game.Profile?.ReopenClosedAppsAfter ?? true,
                Activate: () => ChangeRoutine(game, p => p.ReopenClosedAppsAfter = !p.ReopenClosedAppsAfter)));
        }

        // power and screen
        rows.Add(new FilterRow("WHILE PLAYING", IsHeader: true));
        rows.Add(new FilterRow("High performance power plan", IsOn: () => game.Profile?.HighPerformancePower == true,
            Activate: () => ChangeRoutine(game, p => p.HighPerformancePower = !p.HighPerformancePower)));
        rows.Add(new FilterRow($"Screen:  {profile.Display?.Text ?? "don't change"}  ›",
            Activate: () => OpenSubmenu("Screen while playing", () => DisplayRows(game))));

        return rows;
    }

    /// <summary>The programs with a window open now; choosing one adds it to the apps closed before the game.</summary>
    private List<FilterRow> RunningAppRows(Game game)
    {
        var chosen = (game.Profile?.CloseApps ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apps = RunningAppsDialog.FindApps(game).Where(a => !chosen.Contains(a.Path)).ToList();

        if (apps.Count == 0)
            return [new FilterRow("No other apps with a window are open right now.")];

        return apps.Select(a => new FilterRow($"{a.Name}   ·   {a.Title}", Activate: () =>
        {
            ChangeRoutine(game, p => p.CloseApps.Add(a.Path));
            _menuPages.Pop();   // back to the routine, which lists it now
            RenderGameMenu();
        })).ToList();
    }

    /// <summary>"Don't change", then every mode of the main display (the one in use now marked), the chosen one ticked.</summary>
    private List<FilterRow> DisplayRows(Game game)
    {
        var (modes, current) = DisplayModes.ForChoosing();

        void Choose(DisplayMode? mode)
        {
            ChangeRoutine(game, p => p.Display = mode);
            _menuPages.Pop();
            RenderGameMenu();
        }

        var rows = new List<FilterRow>
        {
            new("Your normal settings come back when the game ends", IsHeader: true),
            new("Don't change", IsOn: () => game.Profile?.Display is null, IsRadio: true, Activate: () => Choose(null))
        };

        foreach (var mode in modes)
        {
            var chosen = mode;
            rows.Add(new FilterRow(chosen.Text + (chosen == current ? "   (now)" : ""), IsOn: () => game.Profile?.Display == chosen, IsRadio: true,
                Activate: () => Choose(chosen)));
        }

        if (modes.Count == 0)
            rows.Add(new FilterRow("Windows didn't list any modes for your main display."));

        return rows;
    }

    /// <summary>The controller's file browser, choosing a program to open (or close) before the game; then back to the routine.</summary>
    private void ChooseRoutineApp(Game game, bool close)
    {
        var pages = MenuSnapshot();   // (back to this same row of the routine afterwards)
        CloseGameMenu();

        OpenProgramBrowse(new ProgramChoice(
            close ? $"Close before {game.DisplayName}: choose the app" : $"Open before {game.DisplayName}: choose the app",
            file =>
            {
                if (ProcessGuard.IsProtected(file))
                    return "That's a system program or a game launcher, so it can't be added.";
                if (close && ProcessGuard.IsTheGame(game, file))
                    return "That's the game itself.";

                ChangeRoutine(game, p =>
                {
                    var list = close ? p.CloseApps : p.StartApps;
                    if (!list.Contains(file, StringComparer.OrdinalIgnoreCase))
                        list.Add(file);
                });
                return null;
            },
            Done: () => ReturnToMenu(pages)));
    }
}
