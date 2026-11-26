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
        if (!IsGamesTab) return;   // nothing to search on Stats or Media

        _noteGame = null;
        _caret = _query.Length;   // the cursor after what is typed already
        _shift = false;
        _caps = CapsMode.Off;
        AutoCapital();   // an empty search starts with a capital
        ShowOverlay(Overlay.Search);
        UpdateQueryUi();
        UpdateKeyboardSelection();
        BuildOverlayHints();
    }

    // the same keyboard types a game's note (Game menu > Notes…): _noteGame is set while that is going on
    private Game? _noteGame;
    private string _noteText = "";
    private const int NoteLimit = 300;

    private void OpenNote(Game game)
    {
        _noteGame = game;
        _noteText = game.Notes ?? "";
        _caret = _noteText.Length;
        _shift = false;
        _caps = CapsMode.Off;
        AutoCapital();
        ShowOverlay(Overlay.Search);
        UpdateQueryUi();
        UpdateKeyboardSelection();
        BuildOverlayHints();
    }

    /// <summary>Done (save the note) or Back (leave it as it was) while typing a note; closes the keyboard either way.</summary>
    private void FinishNote(bool save)
    {
        var game = _noteGame;
        var text = _noteText;
        _noteGame = null;

        if (save && game is not null)
            _gameActions.SetNotes(game, text);

        CloseOverlay();
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
                // (what a key shows is drawn by UpdateKeyboardSelection: it changes with the case, Shift and the controller)
                var cell = new Border
                {
                    Margin = new Thickness(4),
                    Height = 62,
                    CornerRadius = new CornerRadius(8),
                    Background = KeyBackground
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
                    PressKey(Rows[row0][col0]);
                };

                _keyCells[r][c] = cell;
                row.Children.Add(cell);
            }

            KeyRows.Children.Add(row);
        }

        UpdateKeyboardSelection();
    }

    // (named so the code above reads naturally)
    private static string[][] Rows => KeyLayout;

    // ---- capitals and second symbols ----
    // Caps: one press = the next letter is a capital, a second press = capitals stay on (Caps Lock), a third = off.
    // The keyboard starts with one capital (an empty box, and in a note the start of each sentence), then goes to lowercase.
    // Shift: the next key types its second symbol (1 → !, ' → "...), or a letter in the other case, as on a real keyboard.

    private enum CapsMode { Off, Once, Locked }

    private CapsMode _caps;
    private bool _shift;   // the next key only

    private bool Capitals => (_caps != CapsMode.Off) != _shift;   // (Shift during Caps Lock types one small letter)

    /// <summary>The second symbol of the keys that have one, as on a US keyboard.</summary>
    private static readonly Dictionary<string, string> ShiftedSymbols = new()
    {
        ["1"] = "!", ["2"] = "@", ["3"] = "#", ["4"] = "$", ["5"] = "%", ["6"] = "^", ["7"] = "&", ["8"] = "*", ["9"] = "(", ["0"] = ")",
        ["'"] = "\"", [","] = "<", ["."] = ">", ["-"] = "_"
    };

    private static bool IsLetterKey(string key) => key.Length == 1 && char.IsLetter(key[0]);

    /// <summary>What a letter or symbol key types right now (and shows big): the letter in its case, or the symbol Shift picks.</summary>
    private string KeyText(string key) =>
        IsLetterKey(key) ? (Capitals ? key.ToUpperInvariant() : key.ToLowerInvariant())
        : _shift && ShiftedSymbols.TryGetValue(key, out var second) ? second
        : key;

    /// <summary>
    /// A capital comes next where the cursor is at the start of the box, and in a note after the end of a sentence
    /// (never switches Caps off).
    /// </summary>
    private void AutoCapital()
    {
        if (_caps == CapsMode.Locked) return;

        var text = EditText[..Caret];   // (what comes before the cursor)
        var sentenceStart = text.Length == 0 ||
            (_noteGame is not null && (text.EndsWith(". ") || text.EndsWith("! ") || text.EndsWith("? ")));

        if (sentenceStart)
            _caps = CapsMode.Once;
    }

    /// <summary>Draws every key: the selected one, Shift / Caps lit while on, the letters' case and each key's controller button.</summary>
    private void UpdateKeyboardSelection()
    {
        // what every key's face depends on besides its own colors: a key is only drawn again when this or its colors changed
        // (moving the selection repaints two keys, not all fifty)
        var faces = (_shift, _caps, ShownKind, SettingsService.SwapConfirmBack);

        for (var r = 0; r < _keyCells.Length; r++)
        {
            for (var c = 0; c < _keyCells[r].Length; c++)
            {
                var key = Rows[r][c];
                var selected = r == _keyRow && c == _keyCol;
                var lit = key switch { "Shift" => _shift, "Caps" => _caps != CapsMode.Off, _ => false };
                var color = selected ? Dark : lit ? Brushes.White : ThemeService.TextPrimary;

                var cell = _keyCells[r][c];
                cell.Background = selected ? ThemeService.SelectBg : lit ? Purple : KeyBackground;

                var drawn = (faces, color);
                if (cell.Tag is { } before && before.Equals(drawn)) continue;

                cell.Tag = drawn;
                cell.Child = KeyContent(key, color);
            }
        }
    }

    /// <summary>
    /// A key's face: what it types (or its name), the button that does the same on the controller in use (X for Delete...),
    /// and on a key with two symbols the other one, small in the corner.
    /// </summary>
    private UIElement KeyContent(string key, Brush color)
    {
        var caption = key switch
        {
            "Caps" => _caps == CapsMode.Locked ? "Caps Lock" : "Caps",
            "⌫ Delete" => "Delete",   // (room for its button: "Backspace" on a keyboard)
            "Shift" or "Space" or "Clear" or "Done" => key,
            _ => KeyText(key)
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new TextBlock { Text = caption, Foreground = color, FontSize = 22, VerticalAlignment = VerticalAlignment.Center });

        var button = KeyButton(key);
        if (button is not null)
            line.Children.Add(ButtonCap(button, color));

        var face = new Grid();
        face.Children.Add(line);

        if (ShiftedSymbols.TryGetValue(key, out var second))
            face.Children.Add(new TextBlock
            {
                Text = _shift ? key : second, Foreground = color, Opacity = 0.55, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 5, 0, 0)
            });

        return face;
    }

    /// <summary>
    /// The controller button (or keyboard key) that does what this on-screen key does, for the device in use; null when there is
    /// none. The same buttons as in HandleSearch.
    /// </summary>
    private string? KeyButton(string key)
    {
        var l = Labels();

        if (ShownKind == ControllerKind.None)   // a real keyboard: only Delete, Space and Clear have a key of their own
            return key switch { "⌫ Delete" => "Backspace", "Space" => "Space", "Clear" => l.Select, _ => null };

        return key switch
        {
            "⌫ Delete" => l.X,
            "Space" => l.Y,
            "Clear" => l.Select,
            "Done" => MenuGlyph,   // the Menu / Options / + button: three lines (drawn in the icon font)
            "Shift" => l.Left,
            "Caps" => l.Right,
            _ => null
        };
    }

    private const string MenuGlyph = "\uE700";   // three lines, in Segoe MDL2 Assets

    /// <summary>A button's label drawn inside a key: an outlined circle for one character, an outlined pill for a word.</summary>
    private static Border ButtonCap(string label, Brush color)
    {
        var icon = label == MenuGlyph;
        var round = label.Length == 1;

        return new Border
        {
            BorderBrush = color, BorderThickness = new Thickness(1.5), Opacity = 0.8,
            Width = round ? 28 : double.NaN, Height = 28, CornerRadius = new CornerRadius(14),
            Padding = round ? new Thickness(0) : new Thickness(8, 0, 8, 0),
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label, Foreground = color, FontSize = icon ? 13 : round ? 14 : 13, FontWeight = FontWeights.SemiBold,
                FontFamily = icon ? new FontFamily("Segoe MDL2 Assets") : SystemFonts.MessageFontFamily,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private void HandleSearch(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: MoveKey(0, -1); break;
            case PadButton.Right: MoveKey(0, 1); break;
            case PadButton.Up: MoveKey(-1, 0); break;
            case PadButton.Down: MoveKey(1, 0); break;
            case PadButton.Confirm: PressKey(Rows[_keyRow][_keyCol]); break;
            // (the buttons drawn inside the keys, see KeyButton)
            case PadButton.X: PressKey("⌫ Delete"); break;
            case PadButton.Y: PressKey("Space"); break;
            case PadButton.LeftShoulder: PressKey("Shift"); break;
            case PadButton.RightShoulder: PressKey("Caps"); break;
            case PadButton.Start: PressKey("Done"); break;
            case PadButton.Select: PressKey("Clear"); break;   // (View / Share / −; Tab on a keyboard)
            case PadButton.LeftTrigger: MoveCaret(-1); break;   // the text cursor (PgUp / PgDn on a keyboard)
            case PadButton.RightTrigger: MoveCaret(1); break;
            case PadButton.Back: if (_noteGame is not null) FinishNote(save: false); else CloseOverlay(); break;
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

    /// <summary>Does what an on-screen key does (the selected one, or the one a controller button stands for).</summary>
    private void PressKey(string key)
    {
        switch (key)
        {
            case "Shift": _shift = !_shift; UpdateKeyboardSelection(); break;
            case "Caps":
                _caps = _caps switch { CapsMode.Off => CapsMode.Once, CapsMode.Once => CapsMode.Locked, _ => CapsMode.Off };
                UpdateKeyboardSelection();
                break;
            case "Space": TypeText(" "); break;
            case "⌫ Delete": Backspace(); break;
            case "Clear": SetEditText("", 0); break;
            case "Done": if (_noteGame is not null) FinishNote(save: true); else CloseOverlay(); break;
            default:
                var text = KeyText(key);
                _shift = false;   // (one key only)
                TypeText(text);
                break;
        }
    }

    /// <summary>The text changed: the box shows it, a capital comes next where a sentence starts, the keys show the case.</summary>
    private void AfterEdit()
    {
        UpdateQueryUi();
        AutoCapital();
        UpdateKeyboardSelection();
    }

    // ---- the text being typed (the search, or the note) and the cursor in it ----

    private int _caret;   // where the next letter goes: 0 = before the first one, the length = after the last

    private string EditText => _noteGame is not null ? _noteText : _query;

    private int Caret => Math.Clamp(_caret, 0, EditText.Length);   // (the search can be changed from elsewhere)

    /// <summary>Replaces the text being typed and puts the cursor at <paramref name="caret"/>; the list follows a search.</summary>
    private void SetEditText(string text, int caret)
    {
        _caret = caret;

        if (_noteGame is not null)
            _noteText = text;
        else
            SetQuery(text);

        AfterEdit();
    }

    /// <summary>The cursor one place left (-1) or right (+1), stopping at the ends.</summary>
    private void MoveCaret(int step)
    {
        _caret = Math.Clamp(Caret + step, 0, EditText.Length);
        AfterEdit();   // (a capital may come next at the new place)
    }

    /// <summary>Types at the cursor (a letter, a symbol, a space), within the box's length and without double spaces.</summary>
    private void TypeText(string text)
    {
        var (before, after) = (EditText[..Caret], EditText[Caret..]);

        if (EditText.Length + text.Length > (_noteGame is not null ? NoteLimit : 40)) return;
        if (text == " " && (before.Length == 0 || before.EndsWith(' ') || after.StartsWith(' '))) return;   // no leading or double spaces

        // a letter typed uses up a single Caps press: back to lowercase (a letter that didn't fit leaves it on)
        if (_caps == CapsMode.Once && text.Any(char.IsLetter))
            _caps = CapsMode.Off;

        SetEditText(before + text + after, before.Length + text.Length);
    }

    /// <summary>Deletes the letter before the cursor.</summary>
    private void Backspace()
    {
        if (Caret == 0) return;

        var caret = Caret;
        SetEditText(EditText.Remove(caret - 1, 1), caret - 1);
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
        ShowTextWithCaret();

        if (_noteGame is not null)
        {
            QueryHint.Text = "Type a note about " + _noteGame.DisplayName;
            QueryHint.Visibility = _noteText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultCountText.Text = $"{_noteText.Length} / {NoteLimit}";
            return;
        }

        QueryHint.Text = "Type to search your games";
        QueryHint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText.Text = _items.Count == 1 ? "1 game" : $"{_items.Count} games";
    }

    /// <summary>The text with a blinking bar at the cursor (made anew each time, so it shows solid right after a change).</summary>
    private void ShowTextWithCaret()
    {
        var caret = new Border { Width = 2.5, Height = 32, Background = ThemeService.TextPrimary, Margin = new Thickness(1, 0, 1, 0) };

        var blink = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.1), RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        blink.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(1, TimeSpan.Zero));
        blink.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(0, TimeSpan.FromSeconds(0.6)));
        caret.BeginAnimation(OpacityProperty, blink);

        QueryText.Inlines.Clear();
        QueryText.Inlines.Add(new System.Windows.Documents.Run(EditText[..Caret]));
        QueryText.Inlines.Add(new System.Windows.Documents.InlineUIContainer(caret) { BaselineAlignment = BaselineAlignment.Center });
        QueryText.Inlines.Add(new System.Windows.Documents.Run(EditText[Caret..]));
    }

    /// <summary>The buttons that move the cursor, shown in the text box: LT ← → RT (PgUp / PgDn on a keyboard).</summary>
    private void ShowCaretHints()
    {
        var l = Labels();
        var color = ThemeService.TextPrimary;

        CaretHints.Children.Clear();
        CaretHints.Children.Add(ButtonCap(l.LeftTrigger, color));
        CaretHints.Children.Add(new TextBlock
        {
            Text = "←  →", Foreground = Muted, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        });
        CaretHints.Children.Add(ButtonCap(l.RightTrigger, color));
    }

    // ---------- filters ----------

    private void OpenFilters()
    {
        if (!IsGamesTab) return;   // (Stats and Media have nothing to filter)

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
            rows.Add(new FilterRow("None yet. Open a game's menu, then Categories › New category…"));

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

        rows.Add(new FilterRow("SMART FILTERS", IsHeader: true));
        foreach (var smart in Enum.GetValues<SmartFilter>())
        {
            var chosen = smart;
            rows.Add(new FilterRow(chosen.Label(), IsOn: () => _smartFilters.Contains(chosen),
                                   Activate: () => Toggle(_smartFilters, chosen)));
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
        _smartFilters.Clear();
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

            if (row.Steps is not null)
            {
                // a stepped slider: one segment per step on the right, filled up to the current one
                var (current, count) = row.Steps();
                var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
                for (var step = 0; step < count; step++)
                {
                    bar.Children.Add(new Border
                    {
                        Width = 34, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(5, 0, 0, 0),
                        Background = step <= current ? (selected ? Dark : Purple) : Muted,
                        Opacity = step <= current ? 1 : 0.5
                    });
                }

                DockPanel.SetDock(bar, Dock.Right);
                line.Children.Add(bar);
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
        ShotOverlay.Visibility = overlay == Overlay.Shots ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsOverlay.Visibility = overlay == Overlay.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseOverlay()
    {
        ShowOverlay(Overlay.None);
        UpdateDetails();
    }
}
