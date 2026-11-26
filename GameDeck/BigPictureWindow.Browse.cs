using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameDeck.Models;
using GameDeck.Services;

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

    private void OpenBrowse()
    {
        _browseShotsFor = null;
        StartBrowse();
    }

    /// <summary>The same browser, picking pictures to add to the game's screenshots ("Add screenshots" on its page).</summary>
    private void OpenShotBrowse(Game game)
    {
        _browseShotsFor = game;
        StartBrowse();
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
            OpenSettings(keepPlace: true);   // the Library section, on "Add games"
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

        // "Add picked" only once something is picked (X does nothing before); the keyboard's F does the same as X here
        if (_browsePicked.Count > 0)
            FillHints(BrowseHints, (l.Confirm, "Open / pick"), (l.X, "Add picked"), (l.Back, _browsePath is null ? "Close" : "Up"));
        else
            FillHints(BrowseHints, (l.Confirm, "Open / pick"), (l.Back, _browsePath is null ? "Close" : "Up"));
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
            : "Add games: choose where to look");

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
        var places = _browseShotsFor is not null
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

        if (!pictures)
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
            rows.Add(new FilterRow(pictures ? "Nothing to pick here: no folders and no pictures." : "Nothing to pick here: no folders and no programs."));

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
        _ = RenderRows(BrowseList, _browseRows, _browseIndex, index =>
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
