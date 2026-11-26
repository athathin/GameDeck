using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- tabs and the list ----------

    private void SwitchTab(int step)
    {
        _tab = (_tab + step + TabNames.Length) % TabNames.Length;
        ApplyTab(preserveSelection: false);
    }

    private bool HasActiveFilters => _launcherFilters.Count > 0 || _categoryFilters.Count > 0 || _statusFilters.Count > 0 ||
                                     _smartFilters.Count > 0 || _genreFilters.Count > 0 || _showHidden || _showAddOns;

    private void ApplyTab(bool preserveSelection)
    {
        // the Stats tab shows numbers and charts instead of the row of games
        var stats = _tab == StatsTab;
        var media = _tab == MediaTab;
        Carousel.Visibility = stats || media ? Visibility.Collapsed : Visibility.Visible;
        StatsScroll.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        MediaScroll.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatsButton.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;

        if (!media)
            ClearMedia();   // left the Media tab: let go of its pictures

        if (stats || media)
        {
            _items.Clear();   // frees the covers too (the tiles unload)
            EmptyText.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Collapsed;
            FilterSummary.Visibility = Visibility.Collapsed;

            FocusStrip(false);
            UpdateContinue();   // (hides the row of last played games: it belongs to the All games list)

            if (stats)
            {
                BuildStats();
                StatsScroll.ScrollToTop();
            }
            else if (!_mediaLoaded)
            {
                MediaScroll.ScrollToTop();
                BuildMedia();   // (only when the tab is opened: a library reload keeps the pictures and the selection)
            }

            BuildTabs();
            BuildMainHints();
            return;
        }

        BuildMainHints();
        var keep = preserveSelection ? Carousel.SelectedItem as Game : null;

        // a launcher / category that no longer has any games (after a scan, an edit...) stops being a filter
        _launcherFilters.RemoveWhere(l => l != ManualSource && !_library.Any(g => g.Source == l));
        _categoryFilters.RemoveWhere(c => !_library.Any(g => g.Tags.Contains(c, StringComparer.CurrentCultureIgnoreCase)));
        _genreFilters.RemoveWhere(genre => !_library.Any(g => g.Genres.Contains(genre, StringComparer.CurrentCultureIgnoreCase)));

        // narrow the library down by the filters and the search, like the desktop library does
        IEnumerable<Game> source = _library;
        if (!_showHidden)
            source = source.Where(g => !g.IsHidden);
        if (!_showAddOns)
            source = source.Where(g => !g.IsAddOn);
        if (_launcherFilters.Count > 0)
            source = source.Where(g => _launcherFilters.Contains(g.Source));
        if (_categoryFilters.Count > 0)
            source = source.Where(g => g.Tags.Any(_categoryFilters.Contains));
        if (_statusFilters.Count > 0)
            source = source.Where(g => _statusFilters.Contains(g.Status));
        if (_smartFilters.Count > 0)
            source = source.Where(g => _smartFilters.Any(f => f.Matches(g)));
        if (_genreFilters.Count > 0)
            source = source.Where(g => g.Genres.Any(_genreFilters.Contains));
        if (_query.Length > 0)
            source = source.Where(g => g.DisplayName.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
                                       g.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase));

        IEnumerable<Game> list = _tab switch
        {
            1 => Sort(source.Where(g => g.IsFavorite)),
            2 => source.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed).Take(30),
            _ => Sort(source)
        };

        _items.Clear();
        foreach (var game in list)
            _items.Add(game);

        var narrowed = HasActiveFilters || _query.Length > 0;
        EmptyText.Text = narrowed
            ? "No games match your search or filters.\nPress the filters button to change them."
            : _tab switch
            {
                1 => "No favorites yet.\nPress the favorite button on a game to add it here.",
                2 => "Nothing played yet.\nGames you start from GameDeck show up here.",
                _ => "No games found.\nOpen GameDeck's settings and scan for games."
            };
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var index = keep is null ? 0 : Math.Max(0, _items.IndexOf(keep));
        Carousel.SelectedIndex = _items.Count == 0 ? -1 : index;

        UpdateFilterSummary();
        BuildTabs();
        UpdateDetails();

        FocusStrip(false);
        UpdateContinue();

        // a new list starts at its first tile straight away
        UpdateTarget();
        _scroll?.ScrollToHorizontalOffset(_targetOffset);
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

    private const string ManualSource = "Manual";   // games added by hand: shown as "Other"

    private static string LauncherName(string source) => source == ManualSource ? "Other" : source;

    /// <summary>A line under the tabs saying what the list is narrowed down by.</summary>
    private void UpdateFilterSummary()
    {
        var parts = new List<string>();
        if (_query.Length > 0)
            parts.Add($"Search \"{_query}\"");
        parts.AddRange(_launcherFilters.Select(LauncherName).Order());
        parts.AddRange(_categoryFilters.Order());
        parts.AddRange(_statusFilters.OrderBy(s => s).Select(s => s.Label()));
        parts.AddRange(_smartFilters.OrderBy(s => s).Select(s => s.Label()));
        parts.AddRange(_genreFilters.Order(StringComparer.CurrentCultureIgnoreCase));
        if (_showHidden)
            parts.Add("Hidden games");
        if (_showAddOns)
            parts.Add("DLC and soundtracks");

        FilterSummary.Text = "Showing: " + string.Join("  ·  ", parts);
        FilterSummary.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Carousel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetails();

        if (!_hoverSelecting)
            UpdateTarget();
    }

    /// <summary>Where to scroll so the selected tile sits in the middle of the screen.</summary>
    private void UpdateTarget()
    {
        if (Carousel.SelectedIndex < 0) return;

        var viewport = _scroll?.ViewportWidth is > 0 ? _scroll.ViewportWidth : Carousel.ActualWidth;
        var max = Math.Max(0, _items.Count * Pitch + 2 * EdgePad - viewport);

        _targetOffset = Math.Clamp(EdgePad + Carousel.SelectedIndex * Pitch + Pitch / 2 - viewport / 2, 0, max);
    }

    private void UpdateDetails()
    {
        if (Carousel.SelectedItem is not Game game)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailsPanel.Visibility = Visibility.Visible;
        NameText.Text = game.DisplayName;

        var parts = new List<string>
        {
            game.SourceText,
            game.PlaytimeSeconds > 0 ? $"{game.PlaytimeText} played" : "Not played yet"
        };

        if (game.LastPlayed is not null)
            parts.Add($"Last played {game.LastPlayedText}");
        if (game.HasStatus)
            parts.Add(game.StatusText);

        SubText.Text = string.Join("  ·  ", parts);
    }

    // ---------- the Stats tab ----------

    /// <summary>Reloads the play sessions (a game may have ended since) and redraws the numbers and charts.</summary>
    private void RefreshStats()
    {
        try { _sessions.Load(); }
        catch (System.IO.IOException) { }   // the file is being written right now: the numbers already shown stay

        var offset = StatsScroll.VerticalOffset;
        BuildStats();
        StatsScroll.ScrollToVerticalOffset(offset);
    }

    private void RefreshStatsButton_Click(object sender, MouseButtonEventArgs e) => RefreshStats();

    private void BuildStats()
    {
        var visible = _library.Where(g => !g.IsHidden).ToList();   // (hidden games stay out of the stats, as before)
        var played = visible.Where(g => g.PlaytimeSeconds > 0).OrderByDescending(g => g.PlaytimeSeconds).ToList();
        var total = played.Sum(g => g.PlaytimeSeconds);

        StatTotal.Text = total <= 0 ? "0 min" : Game.FormatPlaytime(total);
        StatPlayed.Text = played.Count.ToString();
        StatLibrary.Text = visible.Count(g => !g.IsAddOn).ToString();

        var week = _sessions.SecondsThisWeek();
        StatWeek.Text = week <= 0 ? "0 min" : week < 3600 ? $"{week / 60} min" : $"{week / 3600.0:0.#} h";

        var (current, longest) = _sessions.Streaks();
        StatStreak.Text = current == 1 ? "1 day" : $"{current} days";
        StatLongest.Text = longest == 1 ? "1 day" : $"{longest} days";

        var weekly = _sessions.ThisWeek(_hiddenKeys);
        WeekHeadline.Text = weekly.Headline;
        WeekCompare.Text = weekly.Compare;
        WeekLines.ItemsSource = weekly.Lines;

        var yearly = _sessions.ThisYear(_hiddenKeys);
        YearHeadline.Text = yearly.Headline;
        YearCompare.Text = yearly.Compare;
        YearLines.ItemsSource = yearly.Lines;

        var disk = DiskUsage.Summary(visible, DateTime.UtcNow);
        DiskPanel.Visibility = disk is null ? Visibility.Collapsed : Visibility.Visible;
        if (disk is not null)
        {
            DiskHeadline.Text = disk.Headline;
            DiskCompare.Text = disk.Compare;
            DiskLines.ItemsSource = disk.Lines;
        }

        DaysChart.ItemsSource = _sessions.LastDays(7);
        WeeksChart.ItemsSource = _sessions.LastWeeks(8);

        // most played: a bar per game, the longest one full width
        TopList.Children.Clear();
        NoPlaytimeText.Visibility = played.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var most = played.FirstOrDefault()?.PlaytimeSeconds ?? 1;
        foreach (var game in played.Take(5))
        {
            var fraction = Math.Clamp((double)game.PlaytimeSeconds / most, 0.02, 1);

            var bar = new Grid { Height = 12 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction + 0.0001, GridUnitType.Star) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Accent });

            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            row.Children.Add(new TextBlock
            {
                Text = game.DisplayName, Foreground = ThemeService.TextPrimary, FontSize = 18,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            });

            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var time = new TextBlock
            {
                Text = Game.FormatPlaytime(game.PlaytimeSeconds), Foreground = TextGrey,
                FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, 2);
            row.Children.Add(time);

            TopList.Children.Add(row);
        }

        // sessions of hidden games are left out, so their names don't show up here
        var recent = _sessions.Recent(8, excludeKeys: _hiddenKeys);
        SessionsList.ItemsSource = recent;
        NoSessionsText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- covers (same approach as the main window's tiles) ----------

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game game) return;

        _touchedCovers.TryAdd(game, game.CoverWanted);
        game.CoverWanted = true;

        if (game.Cover is null)
            _ = CoverService.LoadCoverAsync(game);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game game) return;

        // scrolled far away: free the picture, unless the main window's own tile still shows this game (or the home row does)
        if (!_continueItems.Any(i => i.Game == game) && _touchedCovers.Remove(game, out var mainWindowWantedIt) && !mainWindowWantedIt)
        {
            game.CoverWanted = false;
            game.Cover = null;
        }
    }
}
