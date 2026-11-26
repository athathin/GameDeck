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

    /// <summary>Draws a list of headings, tick boxes, radio buttons and actions (the filters, the game menu and the file browser share it).</summary>
    private void RenderRows(StackPanel panel, List<FilterRow> rows, int selectedIndex, Action<int> activate, Action<int> hover)
    {
        panel.Children.Clear();
        var cells = new List<Border>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.IsHeader)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = row.Text, Foreground = Muted, FontSize = 14,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 22, 0, 8)
                });
                cells.Add(new Border());   // keeps the indexes lined up with the rows
                continue;
            }

            var selected = i == selectedIndex;
            var on = row.IsOn?.Invoke() == true;

            var line = new DockPanel();
            if (row.IsOn is not null)
            {
                // tick box / radio button, drawn with Windows' own icon font
                var glyph = row.IsRadio ? (on ? "" : "") : (on ? "" : "");
                line.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20,
                    Foreground = selected ? Dark : on ? Purple : Muted,
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

            var index = i;
            cell.MouseEnter += (_, _) =>
            {
                if (PointerIsChoosing) hover(index);   // (not rows that scroll under a hidden pointer)
            };
            cell.MouseLeftButtonUp += (_, _) => activate(index);

            cells.Add(cell);
            panel.Children.Add(cell);
        }

        // keep the selected row on screen when the panel is long
        // (not when the mouse chose the row: scrolling under the pointer would make the next row the one it points at)
        if (selectedIndex < cells.Count && !_renderingOnHover)
            cells[selectedIndex].BringIntoView();
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
