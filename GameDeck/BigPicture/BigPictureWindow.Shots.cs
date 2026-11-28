using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- a game's screenshots, full screen ----------

    private List<string> _shotFiles = [];
    private int _shotIndex;
    private bool _shotsFromMedia;   // opened from the Media tab (Back goes back there), not from a game's page

    // X asked "move this picture to the Recycle Bin?": A says yes, Back says no
    private bool _shotAskingDelete;

    // opened by the Media tab's Delete (X there): once answered, back to the tab instead of staying on the picture
    private bool _shotDeleteOnly;

    // a picture was moved to the Recycle Bin: the Media tab / the game's page read their pictures again when it closes
    private bool _shotsDeleted;

    private void DetailsView_ScreenshotRequested(object? sender, (IReadOnlyList<string> Files, int Index) e) =>
        OpenShots(e.Files, e.Index);

    private void OpenShots(IReadOnlyList<string> files, int index, bool fromMedia = false, bool askDelete = false)
    {
        if (files.Count == 0) return;

        _shotsFromMedia = fromMedia;
        _shotFiles = [.. files];
        _shotIndex = Math.Clamp(index, 0, files.Count - 1);
        _shotAskingDelete = askDelete;
        _shotDeleteChoice = 0;
        _shotDeleteOnly = askDelete;
        _shotsDeleted = false;
        ShowOverlay(Overlay.Shots);
        ShowShot();
        FillShotHints();
    }

    private void FillShotHints()
    {
        var l = Labels();

        if (_shotAskingDelete)
        {
            FillHints(ShotDeleteHints, (l.Confirm, "Choose"), (l.Back, "Cancel"));
            return;
        }

        var keyboard = ShownKind == ControllerKind.None;   // (PgUp and PgDn do nothing here: the arrow keys do)
        FillHints(ShotHints, (keyboard ? "←" : l.Left, "Previous"), (keyboard ? "→" : l.Right, "Next"),
            (keyboard ? "Del" : l.X, "Delete"), (l.Back, "Back"));
    }

    private void ShowShot()
    {
        var file = _shotFiles[_shotIndex];

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelHeight = 1080;   // no need for more than the screen can show (a 4K picture would cost a lot of memory)
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            ShotImage.Source = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            ShotImage.Source = null;   // (the caption still says which file it was)
        }

        ShotCaption.Text = $"{_shotIndex + 1} / {_shotFiles.Count}   {Path.GetFileName(file)}";
        ShowDeleteCard();
    }

    // ---- the "delete this screenshot?" card ----

    private int _shotDeleteChoice;   // 0 = Move to the Recycle Bin, 1 = Cancel

    /// <summary>Shows the card over the picture while the question is asked (hides it otherwise), with its two buttons.</summary>
    private void ShowDeleteCard()
    {
        ShotDeleteCard.Visibility = _shotAskingDelete ? Visibility.Visible : Visibility.Collapsed;
        ShotHints.Visibility = _shotAskingDelete ? Visibility.Collapsed : Visibility.Visible;
        if (!_shotAskingDelete) return;

        ShotDeleteThumb.Source = ShotImage.Source;
        ShotDeleteText.Text = $"{Path.GetFileName(_shotFiles[_shotIndex])} goes to the Recycle Bin. You can restore it from there.";

        ShotDeleteButtons.Children.Clear();
        string[] buttons = ["Move to the Recycle Bin", "Cancel"];
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var selected = i == _shotDeleteChoice;

            var cell = new Border
            {
                Background = selected ? ThemeService.SelectBg : KeyBackground, CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 13, 20, 13), Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = buttons[i], FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = selected ? Dark : ThemeService.TextPrimary
                }
            };

            // the mouse: pointing selects (like the stick), a click chooses
            cell.MouseEnter += (_, _) =>
            {
                if (!PointerIsChoosing || _shotDeleteChoice == index) return;
                _shotDeleteChoice = index;
                ShowDeleteCard();
            };
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _shotDeleteChoice = index;
                ChooseInDeleteCard();
            };

            ShotDeleteButtons.Children.Add(cell);
        }
    }

    private void ChooseInDeleteCard()
    {
        if (_shotDeleteChoice == 0)
            DeleteShot();
        else
            AnswerDelete();   // no: the picture stays
    }

    private void ShotDeleteCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // a click on the dimmed picture around the card: Cancel
        if (!ReferenceEquals(e.OriginalSource, ShotDeleteCard)) return;

        e.Handled = true;
        AnswerDelete();
    }

    private void HandleShots(PadButton button)
    {
        if (_shotAskingDelete)
        {
            switch (button)
            {
                case PadButton.Up or PadButton.Left: _shotDeleteChoice = 0; ShowDeleteCard(); break;
                case PadButton.Down or PadButton.Right: _shotDeleteChoice = 1; ShowDeleteCard(); break;
                case PadButton.Confirm: ChooseInDeleteCard(); break;
                case PadButton.Back or PadButton.X: AnswerDelete(); break;   // no: the picture stays
            }
            return;
        }

        switch (button)
        {
            case PadButton.Left or PadButton.LeftShoulder: MoveShot(-1); break;
            case PadButton.Right or PadButton.RightShoulder: MoveShot(1); break;
            case PadButton.X: AskDeleteShot(); break;
            case PadButton.Back or PadButton.Confirm: CloseShots(); break;
        }
    }

    /// <summary>"Move this picture to the Recycle Bin?" over the picture: A or Back answers.</summary>
    private void AskDeleteShot()
    {
        _shotAskingDelete = true;
        _shotDeleteChoice = 0;   // (A right away moves it: X then A, as asked; Back or Cancel keeps it)
        ShowDeleteCard();
        FillShotHints();
    }

    /// <summary>The question is answered (or the picture is gone): back to the pictures, or to the Media tab when it was opened from there to delete.</summary>
    private void AnswerDelete()
    {
        _shotAskingDelete = false;

        if (_shotDeleteOnly || _shotFiles.Count == 0)
        {
            CloseShots();
            return;
        }

        ShowShot();
        FillShotHints();
    }

    private void DeleteShot()
    {
        var file = _shotFiles[_shotIndex];

        if (!ScreenshotService.MoveToRecycleBin(file))
        {
            _shotAskingDelete = false;
            ShowShot();
            ShotCaption.Text += "   (couldn't move it to the Recycle Bin)";
            FillShotHints();
            return;
        }

        _shotsDeleted = true;
        _shotFiles.RemoveAt(_shotIndex);
        if (_shotIndex >= _shotFiles.Count)
            _shotIndex = Math.Max(0, _shotFiles.Count - 1);   // (the last one went: the one before it)

        AnswerDelete();
    }

    /// <summary>Back to where the pictures were opened from: the Media tab (on the picture it ended on) or the game's page.</summary>
    private void CloseShots()
    {
        ShotImage.Source = null;   // frees the picture
        _shotAskingDelete = false;

        var deleted = _shotsDeleted;
        _shotsDeleted = false;

        if (_shotsFromMedia)
        {
            ReturnToMedia(_shotIndex, reload: deleted);   // back to the Media tab, on the picture it ended on
            return;
        }

        ShowOverlay(Overlay.Details);   // back to the game's page
        if (deleted)
            DetailsView.ReloadShots();  // (the hints follow once they are found: ShotsChanged)
    }

    private void MoveShot(int step)
    {
        _shotIndex = (_shotIndex + step + _shotFiles.Count) % _shotFiles.Count;
        ShowShot();
    }
}
