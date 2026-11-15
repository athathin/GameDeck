using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDeck.Views;

/// <summary>
/// A border that clips what it holds to its own rounded corners, whatever size it has (a plain Border only clips to a rectangle).
/// The clip follows every size change by itself, so it can never be left over from an older size.
/// </summary>
public class RoundedClipBorder : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        var radius = CornerRadius.TopLeft;
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
        clip.Freeze();
        Clip = clip;
    }
}
