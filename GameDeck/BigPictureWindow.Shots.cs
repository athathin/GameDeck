using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- a game's screenshots, full screen ----------

    private IReadOnlyList<string> _shotFiles = [];
    private int _shotIndex;

    private void DetailsView_ScreenshotRequested(object? sender, (IReadOnlyList<string> Files, int Index) e) =>
        OpenShots(e.Files, e.Index);

    private void OpenShots(IReadOnlyList<string> files, int index)
    {
        if (files.Count == 0) return;

        _shotFiles = files;
        _shotIndex = Math.Clamp(index, 0, files.Count - 1);
        ShowOverlay(Overlay.Shots);
        ShowShot();
        FillShotHints();
    }

    private void FillShotHints()
    {
        var l = Labels();
        FillHints(ShotHints, (l.Left, "Previous"), (l.Right, "Next"), (l.Back, "Back"));
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
            case PadButton.Back or PadButton.Confirm:
                ShotImage.Source = null;           // frees the picture
                ShowOverlay(Overlay.Details);      // back to the game's page
                break;
        }
    }

    private void MoveShot(int step)
    {
        _shotIndex = (_shotIndex + step + _shotFiles.Count) % _shotFiles.Count;
        ShowShot();
    }
}
