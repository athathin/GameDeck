using System.Net.NetworkInformation;

namespace GameDeck.Services;

/// <summary>How this PC is online.</summary>
public enum NetworkKind
{
    None,
    Wifi,
    Wired
}

/// <summary>Whether this PC is online, and how (Big Picture's Wi-Fi button shows it). Windows says when it changes: NetworkChange.</summary>
public static class NetworkStatus
{
    /// <summary>A network adapter that is up and has a way out (a gateway) means online; Wi-Fi counts first.</summary>
    public static NetworkKind Current()
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
}
