using System.Runtime.InteropServices;

namespace PcMieru.Gui;

/// <summary>
/// Which processes own a window the user can see. Used for the "apps" group, for deciding whether "close window" is
/// offered and for the "not responding" hint. Read on the UI thread every few samples (a few hundred windows, microseconds).
/// </summary>
internal static class WindowProbe
{
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>PIDs that own at least one visible, titled, un-owned, un-cloaked top-level window.</summary>
    public static HashSet<uint> VisibleWindowPids()
    {
        var pids = new HashSet<uint>();
        EnumWindows(
            (hwnd, _) =>
            {
                if (IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero && GetWindowTextLength(hwnd) > 0
                    && (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) == 0
                    && DwmGetWindowAttribute(hwnd, DwmaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    pids.Add(pid);
                }

                return true;
            },
            IntPtr.Zero);
        return pids;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

/// <summary>
/// The app's own rule for the list view's groups (not Task Manager's, and the help says so): a visible window means
/// "apps"; session 0 or a well-known Windows name means "Windows"; the rest is "background".
/// </summary>
internal sealed class ProcessClassifier
{
    private static readonly HashSet<string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Registry", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe", "services.exe",
        "lsass.exe", "winlogon.exe", "fontdrvhost.exe", "dwm.exe", "svchost.exe", "conhost.exe", "sihost.exe", "taskhostw.exe",
        "RuntimeBroker.exe", "SearchIndexer.exe", "spoolsv.exe", "audiodg.exe", "ctfmon.exe", "dllhost.exe", "WmiPrvSE.exe",
        "MsMpEng.exe", "SecurityHealthService.exe", "SecurityHealthSystray.exe", "backgroundTaskHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "TextInputHost.exe", "SearchHost.exe", "lsm.exe", "WUDFHost.exe", "LockApp.exe",
    };

    private readonly Func<HashSet<uint>> _windows;
    private HashSet<uint> _windowPids = [];
    private int _age = int.MaxValue;

    public ProcessClassifier(Func<HashSet<uint>> windows) => _windows = windows;

    /// <summary>A deterministic stand-in for sample data, whose PIDs do not exist on this PC.</summary>
    public static ProcessClassifier ForSampleData() =>
        new(static () => [.. Enumerable.Range(0, 20000).Where(i => i % 3 == 0).Select(i => (uint)i)]);

    /// <summary>Re-reads the window list. Called once per sample; reads only every <paramref name="every"/>th call.</summary>
    public void Refresh(int every = 2)
    {
        if (++_age >= every)
        {
            _age = 0;
            _windowPids = _windows();
        }
    }

    public bool HasWindow(uint pid) => _windowPids.Contains(pid);

    public ProcessGroup Classify(ProcessInfo process)
    {
        if (process.SessionId > 0 && HasWindow(process.Key.Pid))
        {
            return ProcessGroup.Apps;
        }

        return process.SessionId == 0 || WindowsNames.Contains(process.Name) ? ProcessGroup.Windows : ProcessGroup.Background;
    }
}
