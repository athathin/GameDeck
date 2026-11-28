using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GameDeck.Views;

/// <summary>
/// Big Picture's heartbeat for reading the controllers: a steady tick every few milliseconds, raised on the screen's own thread
/// at Input priority. A DispatcherTimer can't do this: it only fires in steps of Windows' 15.6 ms timer (asked for 16 ms, it
/// fired every 31), and at its default priority it waits behind loading work, so presses reached the screen late. This clock
/// waits on Windows' high-resolution timer on a thread of its own and hands each tick to the screen; a tick still waiting to
/// be handled is never doubled up (a busy screen gets one tick, not a pile of them).
/// </summary>
public sealed class InputClock : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly Action _tick;
    private volatile bool _running;
    private volatile int _generation;   // each Start's own number: a thread from an earlier Start ends when it sees a newer one
    private int _queued;                // 1 while a tick waits to be handled on the screen's thread

    public InputClock(Dispatcher dispatcher, TimeSpan interval, Action tick)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        _tick = tick;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;

        _running = true;
        var generation = ++_generation;
        new Thread(() => Run(generation)) { IsBackground = true, Name = "Big Picture input clock", Priority = ThreadPriority.AboveNormal }
            .Start();
    }

    public void Stop() => _running = false;   // (the thread ends after its current wait)

    public void Dispose() => Stop();

    private bool Current(int generation) => _running && generation == _generation;

    private void Run(int generation)
    {
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        try
        {
            while (Current(generation))
            {
                if (timer != IntPtr.Zero)
                {
                    var due = -_interval.Ticks;   // (negative: relative, in 100 ns steps)
                    if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(timer, Infinite);
                    else
                        Thread.Sleep(_interval);
                }
                else
                {
                    Thread.Sleep(_interval);   // (Windows before 10 1803 has no high-resolution timer: 15.6 ms steps then)
                }

                if (!Current(generation)) break;
                if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) continue;   // the last tick isn't handled yet

                _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    Interlocked.Exchange(ref _queued, 0);
                    if (_running) _tick();
                });
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
