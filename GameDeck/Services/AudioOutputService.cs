using System.Runtime.InteropServices;

namespace GameDeck.Services;

/// <summary>
/// The sound outputs (speakers, headphones, a TV over HDMI...) for Big Picture's Sound button: which ones there are, which one
/// Windows uses, and switching to another one. Listing uses Windows' own audio API (Core Audio). Windows has no official way for
/// an app to change the default output, so switching uses the hidden interface Windows' own Sound settings use (IPolicyConfig),
/// the same one well-known tools such as EarTrumpet and SoundSwitch rely on; it has been the same since Windows 7.
/// Call from the UI thread (COM).
/// </summary>
public static class AudioOutputService
{
    /// <summary>One sound output: Windows' id for it, the name it shows, and whether it is the one in use.</summary>
    public record Output(string Id, string Name, bool IsDefault);

    /// <summary>The outputs that are plugged in and turned on, the one in use marked. Empty when they can't be read.</summary>
    public static List<Output> List()
    {
        var outputs = new List<Output>();

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            var defaultId = DefaultId(enumerator);

            Check(enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out var devices));
            Check(devices.GetCount(out var count));

            for (var i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;

                if (device.GetId(out var id) == 0 && id is not null)
                    outputs.Add(new Output(id, FriendlyName(device) ?? "Sound output", id == defaultId));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // no audio service, or no outputs: nothing to list
        }

        return outputs.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The name of the output in use, or null when there is none.</summary>
    public static string? DefaultName() => List().FirstOrDefault(o => o.IsDefault)?.Name;

    /// <summary>Makes the output the one Windows uses for everything (games, music, calls).</summary>
    /// <returns>False when Windows refused.</returns>
    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { RoleConsole, RoleMultimedia, RoleCommunications })
                Check(policy.SetDefaultEndpoint(id, role));

            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex, "Sound output");
            return false;
        }
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator) =>
        enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) == 0 && device.GetId(out var id) == 0
            ? id
            : null;

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StorageRead, out var store) != 0) return null;

        var key = FriendlyNameKey;
        if (store.GetValue(ref key, out var value) != 0) return null;

        try
        {
            return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
            Marshal.ThrowExceptionForHR(result);
    }

    // ---------- Windows' audio interfaces ----------

    private const int DataFlowRender = 0;        // eRender: outputs, not microphones
    private const int DeviceStateActive = 1;     // DEVICE_STATE_ACTIVE: plugged in and turned on
    private const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;
    private const int StorageRead = 0;           // STGM_READ
    private const ushort VtLpwstr = 31;

    // PKEY_Device_FriendlyName: "Speakers (Realtek(R) Audio)"
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private readonly ushort _reserved1, _reserved2, _reserved3;
        public IntPtr Pointer;
        private readonly IntPtr _more;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // Windows 7 and later. Only SetDefaultEndpoint is used; the methods before it must still be listed, in this order.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
