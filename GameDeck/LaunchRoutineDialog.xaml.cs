using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;
using Microsoft.Win32;

namespace GameDeck.Views;

/// <summary>
/// "Launch routine": which programs to open and close around a game, and the power plan.
/// ShowDialog() returns true on Save; the routine is then in <see cref="Result"/> (null = nothing configured).
/// </summary>
public partial class LaunchRoutineDialog : Window
{
    public record AppEntry(string Path)
    {
        /// <summary>"Discord" — the file name without .exe.</summary>
        public string Display => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    /// <summary>One line of the screen list: a mode, or "Don't change" (null).</summary>
    public record DisplayChoice(DisplayMode? Mode, string? Note = null)
    {
        public string Display => (Mode?.Text ?? "Don't change") + (Note is null ? "" : "   " + Note);
    }

    private readonly Game _game;
    private readonly ObservableCollection<AppEntry> _startApps = [];
    private readonly ObservableCollection<AppEntry> _closeApps = [];

    public LaunchProfile? Result { get; private set; }

    public LaunchRoutineDialog(Game game)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        _game = game;
        TitleText.Text = $"Launch routine: {game.DisplayName}";

        StartList.ItemsSource = _startApps;
        CloseList.ItemsSource = _closeApps;

        var profile = game.Profile ?? new LaunchProfile();
        foreach (var path in profile.StartApps) _startApps.Add(new AppEntry(path));
        foreach (var path in profile.CloseApps) _closeApps.Add(new AppEntry(path));

        CloseStartedCheckBox.IsChecked = profile.CloseStartedAppsAfter;
        AskCheckBox.IsChecked = profile.AskBeforeClosing;
        ReopenCheckBox.IsChecked = profile.ReopenClosedAppsAfter;
        PowerCheckBox.IsChecked = profile.HighPerformancePower;
        FillDisplayList(profile.Display);

        // without a known folder GameDeck can't tell when the game ends, so the "after" steps could never run
        if (game.TrackingFolder is null)
        {
            Sections.IsEnabled = false;
            SaveButton.IsEnabled = false;
            HintText.Text = "GameDeck can't tell when this game ends, so launch routines aren't available for it.";
        }
    }

    // ---------- apps to open ----------

    private void AddStartApp_Click(object sender, RoutedEventArgs e)
    {
        var path = PickExe("Choose an app to open before the game");
        if (path is null) return;

        if (ProcessGuard.IsProtected(path))
        {
            MessageDialog.Show(this, "That's a system program or a game launcher, so it can't be added here.", "Can't add this app");
            return;
        }

        AddUnique(_startApps, path);
    }

    private void RemoveStartApp_Click(object sender, RoutedEventArgs e)
    {
        if (StartList.SelectedItem is AppEntry entry)
            _startApps.Remove(entry);
    }

    // ---------- apps to close ----------

    private void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RunningAppsDialog(_game) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.SelectedPaths)
            AddUnique(_closeApps, path);
    }

    private void AddCloseApp_Click(object sender, RoutedEventArgs e)
    {
        var path = PickExe("Choose an app to close before the game");
        if (path is null) return;

        if (ProcessGuard.IsProtected(path) || ProcessGuard.IsTheGame(_game, path))
        {
            MessageDialog.Show(this, "GameDeck won't close system programs, game launchers or the game itself.", "Can't add this app");
            return;
        }

        AddUnique(_closeApps, path);
    }

    private void RemoveCloseApp_Click(object sender, RoutedEventArgs e)
    {
        if (CloseList.SelectedItem is AppEntry entry)
            _closeApps.Remove(entry);
    }

    // ---------- screen ----------

    /// <summary>"Don't change", then every mode the main display offers (the one in use now marked), the saved one selected.</summary>
    private void FillDisplayList(DisplayMode? saved)
    {
        var (modes, current) = DisplayModes.ForChoosing();

        var choices = new List<DisplayChoice> { new(null) };
        choices.AddRange(modes.Select(m => new DisplayChoice(m, m == current ? "(now)" : null)));

        // a saved mode the display doesn't offer any more (another screen now): still listed, so saving keeps it unless changed
        if (saved is not null && !modes.Contains(saved))
            choices.Add(new DisplayChoice(saved, "(not offered by this screen now)"));

        DisplayList.ItemsSource = choices;
        DisplayList.SelectedItem = choices.FirstOrDefault(c => c.Mode == saved) ?? choices[0];
        DisplayList.ScrollIntoView(DisplayList.SelectedItem);

        if (modes.Count == 0)
            DisplayHint.Text = "Windows didn't list any modes for your main display, so this can't be used on this PC.";
    }

    // ---------- helpers ----------

    private string? PickExe(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "Programs (*.exe)|*.exe" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static void AddUnique(ObservableCollection<AppEntry> list, string path)
    {
        if (!list.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            list.Add(new AppEntry(path));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = new LaunchProfile
        {
            StartApps = _startApps.Select(a => a.Path).ToList(),
            CloseStartedAppsAfter = CloseStartedCheckBox.IsChecked == true,
            CloseApps = _closeApps.Select(a => a.Path).ToList(),
            AskBeforeClosing = AskCheckBox.IsChecked == true,
            ReopenClosedAppsAfter = ReopenCheckBox.IsChecked == true,
            HighPerformancePower = PowerCheckBox.IsChecked == true,
            Display = (DisplayList.SelectedItem as DisplayChoice)?.Mode
        };

        // closing apps without asking is allowed, but say what it means once
        if (profile.CloseApps.Count > 0 && !profile.AskBeforeClosing)
        {
            var save = MessageDialog.Ask(this, "Close apps without asking?",
                "With \"Ask me before closing anything\" turned off, the apps in the close list are asked to close every time " +
                "this game starts, without a confirmation.\n\nApps can still offer to save your work, but make sure that's what you want.",
                "Save anyway", "Go back");

            if (!save) return;
        }

        Result = profile.IsEmpty ? null : profile;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
