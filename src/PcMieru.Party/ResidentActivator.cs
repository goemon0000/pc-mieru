using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcMieru.Party;

/// <summary>
/// A second start of Party does not just say "already running": it brings the running one to the front (DESIGN-C 3 item 2). Only a Party of this Windows
/// session whose image is the same file as ours counts (<see cref="ResidentActivation.Pick"/>); anything else (another session, the old Parts Life, another
/// install) is left alone and the caller keeps its "the other one is running" message. No window message is sent: this only restores and raises the window.
/// </summary>
internal static partial class ResidentActivator
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SwRestore = 9;

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

    /// <summary>
    /// Finds a running PCMieru.Party of this session and image, restores it when minimised and raises it. True when one was brought forward.
    /// Never throws: any failure to read a process just leaves it out, and false falls back to the plain message.
    /// </summary>
    internal static bool TryActivateExisting()
    {
        try
        {
            var selfPid = Environment.ProcessId;
            if (!ProcessIdToSessionId((uint)selfPid, out var selfSession))
            {
                return false;
            }

            var found = new List<(int Pid, int SessionId, string? ImagePath)>();
            var handles = new Dictionary<int, nint>();
            foreach (var p in Process.GetProcessesByName("PCMieru.Party"))
            {
                using (p)
                {
                    if (!ProcessIdToSessionId((uint)p.Id, out var session))
                    {
                        continue;
                    }

                    found.Add((p.Id, (int)session, ImagePathOf(p.Id)));
                    handles[p.Id] = p.MainWindowHandle;   // visible or minimised top-level window of the process; 0 when it has none
                }
            }

            foreach (var pid in ResidentActivation.Pick(found, selfPid, (int)selfSession, Environment.ProcessPath))
            {
                var hwnd = handles[pid];
                if (hwnd == 0 || !IsWindowVisible(hwnd))
                {
                    continue;
                }

                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SwRestore);
                }

                // We were just started by the user, so Windows lets this process hand the foreground on.
                SetForegroundWindow(hwnd);
                return true;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Falls back to the plain "already running" message.
        }

        return false;
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
}
