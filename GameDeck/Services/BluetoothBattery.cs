using Windows.Devices.Enumeration;

namespace GameDeck.Services;

/// <summary>
/// The battery of a controller connected over Bluetooth, as Windows' Bluetooth settings show it (Settings › Bluetooth &amp;
/// devices): a property Windows keeps for each paired device. Windows keeps the last value after a controller disconnects, so
/// only connected controllers count: the device with the battery and the Bluetooth connection are matched by their container.
/// </summary>
public static class BluetoothBattery
{
    // DEVPKEY_Bluetooth_Battery: the percentage Settings shows
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private const string EndpointContainerKey = "System.Devices.Aep.ContainerId";

    /// <summary>
    /// The battery of the connected Bluetooth controllers whose name contains <paramref name="nameContains"/> ("Xbox"): the
    /// lowest when there are several (the one to charge first). Null when none is connected or none reports it.
    /// </summary>
    public static async Task<int?> ConnectedControllerPercentAsync(string nameContains)
    {
        try
        {
            var endpoints = await DeviceInformation.FindAllAsync(
                $"{ConnectedKey}:=System.StructuredQueryType.Boolean#True", [ConnectedKey, EndpointContainerKey],
                DeviceInformationKind.AssociationEndpoint);

            int? lowest = null;
            foreach (var endpoint in endpoints.Where(e => e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
            {
                if (endpoint.Properties.TryGetValue(EndpointContainerKey, out var container) && container is Guid id &&
                    await PercentOf(id) is { } percent)
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

    /// <summary>The battery of the device in that container (a controller is a few devices: the one that has the battery).</summary>
    private static async Task<int?> PercentOf(Guid container)
    {
        var devices = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryKey],
            DeviceInformationKind.Device);

        foreach (var device in devices)
        {
            if (device.Properties.TryGetValue(BatteryKey, out var value) && value is byte or int or uint)
                return Math.Clamp(Convert.ToInt32(value), 0, 100);
        }

        return null;
    }
}
