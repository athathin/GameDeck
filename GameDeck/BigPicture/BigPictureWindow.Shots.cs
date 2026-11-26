using System.IO;
using System.Windows.Media.Imaging;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- a game's screenshots, full screen ----------

    private IReadOnlyList<string> _shotFiles = [];
    private int _shotIndex;
    private bool _shotsFromMedia;   // opened from the Media tab (Back goes back there), not from a game's page

    private void DetailsView_ScreenshotRequested(object? sender, (IReadOnlyList<string> Files, int Index) e) =>
        OpenShots(e.Files, e.Index);

    private void OpenShots(IReadOnlyList<string> files, int index, bool fromMedia = false)
    {
        if (files.Count == 0) return;

        _shotsFromMedia = fromMedia;
        _shotFiles = files;
        _shotIndex = Math.Clamp(index, 0, files.Count - 1);
        ShowOverlay(Overlay.Shots);
        ShowShot();
        FillShotHints();
    }

    private void FillShotHints()
    {
        var l = Labels();
        var keyboard = ShownKind == ControllerKind.None;   // (PgUp and PgDn do nothing here: the arrow keys do)
        FillHints(ShotHints, (keyboard ? "←" : l.Left, "Previous"), (keyboard ? "→" : l.Right, "Next"),
            (l.X, "Show in folder"), (l.Back, "Back"));
    }

    /// <summary>Opens File Explorer with the picture on screen selected (Explorer comes in front of Big Picture).</summary>
    private void ShowShotInFolder()
    {
        if (!ScreenshotService.ShowInFolder(_shotFiles[_shotIndex]))
            ShotCaption.Text = $"{_shotIndex + 1} / {_shotFiles.Count}   {Path.GetFileName(_shotFiles[_shotIndex])}   (couldn't open its folder)";
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
    }

    private void HandleShots(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left or PadButton.LeftShoulder: MoveShot(-1); break;
            case PadButton.Right or PadButton.RightShoulder: MoveShot(1); break;
            case PadButton.X: ShowShotInFolder(); break;
            case PadButton.Back or PadButton.Confirm:
                ShotImage.Source = null;           // frees the picture

                if (_shotsFromMedia)
                    ReturnToMedia(_shotIndex);     // back to the Media tab, on the picture it ended on
                else
                    ShowOverlay(Overlay.Details);  // back to the game's page
                break;
        }
    }

    private void MoveShot(int step)
    {
        _shotIndex = (_shotIndex + step + _shotFiles.Count) % _shotFiles.Count;
        ShowShot();
    }
}
