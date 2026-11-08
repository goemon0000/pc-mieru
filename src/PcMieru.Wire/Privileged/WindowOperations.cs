using System.Globalization;
using System.Runtime.InteropServices;

namespace PcMieru.Wire;

/// <summary>"Close" = ask the program's own windows to close (WM_CLOSE), like the X button. Nothing is forced.</summary>
public static class WindowOperations
{
    private const uint SendTimeoutMs = 2000;
    private const uint ExitWaitMs = 1500;
    private const int MaxWindows = 64;

    private sealed class EnumState
    {
        public uint Pid;
        public readonly List<nint> Windows = [];
    }

    /// <summary>
    /// The process is re-opened by ProcessKey and checked like every other operation (the same protection rules apply:
    /// PC Mieru does not close its own windows through itself, critical processes are left alone). The handle stays open
    /// while the windows are enumerated, so the PID cannot be recycled; each HWND is mapped back to that PID.
    /// Only visible top-level windows without an owner get the message (dialogs belong to their owner).
    /// </summary>
    public static ActionResult Close(uint pid, long createTime, SelfIdentity self, string systemDirectory)
    {
        var refused = ProcessGuard.Open(pid, createTime, 0, self, systemDirectory, out var guarded);
        if (refused is not null)
        {
            return refused;
        }

        using (guarded)
        {
            var windows = FindWindows(pid);
            if (windows.Count == 0)
            {
                return ActionResult.Of(OperationCode.NotFound, MessageKeys.CloseNoWindow);
            }

            var delivered = 0;
            foreach (var window in windows)
            {
                if (Native.SendMessageTimeout(
                        window, Native.WM_CLOSE, 0, 0, Native.SMTO_ABORTIFHUNG | Native.SMTO_NOTIMEOUTIFNOTHUNG, SendTimeoutMs, out _) != 0)
                {
                    delivered++;
                }
            }

            if (delivered == 0)
            {
                return ActionResult.Of(OperationCode.Error, MessageKeys.CloseNoReply);
            }

            return Native.WaitForSingleObject(guarded!.Handle, ExitWaitMs) == Native.WAIT_OBJECT_0
                ? ActionResult.Of(OperationCode.Ok, MessageKeys.CloseDone, ("pid", pid.ToString(CultureInfo.InvariantCulture)))
                : ActionResult.Of(OperationCode.Ok, MessageKeys.CloseSent, ("pid", pid.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Visible, unowned top-level windows of the process (at most 64, a sane process has a handful).</summary>
    public static IReadOnlyList<nint> FindWindows(uint pid)
    {
        var state = new EnumState { Pid = pid };
        var handle = GCHandle.Alloc(state);
        try
        {
            unsafe
            {
                Native.EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return state.Windows;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint parameter)
    {
        var state = (EnumState)GCHandle.FromIntPtr(parameter).Target!;
        if (state.Windows.Count >= MaxWindows)
        {
            return 0;
        }

        Native.GetWindowThreadProcessId(window, out var owner);
        if (owner == state.Pid && Native.IsWindowVisible(window) && Native.GetWindow(window, Native.GW_OWNER) == 0)
        {
            state.Windows.Add(window);
        }

        return 1;
    }
}
