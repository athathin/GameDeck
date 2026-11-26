using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>Lists the programs that have a window open right now, so the user can pick some to close before a game.</summary>
public partial class RunningAppsDialog : Window
{
    public record RunningApp(string Name, string Title, string Path);

    /// <summary>The .exe paths picked (after OK).</summary>
    public List<string> SelectedPaths { get; private set; } = [];

    public RunningAppsDialog(Game game)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        AppList.ItemsSource = FindApps(game);
    }

    private static List<RunningApp> FindApps(Game game)
    {
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;

                var path = PlaytimeTracker.GetImagePath(process.Id);
                if (path is null || ProcessGuard.IsProtected(path) || ProcessGuard.IsTheGame(game, path)) continue;

                apps.TryAdd(path, new RunningApp(Path.GetFileNameWithoutExtension(path), process.MainWindowTitle, path));
            }
            catch
            {
                // ended meanwhile / not readable: skip it
            }
            finally
            {
                process.Dispose();
            }
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedPaths = AppList.SelectedItems.OfType<RunningApp>().Select(a => a.Path).ToList();
        DialogResult = SelectedPaths.Count > 0;
    }

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem is not null)
            AddButton_Click(sender, e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
