using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the status: the network, Bluetooth and the sound output (buttons at the top), the controller's battery ----------

    private static readonly SolidColorBrush Warning = Frozen(0xF0, 0x7A, 0x5A);   // a battery that is nearly empty

    private enum NetworkKind { None, Wifi, Wired }

    /// <summary>Called once when the screen opens: the network is watched (Windows says when it changes), the battery read.</summary>
    private void StartStatus()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        UpdateNetwork();
        UpdateBattery();
    }

    private void StopStatus()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }

    // (Windows tells on a background thread)
    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateNetwork);

    /// <summary>The Wi-Fi button shows the connection: Wi-Fi, a cable, or none (dimmed).</summary>
    private void UpdateNetwork()
    {
        if (_closing) return;

        var kind = CurrentNetwork();
        WifiGlyph.Text = kind == NetworkKind.Wired ? "" : "";
        WifiGlyph.Opacity = kind == NetworkKind.None ? 0.4 : 1;
        WifiButton.ToolTip = kind switch
        {
            NetworkKind.Wifi => "Connected by Wi-Fi (choose a network)",
            NetworkKind.Wired => "Connected by cable (Wi-Fi networks)",
            _ => "Not connected (choose a Wi-Fi network)"
        };
    }

    /// <summary>How this PC is online: a network adapter that is up and has a way out (a gateway); Wi-Fi counts first.</summary>
    private static NetworkKind CurrentNetwork()
    {
        try
        {
            var online = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                            n.GetIPProperties().GatewayAddresses.Count > 0)   // (virtual adapters have none)
                .ToList();

            if (online.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) return NetworkKind.Wifi;
            if (online.Count > 0) return NetworkKind.Wired;
        }
        catch (NetworkInformationException)
        {
            // can't tell: shown as not connected
        }

        return NetworkKind.None;
    }

    // an Xbox controller's battery over Bluetooth (Windows' Bluetooth settings): asked in the background every 15 seconds
    private int? _bluetoothBattery;
    private long _bluetoothCheckedAt = long.MinValue / 2;   // (not MinValue: "now - MinValue" overflows and would never be 15 s)
    private bool _bluetoothChecking;

    private async void RefreshBluetoothBattery()
    {
        if (_bluetoothChecking || Environment.TickCount64 - _bluetoothCheckedAt < 15_000) return;

        _bluetoothChecking = true;
        try
        {
            _bluetoothBattery = await BluetoothBattery.ConnectedControllerPercentAsync("Xbox");
        }
        finally
        {
            _bluetoothChecking = false;
            _bluetoothCheckedAt = Environment.TickCount64;
        }

        if (!_closing)
            UpdateBattery();
    }

    /// <summary>
    /// The battery next to the controller's name: hidden while the keyboard and mouse are in use, or when the controller doesn't
    /// say (a PlayStation controller over Bluetooth). Called every second.
    /// </summary>
    private void UpdateBattery()
    {
        var battery = _padInUse ? _pad.ActiveBattery : BatteryState.None;

        if (_padInUse && _pad.ActiveKind == ControllerKind.Xbox)
        {
            RefreshBluetoothBattery();

            // on a cable it says so itself (charging / wired); otherwise the level Windows' Bluetooth settings show
            if (battery.Level == ControllerBattery.Unknown && _bluetoothBattery is { } bluetooth)
                battery = new BatteryState(GamepadService.LevelOf(bluetooth), bluetooth);
        }

        if (battery.Level == ControllerBattery.Unknown)
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Segoe MDL2's battery icons: Battery0 (empty) to Battery10 (full), one step per 10%; the levels without a percentage
        // get a step of their own
        var step = battery.Percent is { } percent
            ? (percent + 5) / 10
            : battery.Level switch { ControllerBattery.Empty => 0, ControllerBattery.Low => 2, ControllerBattery.Medium => 5, _ => 10 };
        BatteryGlyph.Text = ((char)(0xE850 + Math.Clamp(step, 0, 10))).ToString();

        // the percentage when it is known (Bluetooth), otherwise the level (a cable says "Charging" / "Wired")
        BatteryText.Text = battery.Percent is { } shown ? $"{shown}%" : battery.Level.ToString();

        var color = battery.Level is ControllerBattery.Low or ControllerBattery.Empty ? Warning : Muted;
        BatteryGlyph.Foreground = color;
        BatteryText.Foreground = color;
        BatteryPanel.Visibility = Visibility.Visible;
    }

    // ---------- the buttons ----------

    private void WifiButton_Click(object sender, MouseButtonEventArgs e) => OpenWifi();

    private void BluetoothButton_Click(object sender, MouseButtonEventArgs e) => OpenBluetooth();

    private void SoundButton_Click(object sender, MouseButtonEventArgs e) => OpenSoundOutputs();

    /// <summary>Windows' list of Wi-Fi networks (the flyout of the taskbar's network icon).</summary>
    private static void OpenWifi() => OpenWindows("ms-availablenetworks:", "the Wi-Fi networks");

    /// <summary>Windows' Bluetooth settings (pair or connect a controller, headphones...).</summary>
    private static void OpenBluetooth() => OpenWindows("ms-settings:bluetooth", "the Bluetooth settings");

    private static void OpenWindows(string uri, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            GameToastWindow.ShowMessage("Couldn't open " + what, "Windows didn't open it.");
        }
    }

    /// <summary>
    /// The sound outputs in the Power menu's card (it works with the controller): the one in use ticked, choosing another makes
    /// it Windows' default, so games and apps play there.
    /// </summary>
    private void OpenSoundOutputs()
    {
        var outputs = AudioOutputService.List();

        var choices = outputs
            .Select(o => ((o.IsDefault ? "✓   " : "") + o.Name, (Action)(() => ChooseOutput(o))))
            .ToList();

        AskInPowerCard(outputs.Count == 0 ? "No sound outputs found" : "Sound output",
        [
            choices,
            [("Sound settings…", () => OpenWindows("ms-settings:sound", "the sound settings"))],
            [("Cancel", () => { })]
        ]);

        // start on the output in use
        if (outputs.FindIndex(o => o.IsDefault) is var current and >= 0)
        {
            _powerIndex = current;
            RenderPower();
        }
    }

    private static void ChooseOutput(AudioOutputService.Output output)
    {
        if (output.IsDefault) return;

        if (AudioOutputService.SetDefault(output.Id))
            GameToastWindow.ShowMessage("Sound output", output.Name);
        else
            GameToastWindow.ShowMessage("Couldn't switch the sound output", output.Name, "Windows refused. Try the sound settings.");
    }
}
