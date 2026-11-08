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
/// "apps"; an image signed and in the Windows folder means "Windows"; the rest is "background". The verdict comes from the
/// collection area's <see cref="IProcessGroupSource"/> (it checks the signature, which a list cannot do per row). The
/// rule below (session 0 or a well-known Windows name) is only the fallback for when that source cannot answer.
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
    private readonly IProcessGroupSource? _groups;
    private HashSet<uint> _windowPids = [];
    private bool _loaded;
    private int _age;

    /// <param name="windows">The PIDs that own a visible window (also used for "close window" and as the fallback rule).</param>
    /// <param name="groups">The collection area's verdicts. Null: the fallback rule decides everything.</param>
    public ProcessClassifier(Func<HashSet<uint>> windows, IProcessGroupSource? groups = null)
    {
        _windows = windows;
        _groups = groups;
    }

    /// <summary>A deterministic stand-in for sample data, whose PIDs do not exist on this PC.</summary>
    public static ProcessClassifier ForSampleData(IProcessGroupSource? groups = null) =>
        new(static () => [.. Enumerable.Range(0, 20000).Where(i => i % 3 == 0).Select(i => (uint)i)], groups);

    /// <summary>Re-reads the window list. Called once per sample; reads only every <paramref name="every"/>th call.</summary>
    public void Refresh(int every = 2)
    {
        // The first call always reads (the group of every process depends on it); then every n-th call.
        if (!_loaded || ++_age >= every)
        {
            _loaded = true;
            _age = 0;
            _windowPids = _windows();
        }
    }

    public bool HasWindow(uint pid) => _windowPids.Contains(pid);

    /// <summary>
    /// The group of a process. The source's verdict when it has one; Background while the source is still examining the
    /// image (shown as such, never guessed as Windows: a look-alike name must not pass for a system process even for a second);
    /// the fallback rule when the source cannot answer (no window list, key not in its latest sample, failed read).
    /// </summary>
    public ProcessGroup Classify(ProcessInfo process)
    {
        if (_groups is null)
        {
            return ClassifyByName(process);
        }

        var verdict = _groups.GroupOf(process.Key);
        return verdict.Status switch
        {
            ValueStatus.Ok => verdict.Data,
            ValueStatus.NotCollected => ProcessGroup.Background,
            _ => ClassifyByName(process),
        };
    }

    /// <summary>The fallback rule: the app's own, from the window list, the session and a list of well-known Windows names.</summary>
    internal ProcessGroup ClassifyByName(ProcessInfo process)
    {
        if (process.SessionId > 0 && HasWindow(process.Key.Pid))
        {
            return ProcessGroup.App;
        }

        return process.SessionId == 0 || WindowsNames.Contains(process.Name) ? ProcessGroup.Windows : ProcessGroup.Background;
    }
}
