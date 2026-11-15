using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- search (on-screen keyboard) ----------

    private void OpenSearch()
    {
        if (_tab == StatsTab) return;   // nothing to search there

        ShowOverlay(Overlay.Search);
        UpdateQueryUi();
        UpdateKeyboardSelection();
        BuildOverlayHints();
    }

    private void BuildKeyboard()
    {
        KeyRows.Children.Clear();
        _keyCells = new Border[Rows.Length][];

        for (var r = 0; r < Rows.Length; r++)
        {
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
            _keyCells[r] = new Border[Rows[r].Length];

            for (var c = 0; c < Rows[r].Length; c++)
            {
                var cell = new Border
                {
                    Margin = new Thickness(4),
                    Padding = new Thickness(0, 14, 0, 14),
                    CornerRadius = new CornerRadius(8),
                    Background = KeyBackground,
                    Child = new TextBlock
                    {
                        Text = Rows[r][c], Foreground = ThemeService.TextPrimary, FontSize = 22,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };

                var (row0, col0) = (r, c);
                cell.MouseEnter += (_, _) =>
                {
                    if (!PointerIsChoosing || (_keyRow == row0 && _keyCol == col0)) return;

                    _keyRow = row0;   // the mouse selects the key it points at
                    _keyCol = col0;
                    UpdateKeyboardSelection();
                };

                cell.MouseLeftButtonUp += (_, _) =>
                {
                    _keyRow = row0;
                    _keyCol = col0;
                    UpdateKeyboardSelection();
                    PressKey();
                };

                _keyCells[r][c] = cell;
                row.Children.Add(cell);
            }

            KeyRows.Children.Add(row);
        }
    }

    // (named so the code above reads naturally)
    private static string[][] Rows => KeyLayout;

    private void UpdateKeyboardSelection()
    {
        for (var r = 0; r < _keyCells.Length; r++)
        {
            for (var c = 0; c < _keyCells[r].Length; c++)
            {
                var selected = r == _keyRow && c == _keyCol;
                var cell = _keyCells[r][c];

                cell.Background = selected ? ThemeService.SelectBg : KeyBackground;
                ((TextBlock)cell.Child).Foreground = selected ? Dark : ThemeService.TextPrimary;
            }
        }
    }

    private void HandleSearch(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: MoveKey(0, -1); break;
            case PadButton.Right: MoveKey(0, 1); break;
            case PadButton.Up: MoveKey(-1, 0); break;
            case PadButton.Down: MoveKey(1, 0); break;
            case PadButton.Confirm: PressKey(); break;
            case PadButton.X: Backspace(); break;
            case PadButton.Y: TypeText(" "); break;
            case PadButton.Back or PadButton.Start: CloseOverlay(); break;
        }
    }

    private void MoveKey(int rowStep, int colStep)
    {
        if (rowStep != 0)
        {
            var newRow = Math.Clamp(_keyRow + rowStep, 0, Rows.Length - 1);

            // keep roughly the same horizontal position when the next row has a different number of keys
            var oldLast = Math.Max(1, Rows[_keyRow].Length - 1);
            var newLast = Rows[newRow].Length - 1;
            _keyCol = (int)Math.Round((double)_keyCol / oldLast * newLast);
            _keyRow = newRow;
        }

        if (colStep != 0)
        {
            var length = Rows[_keyRow].Length;
            _keyCol = (_keyCol + colStep + length) % length;
        }

        UpdateKeyboardSelection();
    }

    private void PressKey()
    {
        var key = Rows[_keyRow][_keyCol];

        switch (key)
        {
            case "Space": TypeText(" "); break;
            case "⌫ Delete": Backspace(); break;
            case "Clear": SetQuery(""); break;
            case "Done": CloseOverlay(); break;
            default: TypeText(key.ToLowerInvariant()); break;
        }
    }

    private void TypeText(string text)
    {
        if (_query.Length + text.Length > 40) return;
        if (text == " " && (_query.Length == 0 || _query.EndsWith(' '))) return;   // no leading or double spaces

        SetQuery(_query + text);
    }

    private void Backspace()
    {
        if (_query.Length > 0)
            SetQuery(_query[..^1]);
    }

    /// <summary>The list follows the search as it is typed.</summary>
    private void SetQuery(string query)
    {
        _query = query;
        ApplyTab(preserveSelection: false);
        UpdateQueryUi();
    }

    private void UpdateQueryUi()
    {
        QueryText.Text = _query;
        QueryHint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText.Text = _items.Count == 1 ? "1 game" : $"{_items.Count} games";
    }

    // ---------- filters ----------

    private void OpenFilters()
    {
        if (_tab == StatsTab) return;

        _filterIndex = 0;
        ShowOverlay(Overlay.Filters);
        RebuildFilters();
        BuildOverlayHints();
    }

    /// <summary>Lists the launchers, categories and statuses that exist in the library, then the sort orders.</summary>
    private void RebuildFilters()
    {
        var rows = new List<FilterRow> { new("Clear all filters", Activate: ClearFilters) };

        var launchers = _library.Select(g => g.Source).Append(ManualSource).Distinct()   // "Other" is always offered
            .OrderBy(s => s == ManualSource).ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (launchers.Count > 0)
        {
            rows.Add(new FilterRow("LAUNCHER", IsHeader: true));
            foreach (var launcher in launchers)
            {
                var chosen = launcher;
                rows.Add(new FilterRow(LauncherName(chosen), IsOn: () => _launcherFilters.Contains(chosen),
                                       Activate: () => Toggle(_launcherFilters, chosen)));
            }
        }

        var categories = _library.SelectMany(g => g.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();

        rows.Add(new FilterRow("CATEGORY", IsHeader: true));
        if (categories.Count == 0)
            rows.Add(new FilterRow("None yet. Edit a game from its menu to add some."));

        foreach (var category in categories)
        {
            var chosen = category;
            rows.Add(new FilterRow(chosen, IsOn: () => _categoryFilters.Contains(chosen),
                                   Activate: () => Toggle(_categoryFilters, chosen)));
        }

        rows.Add(new FilterRow("STATUS", IsHeader: true));
        foreach (var status in Enum.GetValues<GameStatus>().Where(s => s != GameStatus.None))
        {
            var chosen = status;
            rows.Add(new FilterRow(chosen.Label(), IsOn: () => _statusFilters.Contains(chosen),
                                   Activate: () => Toggle(_statusFilters, chosen)));
        }

        rows.Add(new FilterRow("SHOW", IsHeader: true));
        rows.Add(new FilterRow("Hidden games", IsOn: () => _showHidden, Activate: () => _showHidden = !_showHidden));

        rows.Add(new FilterRow("SORT BY", IsHeader: true));
        foreach (var (text, mode) in new[]
                 {
                     ("Name (A–Z)", SortMode.Name), ("Most played", SortMode.Playtime),
                     ("Recently played", SortMode.LastPlayed), ("Launcher", SortMode.Source)
                 })
        {
            var chosen = mode;
            rows.Add(new FilterRow(text, IsOn: () => _sort == chosen, Activate: () => { _sort = chosen; SettingsService.SortMode = chosen; }, IsRadio: true));
        }

        _filterRows = rows;
        if (_filterRows[_filterIndex].IsHeader)
            _filterIndex = 0;

        RenderFilters();
    }

    private static void Toggle<T>(HashSet<T> set, T value)
    {
        if (!set.Remove(value))
            set.Add(value);
    }

    private void ClearFilters()
    {
        _launcherFilters.Clear();
        _categoryFilters.Clear();
        _statusFilters.Clear();
        _showHidden = false;
    }

    private void RenderFilters() =>
        _filterCells = RenderRows(FilterList, _filterRows, _filterIndex, index =>
        {
            _filterIndex = index;
            ActivateFilterRow();
        }, index =>
        {
            if (_filterIndex == index) return;

            _filterIndex = index;
            RenderOnHover(RenderFilters);
        });

    private bool _renderingOnHover;

    /// <summary>Redraws a list because the mouse moved onto another row: the list must not scroll while that happens.</summary>
    private void RenderOnHover(Action render)
    {
        _renderingOnHover = true;
        try { render(); }
        finally { _renderingOnHover = false; }
    }

    /// <summary>Draws a list of headings, tick boxes, radio buttons and actions (the filters and the settings share it).</summary>
    /// <returns>One cell per row (an empty one for a heading), so the indexes line up with <paramref name="rows"/>.</returns>
    private List<Border> RenderRows(StackPanel panel, List<FilterRow> rows, int selectedIndex, Action<int> activate,
                                    Action<int> hover)
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

        return cells;
    }

    private void HandleFilters(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: MoveFilter(-1); break;
            case PadButton.Down: MoveFilter(1); break;
            case PadButton.Confirm: ActivateFilterRow(); break;
            case PadButton.Back or PadButton.Select: CloseOverlay(); break;
        }
    }

    private void MoveFilter(int step)
    {
        var next = _filterIndex;

        do
        {
            next += step;
            if (next < 0 || next >= _filterRows.Count) return;   // at the end: stay where we are
        }
        while (_filterRows[next].IsHeader);

        _filterIndex = next;
        RenderFilters();
    }

    private void ActivateFilterRow()
    {
        _filterRows[_filterIndex].Activate?.Invoke();

        RebuildFilters();                       // ticks change
        ApplyTab(preserveSelection: false);     // the list behind the panel follows
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
    }

    private void CloseOverlay()
    {
        ShowOverlay(Overlay.None);
        UpdateDetails();
    }
}
