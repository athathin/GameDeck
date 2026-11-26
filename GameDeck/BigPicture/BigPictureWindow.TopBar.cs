using System.Windows.Controls;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the icons at the top, reachable with the stick ----------

    private bool _topFocus;
    private int _topIndex = 3;   // Search: the status buttons (Wi-Fi, Bluetooth, sound) are to its left

    private Border[]? _topIcons;

    private Border[] TopIcons => _topIcons ??=
        [WifiButton, BluetoothButton, SoundButton, SearchButton, FilterButton, SettingsButton, PowerButton];

    private void FocusTopBar(bool on)
    {
        _topFocus = on;
        if (on)
        {
            _topIndex = Math.Clamp(_topIndex, 0, TopIcons.Length - 1);
            _stripFocus = false;
            ContinueList.SelectedIndex = -1;
        }

        // the selected tile has no ring while an icon is the selected one (two rings look like two selections)
        HideTileRing(on);
        if (_tab == MediaTab)
            UpdateMediaSelection(scroll: false);

        UpdateTopBar();
        BuildMainHints();
    }

    /// <summary>Selected icon: white with a dark symbol, like the selected key or row elsewhere in this screen.</summary>
    private void UpdateTopBar()
    {
        var icons = TopIcons;
        for (var i = 0; i < icons.Length; i++)
        {
            var selected = _topFocus && i == _topIndex;
            var glyph = (TextBlock)icons[i].Child;

            if (selected)
            {
                icons[i].Background = ThemeService.SelectBg;
                glyph.Foreground = Dark;
            }
            else
            {
                icons[i].ClearValue(BackgroundProperty);   // back to the style's colors (including the mouse hover)
                glyph.Foreground = ThemeService.TextPrimary;
            }
        }
    }

    /// <summary>Returns true when the button was used up here.</summary>
    private bool HandleTopBar(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: _topIndex = Math.Max(0, _topIndex - 1); UpdateTopBar(); return true;
            case PadButton.Right: _topIndex = Math.Min(TopIcons.Length - 1, _topIndex + 1); UpdateTopBar(); return true;
            case PadButton.Up: return true;
            case PadButton.Down when StripAvailable: FocusStrip(true); return true;   // on to the row of last played games
            case PadButton.Down or PadButton.Back: FocusTopBar(false); return true;   // back to the games

            case PadButton.Confirm:
                var icon = TopIcons[_topIndex];
                if (icon == WifiButton) OpenWifi();
                else if (icon == BluetoothButton) OpenBluetooth();
                else if (icon == SoundButton) OpenSoundOutputs();
                else if (icon == SearchButton) OpenSearch();
                else if (icon == FilterButton) OpenFilters();
                else if (icon == SettingsButton) OpenSettings();
                else OpenPower();
                return true;

            default: return false;   // tabs, Home, favorite... keep working from up here
        }
    }
}
