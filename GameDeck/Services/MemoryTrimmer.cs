using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace GameDeck.Services;

/// <summary>
/// Gives memory back to Windows. Used when GameDeck goes to the system tray: nothing is on screen then, so the
/// cover pictures can go, the .NET heap is compacted and the pages Windows still counts for the process are released
/// (they come back by themselves when the window is opened again).
/// </summary>
public static class MemoryTrimmer
{
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, -1, -1);   // -1, -1 = "release everything you can"
        }
        catch
        {
            // purely an optimization
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
