using System.Windows.Threading;
using GameDeck.Services;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- the daily backup in a folder the user chose (Settings › Backup) ----------

    // Checked at the start, every hour while GameDeck runs (it often sits in the tray for days) and when a folder is chosen.
    // Today's file is made once; when the folder isn't there (a USB drive pulled out), the next hour tries again.
    private readonly DispatcherTimer _folderBackupTimer = new() { Interval = TimeSpan.FromHours(1) };

    private void StartFolderBackups()
    {
        _folderBackupTimer.Tick += (_, _) => SaveFolderBackup();
        _folderBackupTimer.Start();
        SettingsService.BackupFolderChanged += SaveFolderBackup;

        // (a moment after the start: the library and the data are loaded by then)
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, SaveFolderBackup);
    }

    private void StopFolderBackups()
    {
        _folderBackupTimer.Stop();
        SettingsService.BackupFolderChanged -= SaveFolderBackup;
    }

    private void SaveFolderBackup()
    {
        if (SettingsService.BackupFolder is { } folder)
            BackupService.SaveDailyIfDue(folder, _userData, _library);
    }
}
