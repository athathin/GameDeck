using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GameDeck.Services;
using GameDeck.Views;

namespace GameDeck
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // One GameDeck at a time. The names are per Windows session ("Local\"), and the Debug build and the Beta use their own
        // pairs, so pressing F5 in Visual Studio works while the published app is running.
#if DEBUG
        private const string InstanceName = @"Local\GameDeck-SingleInstance-Debug" + AppInfo.InstanceSuffix;
        private const string ShowEventName = @"Local\GameDeck-ShowWindow-Debug" + AppInfo.InstanceSuffix;
#else
        private const string InstanceName = @"Local\GameDeck-SingleInstance" + AppInfo.InstanceSuffix;
        private const string ShowEventName = @"Local\GameDeck-ShowWindow" + AppInfo.InstanceSuffix;
#endif

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            MenuAim.Register();   // right-click menus: a submenu stays open while the pointer heads for it

            // whatever goes wrong is written to a file, and the user gets a way to report it (see CrashLog)
            DispatcherUnhandledException += OnUiThreadError;
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            {
                CrashLog.Write(a.ExceptionObject as Exception, a.IsTerminating ? "Unhandled error (closing)" : "Unhandled error");
                UpdateService.NoteCrash();
            };
            TaskScheduler.UnobservedTaskException += (_, a) =>
            {
                CrashLog.Write(a.Exception, "Background task");
                a.SetObserved();
            };

            // the colors first: every window, the update window included, reads them
            ThemeService.Initialize(this, SettingsService.Theme, SettingsService.AccentColor);

            // Started by an update from inside GameDeck: the old program is still closing and holds the lock, so wait for it
            var args = e.Args;
            var waitFor = args.FirstOrDefault(x => x.StartsWith(UpdateService.WaitForExitArgument, StringComparison.OrdinalIgnoreCase));
            if (waitFor is not null)
            {
                args = args.Where(x => x != waitFor).ToArray();

                try
                {
                    if (int.TryParse(waitFor[UpdateService.WaitForExitArgument.Length..], out var pid))
                    {
                        using var old = System.Diagnostics.Process.GetProcessById(pid);
                        old.WaitForExit(20000);
                    }
                }
                catch
                {
                    // it has already gone
                }
            }

            // Already running (maybe hidden in the system tray)? Ask that one to come to the front and quit.
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                // A game's desktop shortcut: the running GameDeck starts the game (and stays where it is)
                if (ShortcutService.LaunchTargetFrom(args) is { } target)
                {
                    AllowSetForegroundWindow(-1);   // (ASFW_ANY) the game may come to the front: the user started this
                    ShortcutService.SendToRunningInstance(target);
                }
                // Started by Windows at sign-in while GameDeck is already running: nothing to show.
                else if (!args.Contains(StartupService.StartupArgument))
                    AskRunningInstanceToShow();

                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }

            // Right after an update the new version has to prove it starts: if it didn't (it crashed the first time), the
            // previous version comes back and the broken one is not offered again.
            if (UpdateService.RollBackIfBroken())
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;

                try { UpdateService.Restart(args); } catch { /* the user starts it again */ }

                Shutdown();
                return;
            }

            StartAfterUpdateCheck(args);
        }

        // ---------- errors ----------

        private bool _errorDialogOpen;
        private int _errorCount;
        private DateTime _errorWindowStart;

        /// <summary>
        /// An error on the screen's thread used to close GameDeck without a word. Now it is saved to a file, GameDeck keeps going
        /// (or closes, if the user prefers), and the user can open a bug report that is already filled in.
        /// </summary>
        private void OnUiThreadError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var log = CrashLog.Write(e.Exception, "Window");
            UpdateService.NoteCrash();
            e.Handled = true;

            if (_errorDialogOpen) return;   // (an error while the error window is showing)

            // an error that repeats at once (something that fails on every tick) would bury the screen: then close
            var now = DateTime.UtcNow;
            if (now - _errorWindowStart > TimeSpan.FromSeconds(10))
            {
                _errorWindowStart = now;
                _errorCount = 0;
            }

            if (++_errorCount > 3)
            {
                ExitAfterError();
                return;
            }

            _errorDialogOpen = true;
            try
            {
                var summary = CrashLog.Sanitize($"{e.Exception.GetType().Name}: {e.Exception.Message}");
                var owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);

                var choice = MessageDialog.Choose(owner, "GameDeck ran into a problem",
                    "Something unexpected happened, so GameDeck stopped what it was doing.\n\n" + summary +
                    (log is null ? "" : "\n\nThe details were saved in:\n" + CrashLog.Sanitize(log)) +
                    "\n\nYou can report it on GitHub: the report opens already filled in, and nothing is sent until you press Submit there.",
                    "Report on GitHub", "Close GameDeck", "Keep going");

                if (choice == MessageChoice.Yes)
                    Process.Start(new ProcessStartInfo(CrashLog.ReportUrl(e.Exception)) { UseShellExecute = true });
                else if (choice == MessageChoice.No)
                    ExitAfterError();
            }
            catch
            {
                // the error window itself failed: the error is in the log file
            }
            finally
            {
                _errorDialogOpen = false;
            }
        }

        /// <summary>Closes GameDeck after an error (saving the playtime when the main window exists).</summary>
        private void ExitAfterError()
        {
            if (MainWindow is MainWindow main)
                main.ForceExit();
            else
                Shutdown(1);
        }

        // ---------- once the app is up ----------

        /// <summary>
        /// After 15 seconds without a crash the app has proven it works: a fresh update becomes final (the previous version's
        /// file is removed) and its release notes are shown once. The daily backup of the user's data also runs here.
        /// </summary>
        private void BeginHealthWatch(MainWindow window)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                UpdateService.MarkHealthy();

                // (only when the window is on screen: when GameDeck started hidden in the tray the notes wait for a later start)
                if (window.IsVisible && UpdateService.TakeWhatsNew() is { } news)
                    MessageDialog.Show(window, news.Notes, $"GameDeck was updated to {news.Version}");
            };
            timer.Start();

            // the daily backup: now, and again every few hours for as long as GameDeck keeps running (it often sits in the tray)
            _ = Task.Run(AutoBackupService.RunIfDue);

            var backups = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            backups.Tick += (_, _) => _ = Task.Run(AutoBackupService.RunIfDue);
            backups.Start();
        }

        /// <summary>
        /// Like Steam: a newer version is installed before anything else happens, then GameDeck starts again as that
        /// version. This only runs here, once per start of GameDeck.
        /// </summary>
        private async void StartAfterUpdateCheck(string[] args)
        {
            var startedByWindows = args.Contains(StartupService.StartupArgument) && SettingsService.StartMinimized;

            // started from a game's desktop shortcut: GameDeck stays in the tray and starts the game
            var shortcutGame = ShortcutService.LaunchTargetFrom(args);

            if (SettingsService.AutoUpdate && UpdateService.CanUpdate)
            {
                // (no window is open yet: don't let closing the progress window end the program)
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                if (await UpdateAsync(args, startedByWindows || shortcutGame is not null))
                    return;   // the new version is starting

                ShutdownMode = ShutdownMode.OnLastWindowClose;
            }

            UpdateService.CleanUpOldVersion();   // the file the previous update left behind

            var window = new MainWindow();
            MainWindow = window;

            // Started by Windows at sign-in ("Launch GameDeck when Windows starts"): stay in the system tray,
            // unless the user turned off "Launch GameDeck minimized to the system tray"
            if (startedByWindows || shortcutGame is not null)
                window.StartInTray();
            else if (SettingsService.StartInBigPicture)
                window.StartInBigPicture();   // console-style: straight to the controller screen
            else
                window.Show();

            ListenForShowRequests(window);
            ShortcutService.Listen(target => Dispatcher.BeginInvoke(() => window.LaunchFromShortcut(target)));
            BeginHealthWatch(window);

            if (shortcutGame is not null)
                window.LaunchFromShortcut(shortcutGame);

#if BETA || DEBUG
            // a look at the "a new version is ready" notice, which the Beta never shows by itself (it never updates):
            // GameDeckBeta.exe --demo-update  (not in the released program)
            if (args.Contains("--demo-update", StringComparer.OrdinalIgnoreCase))
                window.ShowUpdateReady("9.9.9 (demo)");
#endif
        }

        /// <returns>True when a new version was installed and has been started (this one is closing).</returns>
        private async Task<bool> UpdateAsync(string[] args, bool silent)
        {
            var update = await UpdateService.CheckAsync(TimeSpan.FromSeconds(4));
            if (update is null) return false;

            // started by Windows at sign-in, sitting in the tray: update without putting a window on screen
            var window = silent ? null : new UpdateWindow($"{update.Version.Major}.{update.Version.Minor}.{update.Version.Build}");
            window?.Show();

            var installed = await UpdateService.InstallAsync(update, window is null ? null : new Progress<double>(window.SetProgress));
            window?.Close();

            if (!installed) return false;

            // let go of the "only one GameDeck" lock so the new version can take it, then start it
            _instanceMutex?.Dispose();
            _instanceMutex = null;

            try
            {
                UpdateService.Restart(args);
            }
            catch
            {
                // couldn't start it: carry on with this one (the new file is used from the next start) and take the lock back
                _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out _);
                return false;
            }

            Shutdown();
            return true;
        }

        /// <summary>Signals the GameDeck that is already running. Windows lets that one take the focus because we ask for it.</summary>
        private static void AskRunningInstanceToShow()
        {
            try
            {
                AllowSetForegroundWindow(-1);   // ASFW_ANY: this process was started by the user, so it may hand over the focus

                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                {
                    show.Set();
                    show.Dispose();
                }
            }
            catch
            {
                // nothing more to do: just don't open a second window
            }
        }

        /// <summary>Waits (on a background thread, no polling) until another start of GameDeck asks this one to show itself.</summary>
        private void ListenForShowRequests(MainWindow window)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var show = _showEvent;

            var listener = new Thread(() =>
            {
                try
                {
                    while (show.WaitOne())
                        Dispatcher.BeginInvoke(window.ShowRequested);
                }
                catch (ObjectDisposedException)
                {
                    // GameDeck is closing
                }
            })
            {
                IsBackground = true,
                Name = "GameDeck show-window listener"
            };
            listener.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // closed normally (not from an error window): an update that was just installed worked
            if (e.ApplicationExitCode == 0)
                UpdateService.MarkHealthy(cleanUp: false);

            _showEvent?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
