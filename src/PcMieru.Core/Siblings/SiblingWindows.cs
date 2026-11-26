using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcMieru.Core.Siblings;

/// <summary>One top-level window of a sibling process: the process id and the window handle.</summary>
public readonly record struct SiblingWindow(int Pid, nint Handle);

/// <summary>
/// Finding "the other exe of this install" (DESIGN-C 2.2, 3 items 2-4): PCMieru.exe looks for the window of PCMieru.Party.exe and the other way round. One rule for both
/// exes. A process counts only when ALL of these hold: it is not this process, it runs in the SAME Windows session (another signed-in user's window is not ours and cannot
/// be reached anyway), and its full image path is the same file as the EXPECTED exe, compared without case after GetFullPath. A process whose path could not be read counts
/// for nothing: unknown is not "the same".
///
/// The expected exe is <c>Path.Combine(AppContext.BaseDirectory, name)</c> of OUR deployment (<see cref="ExpectedPath"/>), never a name found by search: a copy elsewhere, an
/// old build or another install is not a sibling. <see cref="Pick"/> is the pure decision (testable without starting anything); the rest reads the processes and posts.
/// </summary>
public static partial class SiblingWindows
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SwRestore = 9;
    private const uint FlashAll = 0x3;                  // FLASHW_ALL: the caption and the taskbar button
    private const uint FlashTimerNoForeground = 0xC;    // FLASHW_TIMERNOFG: until the window comes to the foreground

    /// <summary>The file this deployment expects a sibling to run from: <paramref name="exeFileName"/> beside the running exe.</summary>
    public static string ExpectedPath(string exeFileName) => Path.Combine(AppContext.BaseDirectory, exeFileName);

    /// <summary>The process ids that qualify, in the order given. Pure: the caller reads (id, session, full image path) of each candidate.</summary>
    public static IReadOnlyList<int> Pick(
        IEnumerable<(int Pid, int SessionId, string? ImagePath)> candidates,
        int selfPid, int selfSessionId, string? expectedImagePath)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var expected = Normalize(expectedImagePath);
        var picked = new List<int>();
        if (expected is null)
        {
            return picked;
        }

        foreach (var (pid, session, path) in candidates)
        {
            if (pid == selfPid || session != selfSessionId)
            {
                continue;
            }

            if (Normalize(path) is { } other && string.Equals(other, expected, StringComparison.OrdinalIgnoreCase))
            {
                picked.Add(pid);
            }
        }

        return picked;
    }

    /// <summary>
    /// The top-level windows of the running siblings of <paramref name="exeFileName"/> (a file name such as <c>PCMieru.Party.exe</c>, looked for beside this exe). Only a visible
    /// window counts (a minimised one is visible; a process with no window is left out). Never throws: a process that cannot be read is just left out.
    /// </summary>
    public static IReadOnlyList<SiblingWindow> Find(string exeFileName)
    {
        var windows = new List<SiblingWindow>();
        var processes = new List<Process>();
        try
        {
            var selfPid = Environment.ProcessId;
            if (!ProcessIdToSessionId((uint)selfPid, out var selfSession))
            {
                return windows;
            }

            var found = new List<(int Pid, int SessionId, string? ImagePath)>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeFileName)))
            {
                processes.Add(p);
                if (ProcessIdToSessionId((uint)p.Id, out var session))
                {
                    found.Add((p.Id, (int)session, ImagePathOf(p.Id)));
                }
            }

            foreach (var pid in Pick(found, selfPid, (int)selfSession, ExpectedPath(exeFileName)))
            {
                var hwnd = processes.Find(p => p.Id == pid)?.MainWindowHandle ?? 0;   // the visible top-level window of the process; 0 when it has none
                if (hwnd != 0 && IsWindowVisible(hwnd))
                {
                    windows.Add(new SiblingWindow(pid, hwnd));
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // What was found so far stands; the caller falls back to its plain path.
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }

        return windows;
    }

    /// <summary>
    /// Restores a sibling's window when minimised and raises it. True when a sibling window exists, that is when the user's click found the other one. When Windows refuses to hand the
    /// foreground on (SetForegroundWindow returns false), the window is flashed in the taskbar instead (<see cref="Flash"/>) so the user can see where it is; the result stays true because
    /// the window does exist. The caller must have been started or clicked by the user (Windows lets it hand the foreground on).
    /// </summary>
    public static bool Activate(string exeFileName)
    {
        foreach (var window in Find(exeFileName))
        {
            RestoreIfMinimized(window.Handle);
            if (!SetForegroundWindow(window.Handle))
            {
                Flash(window.Handle);
            }

            return true;
        }

        return false;
    }

    /// <summary>Flashes a window's taskbar button (and its caption) until it comes to the front: FLASHW_ALL | FLASHW_TIMERNOFG. Best effort: the result is not reported.</summary>
    public static void Flash(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = hwnd,
            Flags = FlashAll | FlashTimerNoForeground,
            Count = 0,
            TimeoutMs = 0,
        };
        _ = FlashWindowEx(ref info);
    }

    /// <summary>FLASHWINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint TimeoutMs;
    }

    /// <summary>
    /// Posts <paramref name="message"/> (a registered window message, see <see cref="MieruMessages"/>) with wParam and lParam both 0 to every sibling window of
    /// <paramref name="exeFileName"/>. With <paramref name="allowForeground"/> the receiver's process is first allowed to take the foreground (the message asks it to come to the front).
    /// Returns how many windows were posted to; 0 when there is none.
    /// </summary>
    public static int Notify(string exeFileName, uint message, bool allowForeground)
    {
        if (message == 0)
        {
            return 0;
        }

        var posted = 0;
        foreach (var window in Find(exeFileName))
        {
            if (allowForeground)
            {
                AllowSetForegroundWindow((uint)window.Pid);
            }

            if (PostMessage(window.Handle, message, 0, 0))
            {
                posted++;
            }
        }

        return posted;
    }

    /// <summary>Shows a minimised window again (to the size it had before), and does nothing to one that is not minimised.</summary>
    public static void RestoreIfMinimized(nint hwnd)
    {
        if (hwnd != 0 && IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ImagePathOf(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            var buffer = new ushort[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(h, 0, buffer, ref size)
                ? new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, (int)size)))
                : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] ushort[] buffer, ref uint size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int cmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, uint message, nuint wParam, nint lParam);
}
