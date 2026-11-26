using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- showing the library ----------

    /// <param name="reapplyUserData">False when only the search text changed: nothing the user saved is different, so the
    /// favorites, names, categories... don't have to be copied onto every game again for each typed letter.</param>
    private void Refresh(bool reapplyUserData = true)
    {
        if (reapplyUserData)
        {
            _userData.RecordAddedDates(_library.Games);   // games that just showed up get today's date ("Recently added")

            foreach (var game in _library.Games)
            {
                _userData.Apply(game);
                game.Genres = GenreService.For(game);
            }

            DiskUsage.ApplySaved(_library.Games);   // the sizes measured before (new games are measured in the background)
        }

        // a launcher / category that no longer has any games (after a scan) stops being a filter
        _sourceFilters.RemoveWhere(source => source != ManualSource && !_library.Games.Any(g => g.Source == source));
        _categoryFilters.RemoveWhere(category => !_library.Games.Any(g => HasTag(g, category)));
        _genreFilters.RemoveWhere(genre => !_library.Games.Any(g => g.Genres.Contains(genre, StringComparer.CurrentCultureIgnoreCase)));
        UpdateFilterButtons();

        var stats = _view == "Stats";
        var media = _view == "Media";
        GameList.Visibility = stats || media ? Visibility.Collapsed : Visibility.Visible;
        StatsPanel.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = stats || media ? Visibility.Collapsed : Visibility.Visible;

        if (media)
        {
            UpdateContinue(false);
            EmptyText.Visibility = Visibility.Collapsed;
            if (!_mediaLoaded)
                StatusText.Text = "";
            ShowMedia();   // (only reads the folders when the tab is opened, not on every refresh)
            return;
        }

        ClearMedia();   // left the tab: let go of its pictures

        if (stats)
        {
            UpdateContinue(false);
            ShowStats();
            EmptyText.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{_library.Games.Count} games";
            return;
        }

        var query = SearchBox.Text.Trim();

        IEnumerable<Game> shown = _library.Games;
        if (!_showHidden)
            shown = shown.Where(g => !g.IsHidden);
        if (_sourceFilters.Count > 0)
            shown = shown.Where(g => _sourceFilters.Contains(g.Source));
        if (_categoryFilters.Count > 0)
            shown = shown.Where(g => g.Tags.Any(_categoryFilters.Contains));
        if (_statusFilters.Count > 0)
            shown = shown.Where(g => _statusFilters.Contains(g.Status));
        if (_smartFilters.Count > 0)
            shown = shown.Where(g => _smartFilters.Any(f => f.Matches(g)));
        if (_genreFilters.Count > 0)
            shown = shown.Where(g => g.Genres.Any(_genreFilters.Contains));
        if (query.Length > 0)
            shown = shown.Where(g => g.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                     g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        shown = _view switch
        {
            "Favorites" => Sort(shown.Where(g => g.IsFavorite)),
            "Recent" => shown.Where(g => g.LastPlayed is not null)
                             .OrderByDescending(g => g.LastPlayed)
                             .Take(20),
            _ => Sort(shown)
        };

        var list = shown.ToList();
        var realCount = list.Count;
        if (realCount > 0)
            list.Insert(0, RandomTile);   // the "pick a game for me" tile comes first
        SyncGames(list);

        StatusText.Text = _view switch
        {
            "Favorites" => $"{realCount} favorites",
            "Recent" => $"{realCount} recent",
            _ => $"{realCount} games"
        };

        var filtered = query.Length > 0 || _sourceFilters.Count > 0 || _categoryFilters.Count > 0 || _statusFilters.Count > 0 ||
                       _smartFilters.Count > 0 || _genreFilters.Count > 0;
        EmptyText.Text = filtered
            ? "No games found."
            : _view switch
            {
                "Favorites" => "No favorites yet. Right-click a game and choose \"Add to favorites\".",
                "Recent" => "Nothing played yet. Games you launch from GameDeck show up here.",
                _ => "No games found. Open Settings (the gear) and choose \"Scan for games\" or \"Add games\"."
            };
        EmptyText.Visibility = realCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateContinue(_view is not ("Favorites" or "Recent") && !filtered && !_showHidden);
    }

    private IOrderedEnumerable<Game> Sort(IEnumerable<Game> games) => _sort switch
    {
        SortMode.Playtime => games.OrderByDescending(g => g.PlaytimeSeconds).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.LastPlayed => games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.Source => games.OrderBy(g => g.Source, StringComparer.CurrentCultureIgnoreCase).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.Size => games.OrderByDescending(g => g.SizeBytes ?? -1).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        _ => games.OrderBy(ByName, StringComparer.CurrentCultureIgnoreCase)
    };

    private static string ByName(Game game) => game.DisplayName;

    private static bool HasTag(Game game, string tag) =>
        game.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Makes Games match the list with as few changes as possible. Clearing and refilling the collection would throw
    /// every tile away (and its cover) each time you type a letter in the search box.
    /// </summary>
    private void SyncGames(List<Game> target)
    {
        var wanted = new HashSet<Game>(target);

        for (var i = Games.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Games[i]))
                Games.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Games.Count && ReferenceEquals(Games[i], target[i])) continue;

            var current = Games.IndexOf(target[i]);
            if (current >= 0)
                Games.Move(current, i);
            else
                Games.Insert(i, target[i]);
        }
    }

    // ---------- covers: only tiles on screen keep their cover in memory ----------

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game { IsRandomTile: false } game) return;

        game.CoverWanted = true;
        if (game.Cover is null)
            _ = CoverService.LoadCoverAsync(game);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game { IsRandomTile: false } game) return;
        if (_continueGames.Contains(game)) return;   // the Continue playing row still shows it

        game.CoverWanted = false;
        game.Cover = null;   // scrolled away (or filtered out): free the image, it reloads from disk when needed
    }

    private Task LoadVisibleCoversAsync() =>
        Task.WhenAll(Games.Where(g => g.CoverWanted && g.Cover is null).Select(CoverService.LoadCoverAsync));

    // ---------- search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            Refresh(reapplyUserData: false);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        SearchBox.Clear();
        Keyboard.ClearFocus();
    }

    private void ClearSearch_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Clear();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F11 opens Big Picture mode from anywhere in the window (not while typing a new quick launch shortcut)
        if (e.Key == Key.F11 && !SettingsPanel.IsCapturingShortcut)
        {
            OpenBigPicture();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && FilterPopup.IsOpen)
        {
            FilterPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        // On a game's page Esc goes back, nothing else applies
        if (DetailsPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseDetails();
                e.Handled = true;
            }
            return;
        }

        // On the settings screen Esc goes back, nothing else applies
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            // while a new shortcut is being captured, Esc only cancels that
            if (e.Key == Key.Escape && !SettingsPanel.IsCapturingShortcut)
            {
                CloseSettings();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+F jumps to the search box
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_view is "Stats" or "Media")
                AllTab.IsChecked = true;

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ViewTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string view }) return;

        _view = view;
        Refresh();
    }

    // ---------- sort and filters ----------

    private void UpdateFilterButtons()
    {
        // the icon turns purple while a launcher / category filter or "show hidden" is on
        var active = HasActiveFilters;
        FilterButton.Foreground = active ? ThemeService.Accent : ThemeService.TextPrimary;
        UpdateFilterChips();
    }

    private void UpdateTileSizeButton()
    {
        if (TileSlider is null) return;

        TileSlider.Value = SettingsService.StepIndex(SettingsService.TileScales, TileScale);
        TileSlider.ToolTip = $"Tile size: {FormatScale(TileScale)}×";
    }

    private void TileSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;   // ignore the slider's own start-up values

        TileScale = SettingsService.StepAt(SettingsService.TileScales, e.NewValue);
    }

    private static string FormatScale(double scale) => scale.ToString("0.##", CultureInfo.InvariantCulture);

    private static MenuItem MenuEntry(string header, bool isChecked, Action onClick, bool enabled = true)
    {
        // a TextBlock header, so a "_" in a name (a category called "Co_op") isn't swallowed as a keyboard shortcut marker
        var item = new MenuItem { Header = new TextBlock { Text = header }, IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private bool HasActiveFilters => _sourceFilters.Count > 0 || _categoryFilters.Count > 0 || _statusFilters.Count > 0 ||
                                     _smartFilters.Count > 0 || _genreFilters.Count > 0 || _showHidden;

    /// <summary>The filter icon: opens the Library Filters panel. (While it's open the icon can't be clicked, so clicking it closes the panel.)</summary>
    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        BuildFilterPanel();

        // the panel is wider than the icon: line up its right edge with the icon
        FilterPopup.HorizontalOffset = FilterButton.ActualWidth - 540 - 14;
        FilterPopup.IsOpen = true;
    }

    // ---------- filter chips (in the search box) ----------

    /// <summary>Games added with "Add games" have the source "Manual"; the filters call them "Other".</summary>
    private const string ManualSource = "Manual";

    private static string LauncherName(string source) => source == ManualSource ? "Other" : source;

    private void UpdateFilterChips()
    {
        FilterChips.Children.Clear();

        foreach (var source in _sourceFilters.OrderBy(s => s == ManualSource).ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList())
            AddFilterChip(LauncherName(source), () => _sourceFilters.Remove(source));

        foreach (var category in _categoryFilters.OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList())
            AddFilterChip(category, () => _categoryFilters.Remove(category));

        foreach (var status in _statusFilters.OrderBy(s => s).ToList())
            AddFilterChip(status.Label(), () => _statusFilters.Remove(status));

        foreach (var smart in _smartFilters.OrderBy(s => s).ToList())
            AddFilterChip(smart.Label(), () => _smartFilters.Remove(smart));

        foreach (var genre in _genreFilters.OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList())
            AddFilterChip(genre, () => _genreFilters.Remove(genre));

        if (_showHidden)
            AddFilterChip("Hidden games", () => _showHidden = false);

        var any = FilterChips.Children.Count > 0;
        FilterChips.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearAllFiltersButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFilterChip(string text, Action remove)
    {
        var close = new TextBlock
        {
            Text = "✕",
            Foreground = Brushes.White,
            FontSize = 11,
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Remove this filter"
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            remove();
            FiltersChanged();
        };

        FilterChips.Children.Add(new Border
        {
            Background = ThemeService.Accent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 },
                    close
                }
            }
        });
    }

    /// <summary>After a filter was changed from outside the panel (a chip's x): refresh the list, and the panel if it's open.</summary>
    private void FiltersChanged()
    {
        if (FilterPopup.IsOpen)
            BuildFilterPanel();

        Refresh();
    }

    /// <summary>The x next to the search box: every launcher, every category, hidden games not shown. The sort order stays.</summary>
    private void ClearAllFilters_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        _sourceFilters.Clear();
        _categoryFilters.Clear();
        _statusFilters.Clear();
        _smartFilters.Clear();
        _genreFilters.Clear();
        _showHidden = false;
        FiltersChanged();
    }

    /// <summary>Fills the panel with the launchers and categories that exist right now, ticked as currently chosen.</summary>
    private void BuildFilterPanel()
    {
        // launchers (pick any number: games from any ticked launcher are shown)
        LauncherList.Children.Clear();
        foreach (var source in _library.Games.Select(g => g.Source).Append(ManualSource).Distinct()   // "Other" is always offered
                     .OrderBy(s => s == ManualSource)                       // and goes last
                     .ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var chosen = source;
            LauncherList.Children.Add(FilterCheckBox(LauncherName(chosen), _sourceFilters.Contains(chosen), on =>
            {
                if (on) _sourceFilters.Add(chosen); else _sourceFilters.Remove(chosen);
                Refresh();
            }));
        }

        // categories
        CategoryList.Children.Clear();
        var categories = AllCategories();
        if (categories.Count == 0)
        {
            CategoryList.Children.Add(new TextBlock
            {
                Text = "None yet. Right-click a game, then Categories › New category…",
                Foreground = ThemeService.TextMuted,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (var category in categories)
        {
            var chosen = category;
            CategoryList.Children.Add(FilterCheckBox(chosen, _categoryFilters.Contains(chosen), on =>
            {
                if (on) _categoryFilters.Add(chosen); else _categoryFilters.Remove(chosen);
                Refresh();
            }));
        }

        // status (Playing / Completed / ...)
        StatusList.Children.Clear();
        foreach (var status in Enum.GetValues<GameStatus>().Where(s => s != GameStatus.None))
        {
            var chosen = status;
            StatusList.Children.Add(FilterCheckBox(chosen.Label(), _statusFilters.Contains(chosen), on =>
            {
                if (on) _statusFilters.Add(chosen); else _statusFilters.Remove(chosen);
                Refresh();
            }));
        }

        // genres (Steam games: from the Steam store, see GenreService)
        GenreList.Children.Clear();
        var genres = _library.Games.SelectMany(g => g.Genres).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (genres.Count == 0)
        {
            GenreList.Children.Add(new TextBlock
            {
                Text = SettingsService.OnlineCovers
                    ? "None yet. Games get their genres from the Steam store in the background (games that aren't on Steam get none)."
                    : "Games get their genres from the Steam store while \"Download cover art from the internet\" is on.",
                Foreground = ThemeService.TextMuted,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (var genre in genres)
        {
            var chosen = genre;
            GenreList.Children.Add(FilterCheckBox(chosen, _genreFilters.Contains(chosen), on =>
            {
                if (on) _genreFilters.Add(chosen); else _genreFilters.Remove(chosen);
                Refresh();
            }));
        }

        // smart filters (Never played, Played under 1 hour, Recently added)
        SmartList.Children.Clear();
        foreach (var smart in Enum.GetValues<SmartFilter>())
        {
            var chosen = smart;
            SmartList.Children.Add(FilterCheckBox(chosen.Label(), _smartFilters.Contains(chosen), on =>
            {
                if (on) _smartFilters.Add(chosen); else _smartFilters.Remove(chosen);
                Refresh();
            }));
        }

        _buildingFilterPanel = true;
        HiddenCheckBox.IsChecked = _showHidden;
        _buildingFilterPanel = false;

        // sort order (one of four)
        SortList.Children.Clear();
        void AddSort(string text, SortMode mode)
        {
            var radio = new RadioButton
            {
                Content = new TextBlock { Text = text },
                GroupName = "LibrarySort",
                IsChecked = _sort == mode,
                Style = (Style)FindResource("DarkRadioButton"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            radio.Checked += (_, _) =>   // attached after IsChecked was set, so building the panel doesn't trigger it
            {
                _sort = mode;
                SettingsService.SortMode = mode;
                Refresh();
            };
            SortList.Children.Add(radio);
        }
        AddSort("Name (A–Z)", SortMode.Name);
        AddSort("Most played", SortMode.Playtime);
        AddSort("Recently played", SortMode.LastPlayed);
        AddSort("Launcher", SortMode.Source);
        AddSort("Largest first", SortMode.Size);
    }

    private CheckBox FilterCheckBox(string text, bool isChecked, Action<bool> changed)
    {
        // a TextBlock as content, so a "_" in a name (a category called "Co_op") isn't swallowed as a shortcut marker
        var box = new CheckBox
        {
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
            IsChecked = isChecked,
            Style = (Style)FindResource("DarkCheckBox"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        // Checked / Unchecked (not Click) so keyboard and screen readers work too; attached after IsChecked was set
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private bool _buildingFilterPanel;   // true while the code (not the user) sets the panel's boxes

    private void HiddenCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_buildingFilterPanel) return;

        _showHidden = HiddenCheckBox.IsChecked == true;
        Refresh();
    }
}
