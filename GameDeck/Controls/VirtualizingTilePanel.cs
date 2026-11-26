using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GameDeck.Controls;

/// <summary>
/// A wrapping grid of equally sized tiles that only creates the tiles that are on screen (plus a spare row above
/// and below). WPF's WrapPanel creates every tile of the library at once, which costs memory and start-up time
/// that grows with the number of games. Tiles are left to right, top to bottom, scrolled vertically.
/// </summary>
public class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(176.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ItemInsetProperty = DependencyProperty.Register(
        nameof(ItemInset), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty TileAspectProperty = DependencyProperty.Register(
        nameof(TileAspect), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>The wanted width of one tile slot (the tile plus its margin). The real width is a little different:
    /// the columns share the whole row evenly, so there is never a gap next to the scrollbar.</summary>
    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>Margin around the tile inside its slot, both sides together.</summary>
    public double ItemInset
    {
        get => (double)GetValue(ItemInsetProperty);
        set => SetValue(ItemInsetProperty, value);
    }

    /// <summary>Tile height divided by tile width (2:3 posters = 1.5).</summary>
    public double TileAspect
    {
        get => (double)GetValue(TileAspectProperty);
        set => SetValue(TileAspectProperty, value);
    }

    private const double WheelStep = 72;
    private const double LineStep = 24;

    private Size _extent;
    private Size _viewport;
    private double _offsetY;
    private int _perRow = 1;
    private double _slotWidth = 176;    // real slot size after the row has been shared out
    private double _slotHeight = 256;

    // ---------- layout ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        // ItemContainerGenerator stays null until InternalChildren has been read once
        _ = InternalChildren;

        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? _slotHeight * 4 : availableSize.Height;

        _perRow = Math.Max(1, (int)(width / ItemWidth));
        _slotWidth = width / _perRow;
        _slotHeight = Math.Max(1, _slotWidth - ItemInset) * TileAspect + ItemInset;
        var rows = (count + _perRow - 1) / _perRow;
        UpdateScrollInfo(new Size(width, height), new Size(width, rows * _slotHeight));

        // rows that are on screen, plus one spare row each side so scrolling doesn't show empty gaps
        var firstRow = Math.Max(0, (int)Math.Floor(_offsetY / _slotHeight) - 1);
        var lastRow = (int)Math.Ceiling((_offsetY + height) / _slotHeight);
        var first = count == 0 ? 0 : firstRow * _perRow;
        var last = count == 0 ? -1 : Math.Min(count - 1, (lastRow + 1) * _perRow - 1);

        if (first <= last)
            Realize(first, last);

        CleanUp(first, last);

        return new Size(width, double.IsInfinity(availableSize.Height) ? rows * _slotHeight : height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            var row = index / _perRow;
            var column = index % _perRow;

            InternalChildren[i].Arrange(new Rect(column * _slotWidth, row * _slotHeight - _offsetY, _slotWidth, _slotHeight));
        }

        return finalSize;
    }

    /// <summary>Creates the tiles for items first..last that don't exist yet.</summary>
    private void Realize(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var position = generator.GeneratorPositionFromIndex(first);
        var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;

        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            for (var item = first; item <= last; item++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(_slotWidth, _slotHeight));
            }
        }
    }

    /// <summary>Throws away the tiles that scrolled out of the first..last range (this is what frees their memory).</summary>
    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);

            if (index < first || index > last)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;

        var top = index / _perRow * _slotHeight;
        var bottom = top + _slotHeight;

        if (top < _offsetY)
            SetVerticalOffset(top);
        else if (bottom > _offsetY + _viewport.Height)
            SetVerticalOffset(bottom - _viewport.Height);
    }

    // ---------- scrolling (IScrollInfo) ----------

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;

        _offsetY = Math.Min(_offsetY, Math.Max(0, extent.Height - viewport.Height));

        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offsetY;

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (offset == _offsetY) return;

        _offsetY = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { }

    public void LineUp() => SetVerticalOffset(_offsetY - LineStep);
    public void LineDown() => SetVerticalOffset(_offsetY + LineStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offsetY - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offsetY + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offsetY - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offsetY + WheelStep);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>Keeps the tile that has keyboard focus in view.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var childIndex = visual is UIElement element ? InternalChildren.IndexOf(element) : -1;
        if (childIndex >= 0)
            BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0)));

        return rectangle;
    }
}
