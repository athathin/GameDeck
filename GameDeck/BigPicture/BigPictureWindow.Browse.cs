using System.IO;
using System.Windows;
using GameDeck.Models;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- adding games with the controller: a file browser (Settings > Add games) ----------

    private const int BrowseLimit = 300;   // a folder with thousands of entries (System32...) must not make the list crawl

    private string? _browsePath;                      // null = the start page: places and drives
    private List<FilterRow> _browseRows = [];
    private int _browseIndex;
    private readonly HashSet<string> _browsePicked = new(StringComparer.OrdinalIgnoreCase);

    // null = adding games (.exe files, from Settings); a game = adding pictures to its screenshots (from its page)
    private Game? _browseShotsFor;

    /// <summary>Choosing a folder for a setting: the browser's title, its "use this folder" row, what choosing one does.</summary>
    private sealed record FolderChoice(string Title, string ChooseHere, Action<string> Chosen);

    // not null = choosing a folder (Settings › Screenshots, Settings › System › Backup): folders only
    private FolderChoice? _browseFolder;

    private void OpenBrowse()
    {
        _browseShotsFor = null;
        _browseFolder = null;
        StartBrowse();
    }

    /// <summary>The same browser, picking pictures to add to the game's screenshots ("Add screenshots" on its page).</summary>
    private void OpenShotBrowse(Game game)
    {
        _browseShotsFor = game;
        _browseFolder = null;
        StartBrowse();
    }

    /// <summary>The same browser, choosing the folder the controller's screenshots are saved in (Settings › Screenshots).</summary>
    private void OpenSaveFolderBrowse() => OpenFolderBrowse(new(
        "Save the controller's screenshots in: choose a folder", "Save screenshots here", folder =>
        {
            SettingsService.ScreenshotFolder = folder;
            ShowSettingsStatus("Screenshots are saved in " + folder);
        }));

    /// <summary>The same browser, choosing the folder a backup is saved in every day (Settings › System).</summary>
    private void OpenBackupFolderBrowse() => OpenFolderBrowse(new(
        "Save a backup every day in: choose a folder (OneDrive, a USB drive...)", "Save the backups here", folder =>
        {
            SettingsService.BackupFolder = folder;
            ShowSettingsStatus("A backup is saved every day in " + BackupService.FolderIn(folder));
        }));

    private void OpenFolderBrowse(FolderChoice choice)
    {
        _browseShotsFor = null;
        _browseFolder = choice;
        StartBrowse();
    }

    private void ChooseFolder(string folder)
    {
        var choice = _browseFolder!;
        CloseBrowse();
        choice.Chosen(folder);   // (after closing: its message shows on the settings)
    }

    private void StartBrowse()
    {
        _browsePath = null;
        _browsePicked.Clear();
        ShowBrowseStatus(null);
        ShowOverlay(Overlay.Browse);
        BuildBrowse(firstRow: true);
        FillBrowseHints();
    }

    /// <summary>Back to where it was opened from: the settings, or the game's page (with its new screenshots).</summary>
    private void CloseBrowse()
    {
        if (_browseShotsFor is null)
        {
            _browseFolder = null;
            OpenSettings(keepPlace: true);   // the section it was opened from (Library › Add games, Screenshots › Browse)
            return;
        }

        _browseShotsFor = null;
        ShowOverlay(Overlay.Details);
        DetailsView.ReloadShots();      // (the hints follow once they are found: ShotsChanged)
        SelectDetailsButton(addShots: true);
    }

    private void FillBrowseHints()
    {
        var l = Labels();

        var open = _browseFolder is not null ? "Open / choose" : "Open / pick";

        // "Add picked" only once something is picked (X does nothing before); the keyboard's F does the same as X here
        var hints = new List<(string, string)> { (l.Confirm, open) };
        if (_browsePicked.Count > 0)
            hints.Add((l.X, "Add picked"));
        hints.Add((l.Back, _browsePath is null ? "Close" : "Up"));

        AddScrollHint(hints, Scrollable(ScrollOf(BrowseList)));
        FillHints(BrowseHints, hints.ToArray());
    }

    /// <summary>Builds the rows of the current folder (or the start page) and draws them.</summary>
    private void BuildBrowse(bool firstRow = false)
    {
        var rows = new List<FilterRow>();

        if (_browsePath is null)
            AddPlaces(rows);
        else
            AddFolder(rows, _browsePath);

        _browseRows = rows;
        BrowseTitle.Text = _browsePath ?? (_browseShotsFor is { } game
            ? $"Add screenshots to {game.DisplayName}: choose where to look"
            : _browseFolder?.Title ?? "Add games: choose where to look");

        if (firstRow || _browseIndex < 0 || _browseIndex >= rows.Count || rows[_browseIndex].IsHeader)
            _browseIndex = Math.Max(0, rows.FindIndex(r => !r.IsHeader));

        RenderBrowse();
        FillBrowseHints();
    }

    private void AddPlaces(List<FilterRow> rows)
    {
        rows.Add(new FilterRow("PLACES", IsHeader: true));

        var common = new[]
        {
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        };

        // pictures: where screenshots usually are first; games: where programs are installed last
        var places = _browseShotsFor is not null || _browseFolder is not null
            ? ScreenshotService.UsualPlaces().Concat(common).ToArray()
            : common.Concat(
              [
                  ("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
                  ("Program Files (x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
              ]).ToArray();

        foreach (var (name, path) in places.Where(p => p.Item2.Length > 0 && Directory.Exists(p.Item2)).DistinctBy(p => p.Item2))
        {
            var target = path;
            rows.Add(new FilterRow(name + "  ›", Activate: () => GoTo(target)));
        }

        rows.Add(new FilterRow("DRIVES", IsHeader: true));
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;

                var target = drive.RootDirectory.FullName;
                rows.Add(new FilterRow($"{drive.Name}   {drive.DriveType}, {drive.AvailableFreeSpace / 1_000_000_000} GB free  ›",
                                       Activate: () => GoTo(target)));
            }
            catch (IOException) { }
        }
    }

    private void AddFolder(List<FilterRow> rows, string path)
    {
        var parent = Directory.GetParent(path)?.FullName;
        rows.Add(new FilterRow(parent is null ? "‹  Places and drives" : "‹  Up", Activate: () => GoTo(parent)));

        var pictures = _browseShotsFor is not null;
        var what = pictures ? "picture" : "game";

        if (_browsePicked.Count > 0)
        {
            var count = _browsePicked.Count;
            rows.Add(new FilterRow(count == 1 ? $"Add the 1 picked {what}" : $"Add the {count} picked {what}s", Activate: AddPicked));
        }

        var foldersOnly = _browseFolder is not null;
        if (_browseFolder is { } choice)
            rows.Add(new FilterRow(choice.ChooseHere, Activate: () => ChooseFolder(path)));
        else if (!pictures)
            rows.Add(new FilterRow("Add this folder as a game  (GameDeck finds its program)", Activate: () => AddFolderAsGame(path)));

        List<string> folders = [];
        List<string> programs = [];
        var cut = false;

        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.Hidden) || entry.Attributes.HasFlag(FileAttributes.System)) continue;

                if (entry is DirectoryInfo)
                    folders.Add(entry.FullName);
                else if (foldersOnly)
                    continue;   // (choosing a folder: the files in it don't matter)
                else if (pictures
                             ? ScreenshotService.Extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase)
                             : entry.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    programs.Add(entry.FullName);   // (the pictures, when adding screenshots)

                if (folders.Count + programs.Count >= BrowseLimit)
                {
                    cut = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            rows.Add(new FilterRow("This folder can't be opened."));
            return;
        }

        folders.Sort(StringComparer.CurrentCultureIgnoreCase);
        programs.Sort(StringComparer.CurrentCultureIgnoreCase);

        if (folders.Count > 0)
        {
            rows.Add(new FilterRow("FOLDERS", IsHeader: true));
            foreach (var folder in folders)
            {
                var target = folder;
                rows.Add(new FilterRow(Path.GetFileName(folder) + "  ›", Activate: () => GoTo(target)));
            }
        }

        if (programs.Count > 0)
        {
            rows.Add(new FilterRow(pictures ? "PICTURES  (pick the screenshots)" : "PROGRAMS  (pick the games' .exe files)", IsHeader: true));
            foreach (var program in programs)
            {
                var file = program;
                rows.Add(new FilterRow(Path.GetFileName(file), IsOn: () => _browsePicked.Contains(file), Activate: () =>
                {
                    if (!_browsePicked.Remove(file))
                        _browsePicked.Add(file);

                    BuildBrowse();   // the "Add the N picked games" row changes too
                }));
            }
        }

        if (folders.Count == 0 && programs.Count == 0)
            rows.Add(new FilterRow(_browseFolder is { } empty ? $"No folders inside. Choose \"{empty.ChooseHere}\" to use this one."
                : pictures ? "Nothing to pick here: no folders and no pictures."
                : "Nothing to pick here: no folders and no programs."));

        if (cut)
            rows.Add(new FilterRow($"Only the first {BrowseLimit} entries are shown."));
    }

    private void GoTo(string? path)
    {
        _browsePath = path;
        BuildBrowse(firstRow: true);
    }

    private void AddPicked()
    {
        var files = _browsePicked.ToList();
        _browsePicked.Clear();

        if (_browseShotsFor is { } game)
        {
            var added = ScreenshotService.Add(game, files);
            ShowBrowseStatus(added == files.Count
                ? $"Added {added} {(added == 1 ? "picture" : "pictures")} to {game.DisplayName}'s screenshots."
                : $"Added {added} of {files.Count} pictures (the others couldn't be copied).");
            BuildBrowse();
            return;
        }

        AddFiles(files);
    }

    private void AddFolderAsGame(string folder)
    {
        var exe = ExeFinder.FindMainExe(folder);
        if (exe is null)
        {
            ShowBrowseStatus("No program found in this folder. Open it and pick the .exe yourself.");
            return;
        }

        AddFiles([exe]);
    }

    private void AddFiles(IReadOnlyList<string> files)
    {
        var status = _actions.AddFiles(files);
        ReloadLibrary();   // the screen behind shows the new games
        ShowBrowseStatus(status);
        BuildBrowse();
    }

    private void ShowBrowseStatus(string? text)
    {
        BrowseStatus.Text = text ?? "";
        BrowseStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderBrowse() =>
        RenderRows(BrowseList, _browseRows, _browseIndex, index =>
        {
            _browseIndex = index;
            ActivateBrowseRow();
        }, index =>
        {
            if (_browseIndex == index) return;

            _browseIndex = index;
            RenderOnHover(RenderBrowse);
        });

    private void ActivateBrowseRow()
    {
        if (_browseIndex >= 0 && _browseIndex < _browseRows.Count)
            _browseRows[_browseIndex].Activate?.Invoke();
    }

    private void HandleBrowse(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: MoveBrowse(-1); break;
            case PadButton.Down: MoveBrowse(1); break;
            case PadButton.LeftTrigger or PadButton.RightTrigger: PageThrough(() => _browseIndex, MoveBrowse, TriggerDirection(button)); break;
            case PadButton.Confirm: ActivateBrowseRow(); break;
            case PadButton.X when _browsePicked.Count > 0: AddPicked(); break;
            case PadButton.Back:
                if (_browsePath is null) CloseBrowse();
                else GoTo(Directory.GetParent(_browsePath)?.FullName);
                break;
        }
    }

    private void MoveBrowse(int step)
    {
        var next = _browseIndex;

        do
        {
            next += step;
            if (next < 0 || next >= _browseRows.Count) return;
        }
        while (_browseRows[next].IsHeader);

        _browseIndex = next;
        RenderBrowse();
    }
}
