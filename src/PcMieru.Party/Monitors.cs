using System.Runtime.InteropServices;

namespace PcMieru.Party;

/// <summary>
/// Which monitors are attached right now (a small user32 call; WinForms is not referenced). Used to decide whether a remembered window position
/// can still be grabbed after a monitor was removed.
/// </summary>
internal static partial class Monitors
{
    private const uint MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref NativeRect rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>
    /// True when the rectangle (physical pixels, virtual-desktop coordinates) overlaps the WORK area (the screen without the taskbar) of the monitor
    /// that holds most of it. A rectangle in the gap between monitors, or outside all of them, is false.
    /// </summary>
    internal static bool IntersectsWorkArea(int left, int top, int right, int bottom)
    {
        var rect = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
        nint monitor = MonitorFromRect(ref rect, MonitorDefaultToNull);
        if (monitor == 0) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var work = info.Work;
        return rect.Left < work.Right && rect.Right > work.Left && rect.Top < work.Bottom && rect.Bottom > work.Top;
    }
}
