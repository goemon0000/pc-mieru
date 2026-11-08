using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>The pids that own a window the user can see, and the ones among them whose window is not answering.</summary>
internal sealed class WindowProbeResult
{
    public HashSet<uint> WithWindow { get; } = [];

    public HashSet<uint> Hung { get; } = [];

    public int TopLevelWindows { get; internal set; }
}

/// <summary>What a top-level window looks like to the filter; plain numbers so the rule can be tested without a desktop.</summary>
internal readonly record struct WindowTraits(bool Visible, bool HasOwner, int ExStyle, int CloakedReasons)
{
    public bool IsToolWindow => (ExStyle & Win32.WsExToolWindow) != 0 && !IsAppWindow;

    public bool IsAppWindow => (ExStyle & Win32.WsExAppWindow) != 0;

    /// <summary>
    /// A window the user would call "the app's window": visible, not cloaked (an app that Windows froze, or one on another
    /// virtual desktop, is cloaked), not a tool palette, and not a dialog owned by another window.
    /// </summary>
    public bool CountsAsAppWindow => Visible && CloakedReasons == 0 && !IsToolWindow && (!HasOwner || IsAppWindow);
}

/// <summary>
/// One EnumWindows pass per tick (SPEC 5.2: state is "not responding" when the window is a hung app window, and a process
/// "has a window" when a visible one belongs to it). The callback runs on the calling thread and never waits on another.
/// </summary>
internal static unsafe class WindowProbe
{
    private const string FrameClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private sealed class State(Func<nint, bool> isHung, Func<nint, WindowTraits> read)
    {
        public readonly WindowProbeResult Result = new();
        public readonly Func<nint, bool> IsHung = isHung;
        public readonly Func<nint, WindowTraits> Read = read;
    }

    public static WindowProbeResult Probe() => Probe(static h => Win32.IsHungAppWindow(h), ReadTraits);

    /// <summary>The same pass with the two window questions supplied by the caller (tests drive it with fake windows).</summary>
    internal static WindowProbeResult Probe(Func<nint, bool> isHung, Func<nint, WindowTraits> read)
    {
        var state = new State(isHung, read);
        var handle = GCHandle.Alloc(state);
        try
        {
            if (!Win32.EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle)) && state.Result.TopLevelWindows == 0)
            {
                throw new InvalidOperationException("EnumWindows failed and listed no window (no desktop for this process?).");
            }
        }
        finally
        {
            handle.Free();
        }

        return state.Result;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint parameter)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(parameter).Target!;
            Consider(state, window);
        }
        catch
        {
            // An exception must never cross the native frame; a window that could not be read is simply not counted.
        }

        return 1;
    }

    private static void Consider(State state, nint window)
    {
        state.Result.TopLevelWindows++;
        var traits = state.Read(window);
        if (!traits.CountsAsAppWindow)
        {
            return;
        }

        if (Win32.GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
        {
            return;
        }

        state.Result.WithWindow.Add(pid);
        if (state.IsHung(window))
        {
            state.Result.Hung.Add(pid);
        }

        // A packaged (UWP) app is shown inside a frame window that belongs to ApplicationFrameHost; the app's own process
        // owns the CoreWindow inside it. Credit that process with the window too.
        if (IsClass(window, FrameClass))
        {
            var core = Win32.FindWindowEx(window, 0, CoreWindowClass, null);
            if (core != 0 && Win32.GetWindowThreadProcessId(core, out var corePid) != 0 && corePid != 0 && corePid != pid)
            {
                state.Result.WithWindow.Add(corePid);
            }
        }
    }

    private static WindowTraits ReadTraits(nint window)
    {
        var visible = Win32.IsWindowVisible(window);
        if (!visible)
        {
            return new WindowTraits(false, false, 0, 0);
        }

        var hasOwner = Win32.GetWindow(window, Win32.GwOwner) != 0;
        var exStyle = Win32.GetWindowLong32(window, Win32.GwlExStyle);
        var cloaked = Win32.DwmGetWindowAttribute(window, Win32.DwmwaCloaked, out var reasons, sizeof(int)) == 0 ? reasons : 0;
        return new WindowTraits(true, hasOwner, exStyle, cloaked);
    }

    private static bool IsClass(nint window, string expected)
    {
        var buffer = stackalloc char[64];
        var length = Win32.GetClassName(window, buffer, 64);
        return length == expected.Length && new ReadOnlySpan<char>(buffer, length).SequenceEqual(expected);
    }
}

/// <summary>Turns "all threads suspended" and the window probe into the state column (SPEC 5.2).</summary>
internal static class ProcessStateRule
{
    public const string StateSource = "thread wait reason + IsHungAppWindow";
    public const string HungSource = "IsHungAppWindow";
    public const string NoWindowDetail = "no visible window, so responsiveness cannot be checked";

    /// <param name="windows">The probe for this tick, or null when it failed (then <paramref name="failureStatus"/> and <paramref name="failureDetail"/> say why).</param>
    public static Value<ProcessState> Decide(bool allThreadsSuspended, uint pid, WindowProbeResult? windows, ValueStatus failureStatus, string? failureDetail)
    {
        // Frozen beats hung: a suspended process cannot answer, and that is the reason to show.
        if (allThreadsSuspended)
        {
            return Value.Ok(ProcessState.Suspended, "thread wait reason");
        }

        if (windows is null)
        {
            return Value.Missing<ProcessState>(failureStatus, HungSource, failureDetail);
        }

        if (windows.Hung.Contains(pid))
        {
            return Value.Ok(ProcessState.NotResponding, HungSource);
        }

        return windows.WithWindow.Contains(pid)
            ? Value.Ok(ProcessState.Running, StateSource)
            : new Value<ProcessState>(ProcessState.Running, ValueStatus.Ok, StateSource, NoWindowDetail);
    }
}
