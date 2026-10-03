using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AravalsStream.Capture.Display;

internal sealed class HighResolutionFrameWait : IDisposable
{
    private const uint CreateHighResolutionTimer = 0x00000002;
    private const uint SynchronizeAccess = 0x00100000;
    private static readonly IntPtr InvalidHandle = IntPtr.Zero;
    private readonly IntPtr _handle;

    private HighResolutionFrameWait(IntPtr handle) => _handle = handle;

    public static HighResolutionFrameWait? TryCreate()
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateHighResolutionTimer, SynchronizeAccess);
        return handle == InvalidHandle ? null : new HighResolutionFrameWait(handle);
    }

    public void WaitUntil(long deadlineTicks)
    {
        var remaining = deadlineTicks - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        var dueTime = -Math.Max(1L, (long)(remaining * 10_000_000d / Stopwatch.Frequency));
        if (SetWaitableTimer(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            _ = WaitForSingleObject(_handle, uint.MaxValue);
        while (Stopwatch.GetTimestamp() < deadlineTicks) Thread.SpinWait(16);
    }

    public void Dispose()
    {
        if (_handle != InvalidHandle) CloseHandle(_handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
