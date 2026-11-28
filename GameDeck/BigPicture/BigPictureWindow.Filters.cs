using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
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

        var genres = _library.SelectMany(g => g.Genres).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (genres.Count > 0)   // (games get theirs from the Steam store: none yet, no heading)
        {
            rows.Add(new FilterRow("GENRE", IsHeader: true));
            foreach (var genre in genres)
            {
                var chosen = genre;
                rows.Add(new FilterRow(chosen, IsOn: () => _genreFilters.Contains(chosen), Activate: () => Toggle(_genreFilters, chosen)));
            }
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
        if (_showAddOns || _library.Any(g => g.IsAddOn))   // (only offered when Steam installed some as apps)
            rows.Add(new FilterRow("DLC and soundtracks", IsOn: () => _showAddOns, Activate: () => _showAddOns = !_showAddOns));

        rows.Add(new FilterRow("SORT BY", IsHeader: true));
        foreach (var (text, mode) in new[]
                 {
                     ("Name (A–Z)", SortMode.Name), ("Most played", SortMode.Playtime),
                     ("Recently played", SortMode.LastPlayed), ("Launcher", SortMode.Source), ("Largest first", SortMode.Size),
                     ("Highest rated", SortMode.Rating)
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
        _genreFilters.Clear();
        _showHidden = false;
        _showAddOns = false;
    }

    private void RenderFilters() =>
        RenderRows(FilterList, _filterRows, _filterIndex, index =>
        {
            _filterIndex = index;
            ActivateFilterRow();
        }, index =>
        {
            if (_filterIndex == index) return;

            _filterIndex = index;
            RenderOnHover(RenderFilters);
        });

    private void HandleFilters(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: MoveFilter(-1); break;
            case PadButton.Down: MoveFilter(1); break;
            case PadButton.LeftTrigger or PadButton.RightTrigger: PageThrough(() => _filterIndex, MoveFilter, TriggerDirection(button)); break;
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
}
