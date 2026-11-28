using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- shared by the panels: drawing a list of rows, and showing one panel at a time ----------

    private bool _renderingOnHover;

    /// <summary>Redraws a list because the mouse moved onto another row: the list must not scroll while that happens.</summary>
    private void RenderOnHover(Action render)
    {
        _renderingOnHover = true;
        try { render(); }
        finally { _renderingOnHover = false; }
    }

    /// <summary>
    /// What a drawn list remembers (in its panel's Tag): what its rows were (to tell whether only the selection moved), their
    /// cells, the selected one, and what a click does now. The cells ask it at the moment of the click, so cells kept from an
    /// earlier drawing always do what the newest drawing says.
    /// </summary>
    private sealed class DrawnRows
    {
        public required string Signature;
        public required List<FilterRow> Rows;
        public required List<FrameworkElement> Cells;
        public int Selected;
        public required Action<int> Activate;
        public required Action<int> Hover;
        public Action<int, int>? Adjust;
    }

    /// <summary>Everything that changes how the rows look, apart from which one is selected (the theme's colors too).</summary>
    private static string RowsSignature(List<FilterRow> rows) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ThemeService.SelectBg) + "\u0001" +
        string.Join("\u0001", rows.Select(r => $"{r.Text}\u0002{r.IsHeader}{r.IsRadio}{r.IsOn?.Invoke()}{r.Adjust is not null}"));

    /// <summary>
    /// Draws a list of headings, tick boxes, radio buttons and actions (the filters, the game menu and the file browser share it).
    /// When only the selection moved (the same rows as last time), only the two rows that change are drawn again: a long list
    /// (a folder of 300 files) doesn't make every press slow.
    /// </summary>
    /// <param name="adjust">For rows with <see cref="FilterRow.Adjust"/>: the row's index and -1 / +1 (its ‹ › arrows were clicked).</param>
    private void RenderRows(StackPanel panel, List<FilterRow> rows, int selectedIndex, Action<int> activate, Action<int> hover,
                            Action<int, int>? adjust = null)
    {
        var signature = RowsSignature(rows);

        if (panel.Tag is DrawnRows drawn && drawn.Signature == signature && (drawn.Adjust is null) == (adjust is null))
        {
            drawn.Activate = activate;   // (the newest actions: the cells call them through here)
            drawn.Hover = hover;
            drawn.Adjust = adjust;
            drawn.Rows = rows;

            if (drawn.Selected != selectedIndex)
            {
                var before = drawn.Selected;
                drawn.Selected = selectedIndex;
                RedrawRow(panel, drawn, before);
                RedrawRow(panel, drawn, selectedIndex);
            }

            KeepSelectedInView(drawn);
            return;
        }

        var state = new DrawnRows
        {
            Signature = signature, Rows = rows, Cells = [], Selected = selectedIndex, Activate = activate, Hover = hover, Adjust = adjust
        };
        panel.Tag = state;
        panel.Children.Clear();

        for (var i = 0; i < rows.Count; i++)
        {
            var cell = RowCell(panel, state, i);
            state.Cells.Add(cell);
            panel.Children.Add(cell);
        }

        KeepSelectedInView(state);
    }

    /// <summary>Draws one row again (its selection changed), in its place.</summary>
    private void RedrawRow(StackPanel panel, DrawnRows drawn, int index)
    {
        if (index < 0 || index >= drawn.Rows.Count || drawn.Rows[index].IsHeader) return;

        var cell = RowCell(panel, drawn, index);
        drawn.Cells[index] = cell;

        // (WPF can't put an element over another in place: the old one goes out first)
        panel.Children.RemoveAt(index);
        panel.Children.Insert(index, cell);
    }

    /// <summary>
    /// Keeps the selected row on screen when the panel is long (not when the mouse chose the row: scrolling under the pointer
    /// would make the next row the one it points at).
    /// </summary>
    private void KeepSelectedInView(DrawnRows drawn)
    {
        if (drawn.Selected >= 0 && drawn.Selected < drawn.Cells.Count && !_renderingOnHover)
            drawn.Cells[drawn.Selected].BringIntoView();
    }

    /// <summary>One row of a list: a heading, or a cell with its switch, radio button or ‹ › arrows.</summary>
    private FrameworkElement RowCell(StackPanel panel, DrawnRows state, int index)
    {
        var row = state.Rows[index];

        if (row.IsHeader)
            return new TextBlock
            {
                Text = row.Text, Foreground = Muted, FontSize = 14,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 22, 0, 8)
            };

        // (the actions of the newest drawing of this panel)
        DrawnRows Now() => panel.Tag as DrawnRows ?? state;

        var selected = index == state.Selected;
        var on = row.IsOn?.Invoke() == true;

        var line = new DockPanel();
        if (row.Adjust is not null && state.Adjust is not null)
        {
            // ‹ › on the right: left / right on the controller, a click with the mouse
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            foreach (var (glyph, step) in new[] { ("", -1), ("", 1) })
            {
                var arrow = new Border
                {
                    Background = Brushes.Transparent, Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                        Foreground = selected ? Dark : TextGrey, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                arrow.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;   // (not also a click on the row)
                    Now().Adjust?.Invoke(index, step);
                };
                arrows.Children.Add(arrow);
            }

            DockPanel.SetDock(arrows, Dock.Right);
            line.Children.Add(arrows);
        }
        else if (row.IsOn is not null && !row.IsRadio)
        {
            // on / off: the same switch as the settings, on the right
            var toggle = Switch(on);
            toggle.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(toggle, Dock.Right);
            line.Children.Add(toggle);
        }
        else if (row.IsOn is not null)
        {
            // one of several (sort order, status, screen mode): a radio button, drawn with Windows' own icon font
            line.Children.Add(new TextBlock
            {
                Text = on ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20,
                Foreground = selected ? Dark : on ? Accent : Muted,
                Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center
            });
        }

        line.Children.Add(new TextBlock
        {
            Text = row.Text, FontSize = 19, VerticalAlignment = VerticalAlignment.Center,
            Foreground = selected ? Dark : TextGrey, TextTrimming = TextTrimming.CharacterEllipsis
        });

        var cell = new Border
        {
            Background = selected ? ThemeService.SelectBg : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 1, 0, 1),
            Child = line,
            Cursor = Cursors.Hand
        };

        cell.MouseEnter += (_, _) =>
        {
            if (PointerIsChoosing) Now().Hover(index);   // (not rows that scroll under a hidden pointer)
        };
        cell.MouseLeftButtonUp += (_, _) => Now().Activate(index);

        return cell;
    }

    // ---------- LT / RT: a page up / down, wherever something is longer than the screen ----------
    // (the exceptions keep their own jobs: RT picks a random game on the row of games, and LT / RT move the text cursor in the
    // on-screen keyboard; anywhere else nothing scrolls, so the triggers do nothing)

    /// <summary>How many rows a trigger press moves through in a list (settings, filters, menus, the file browser).</summary>
    private const int PageRows = 6;

    /// <summary>LT = -1, RT = +1, anything else 0.</summary>
    private static int TriggerDirection(PadButton button) =>
        button switch { PadButton.LeftTrigger => -1, PadButton.RightTrigger => 1, _ => 0 };

    /// <summary>A page through a list: the one-row move done up to <see cref="PageRows"/> times, stopping at the end.</summary>
    private static void PageThrough(Func<int> index, Action<int> moveOne, int direction)
    {
        for (var i = 0; i < PageRows; i++)
        {
            var before = index();
            moveOne(direction);
            if (index() == before) break;   // (the first / last row)
        }
    }

    /// <summary>Scrolls a part of what is on screen (two thirds: a trigger press) down (+1) or up (-1). False when already at that end.</summary>
    private static bool ScrollPage(ScrollViewer scroll, int direction, double part = 2 / 3.0)
    {
        var before = scroll.VerticalOffset;
        if (direction > 0 ? before >= scroll.ScrollableHeight - 0.5 : before <= 0.5) return false;

        scroll.ScrollToVerticalOffset(before + direction * Math.Max(120, scroll.ViewportHeight * part));
        return true;
    }

    /// <summary>True when the list or page inside <paramref name="scroll"/> is longer than the screen (the "Scroll" hint shows then).</summary>
    private static bool Scrollable(ScrollViewer? scroll) => scroll is { ScrollableHeight: > 0.5 };

    /// <summary>The ScrollViewer around a panel's list.</summary>
    private static ScrollViewer? ScrollOf(DependencyObject list)
    {
        for (var parent = VisualTreeHelper.GetParent(list); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) return scroll;

        return null;
    }

    /// <summary>The "LT / RT  Scroll" hint, added when <paramref name="scrollable"/>.</summary>
    private void AddScrollHint(List<(string, string)> hints, bool scrollable)
    {
        QueueHintRefresh();   // (a list that was just filled isn't laid out yet: it is looked at again once it is)
        if (!scrollable) return;

        var l = Labels();
        hints.Insert(Math.Max(0, hints.Count - 1), ($"{l.LeftTrigger} / {l.RightTrigger}", "Scroll"));   // (before Back / Close)
    }

    private bool _hintsQueued;
    private bool _refreshingHints;

    /// <summary>
    /// Draws the hints of what is on screen again once the layout is done: whether a list is longer than the screen (the
    /// "Scroll" hint) is only known then. Called while hints are being drawn; the redraw itself doesn't queue another one.
    /// </summary>
    private void QueueHintRefresh()
    {
        if (_hintsQueued || _refreshingHints) return;

        _hintsQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _hintsQueued = false;
            if (_closing) return;

            _refreshingHints = true;
            try
            {
                if (_overlay == Overlay.None) BuildMainHints();
                else RefreshOpenPanelHints();
            }
            finally
            {
                _refreshingHints = false;
            }
        });
    }

    // ---------- menus on top of the screen ----------

    private void ShowOverlay(Overlay overlay)
    {
        _overlay = overlay;

        SearchOverlay.Visibility = overlay == Overlay.Search ? Visibility.Visible : Visibility.Collapsed;
        FilterOverlay.Visibility = overlay == Overlay.Filters ? Visibility.Visible : Visibility.Collapsed;
        PowerOverlay.Visibility = overlay == Overlay.Power ? Visibility.Visible : Visibility.Collapsed;
        DetailsOverlay.Visibility = overlay == Overlay.Details ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.Visibility = overlay == Overlay.Settings ? Visibility.Visible : Visibility.Collapsed;
        GameMenuOverlay.Visibility = overlay == Overlay.GameMenu ? Visibility.Visible : Visibility.Collapsed;
        BrowseOverlay.Visibility = overlay == Overlay.Browse ? Visibility.Visible : Visibility.Collapsed;
        ShotOverlay.Visibility = overlay == Overlay.Shots ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsOverlay.Visibility = overlay == Overlay.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseOverlay()
    {
        ShowOverlay(Overlay.None);
        UpdateDetails();
    }
}
