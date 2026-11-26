using Windows.Devices.Enumeration;

namespace GameDeck.Services;

/// <summary>
/// The battery of a controller connected over Bluetooth, as Windows' Bluetooth settings show it (Settings › Bluetooth &amp;
/// devices): a property Windows keeps on a paired device. Windows keeps the last value after a controller disconnects, so only
/// connected controllers count: all of a connected controller's input devices are present, a disconnected one's game
/// controller device isn't.
/// Everything is read from the device tree (quick); asking Bluetooth itself which devices are connected takes 30 seconds.
/// </summary>
public static class BluetoothBattery
{
    // DEVPKEY_Bluetooth_Battery: the percentage Settings shows
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string PresentKey = "System.Devices.Present";
    private const string ClassKey = "System.Devices.ClassGuid";

    // the "HIDClass" devices: a controller's game controller device is one of them
    private static readonly Guid HidClass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    // the controllers (their containers) that have a battery value: found by going through every device (about 0.4 s), so
    // only now and then (a newly paired controller shows up within 2 minutes)
    private static readonly TimeSpan ListFor = TimeSpan.FromMinutes(2);
    private static List<(Guid Container, string Name)>? _withBattery;
    private static long _listedAt;

    /// <summary>
    /// The battery of the connected Bluetooth controllers whose name contains <paramref name="nameContains"/> ("Xbox"): the
    /// lowest when there are several (the one to charge first). Null when none is connected or none reports it.
    /// </summary>
    public static async Task<int?> ConnectedControllerPercentAsync(string nameContains)
    {
        try
        {
            if (_withBattery is null || Environment.TickCount64 - _listedAt > ListFor.TotalMilliseconds)
            {
                var all = await DeviceInformation.FindAllAsync("", [BatteryKey, ContainerKey], DeviceInformationKind.Device);
                _withBattery = all
                    .Where(d => d.Properties.TryGetValue(BatteryKey, out var battery) && battery is not null &&
                                d.Properties.TryGetValue(ContainerKey, out var container) && container is Guid)
                    .Select(d => ((Guid)d.Properties[ContainerKey], d.Name))
                    .Distinct()
                    .ToList();
                _listedAt = Environment.TickCount64;
            }

            int? lowest = null;
            foreach (var (container, _) in _withBattery.Where(c => c.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
            {
                if (await ConnectedPercent(container) is { } percent)
                    lowest = lowest is null ? percent : Math.Min(lowest.Value, percent);
            }

            return lowest;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or
                                   InvalidCastException or ArgumentException)
        {
            return null;   // Windows wouldn't say: no battery shown
        }
    }

    /// <summary>The battery of the controller in that container, or null when it isn't connected (or has no value).</summary>
    private static async Task<int?> ConnectedPercent(Guid container)
    {
        var devices = await DeviceInformation.FindAllAsync($"{ContainerKey}:=\"{{{container}}}\"", [BatteryKey, PresentKey, ClassKey],
            DeviceInformationKind.Device);

        // a controller has two input devices: the Bluetooth link's one stays present after it disconnects, its game controller
        // device doesn't. So it is connected only when all of them are present.
        var inputs = devices.Where(d => d.Properties.TryGetValue(ClassKey, out var type) && type is Guid id && id == HidClass).ToList();
        var connected = inputs.Count > 0 && inputs.All(d => d.Properties.TryGetValue(PresentKey, out var present) && present is true);
        if (!connected) return null;

        foreach (var device in devices)
        {
            if (device.Properties.TryGetValue(BatteryKey, out var value) && value is byte or int or uint)
                return Math.Clamp(Convert.ToInt32(value), 0, 100);
        }

        return null;
    }
}
