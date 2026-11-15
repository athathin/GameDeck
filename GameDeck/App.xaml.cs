using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using GameDeck.Services;
using GameDeck.Views;

namespace GameDeck
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // One GameDeck at a time. The names are per Windows session ("Local\"), and the Debug build uses its own
        // pair, so pressing F5 in Visual Studio works while the published app is running.
#if DEBUG
        private const string InstanceName = @"Local\GameDeck-SingleInstance-Debug";
        private const string ShowEventName = @"Local\GameDeck-ShowWindow-Debug";
#else
        private const string InstanceName = @"Local\GameDeck-SingleInstance";
        private const string ShowEventName = @"Local\GameDeck-ShowWindow";
#endif

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Already running (maybe hidden in the system tray)? Ask that one to come to the front and quit.
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                // Started by Windows at sign-in while GameDeck is already running: nothing to show.
                if (!e.Args.Contains(StartupService.StartupArgument))
                    AskRunningInstanceToShow();

                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }

            StartAfterUpdateCheck(e.Args);
        }

        /// <summary>
        /// Like Steam: a newer version is installed before anything else happens, then GameDeck starts again as that
        /// version. This only runs here, once per start of GameDeck.
        /// </summary>
        private async void StartAfterUpdateCheck(string[] args)
        {
            var startedByWindows = args.Contains(StartupService.StartupArgument) && SettingsService.StartMinimized;

            if (SettingsService.AutoUpdate && UpdateService.CanUpdate)
            {
                // (no window is open yet: don't let closing the progress window end the program)
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                if (await UpdateAsync(args, startedByWindows))
                    return;   // the new version is starting

                ShutdownMode = ShutdownMode.OnLastWindowClose;
            }

            UpdateService.CleanUpOldVersion();   // the file the previous update left behind

            var window = new MainWindow();
            MainWindow = window;

            // Started by Windows at sign-in ("Launch GameDeck when Windows starts"): stay in the system tray,
            // unless the user turned off "Launch GameDeck minimized to the system tray"
            if (startedByWindows)
                window.StartInTray();
            else
                window.Show();

            ListenForShowRequests(window);
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
                        Dispatcher.BeginInvoke(window.BringToFront);
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
            _showEvent?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
