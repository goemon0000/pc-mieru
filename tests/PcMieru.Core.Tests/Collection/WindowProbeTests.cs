using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

public sealed class WindowTraitsRuleTests
{
    private const int ToolWindow = 0x80;
    private const int AppWindow = 0x40000;

    [Fact]
    public void A_visible_unowned_uncloaked_window_counts()
    {
        Assert.True(new WindowTraits(true, false, 0, 0).CountsAsAppWindow);
    }

    [Fact]
    public void A_hidden_window_never_counts()
    {
        Assert.False(new WindowTraits(false, false, 0, 0).CountsAsAppWindow);
        Assert.False(new WindowTraits(false, false, AppWindow, 0).CountsAsAppWindow);
    }

    [Fact]
    public void A_cloaked_window_does_not_count_whatever_the_reason()
    {
        // Cloaked = on another virtual desktop, or a packaged app Windows has frozen. Not something the user can see.
        foreach (var reason in new[] { 1, 2, 4, 7 })
        {
            Assert.False(new WindowTraits(true, false, 0, reason).CountsAsAppWindow);
        }
    }

    [Fact]
    public void A_tool_window_does_not_count_unless_it_also_asks_for_a_taskbar_button()
    {
        Assert.False(new WindowTraits(true, false, ToolWindow, 0).CountsAsAppWindow);
        Assert.True(new WindowTraits(true, false, ToolWindow | AppWindow, 0).CountsAsAppWindow);
    }

    [Fact]
    public void An_owned_window_is_a_dialog_of_another_window_unless_it_has_its_own_button()
    {
        Assert.False(new WindowTraits(true, true, 0, 0).CountsAsAppWindow);
        Assert.True(new WindowTraits(true, true, AppWindow, 0).CountsAsAppWindow);
    }
}

#pragma warning disable SYSLIB1054 // test-only P/Invoke; the Core project is the one that must be AOT/marshalling clean

/// <summary>
/// A real top-level window on a thread of this process, so the probe has something real to look at. The window is a plain
/// STATIC control (no window procedure of ours), created off-screen so nothing appears on the desktop.
/// </summary>
internal sealed class TestWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const int WsExToolWindow = 0x80;
    private const uint PmRemove = 1;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private nint _handle;

    /// <param name="pump">Whether the owning thread keeps taking messages. A thread that does not looks hung after 5 seconds.</param>
    /// <param name="toolWindow">Tool windows have no taskbar button, so nothing flashes on the user's taskbar.</param>
    public TestWindow(bool pump, bool toolWindow)
    {
        _thread = new Thread(() => Run(pump, toolWindow)) { IsBackground = true, Name = "test window" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || _handle == 0)
        {
            throw new InvalidOperationException("The test window could not be created (no desktop for this process?).");
        }
    }

    public nint Handle => _handle;

    public uint ProcessId => (uint)Environment.ProcessId;

    private void Run(bool pump, bool toolWindow)
    {
        _handle = CreateWindowExW(toolWindow ? WsExToolWindow : 0, "STATIC", "pcmieru probe test", WsPopup | WsVisible, -32000, -32000, 8, 8, 0, 0, 0, 0);
        _ready.Set();
        if (_handle == 0)
        {
            return;
        }

        if (pump)
        {
            while (!_stop.IsSet)
            {
                while (PeekMessageW(out var message, 0, 0, 0, PmRemove))
                {
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessageW(ref message);
                }

                Thread.Sleep(5);
            }
        }
        else
        {
            _stop.Wait(); // a blocking wait on a non-STA thread takes no window messages
        }

        _ = DestroyWindow(_handle);
    }

    public void Dispose()
    {
        _stop.Set();
        _ = _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Message message);
}

#pragma warning restore SYSLIB1054

public sealed class WindowProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void The_real_probe_lists_visible_windows_and_credits_them_to_their_process()
    {
        using var window = new TestWindow(pump: true, toolWindow: false);

        var result = WindowProbe.Probe();

        Assert.True(result.TopLevelWindows > 0);
        Assert.Contains(window.ProcessId, result.WithWindow);
        Assert.DoesNotContain(window.ProcessId, result.Hung);
    }

    [Fact]
    public void A_tool_window_off_screen_is_not_a_window_of_the_process()
    {
        // Own pid appears in WithWindow only through some other window of this process; with none, the set must not hold it.
        using var window = new TestWindow(pump: true, toolWindow: true);

        var result = WindowProbe.Probe();

        var alsoHasAnotherWindow = result.WithWindow.Contains(window.ProcessId);
        output.WriteLine($"test host pid {window.ProcessId} has another visible window: {alsoHasAnotherWindow}");
        var counted = WindowProbe.Probe(static _ => false, h => h == window.Handle ? new WindowTraits(true, false, 0x80, 0) : new WindowTraits(false, false, 0, 0));
        Assert.DoesNotContain(window.ProcessId, counted.WithWindow);
    }

    [Fact]
    public async Task A_window_whose_thread_stops_taking_messages_is_reported_hung_and_a_busy_one_is_not()
    {
        using var hung = new TestWindow(pump: false, toolWindow: true);
        using var alive = new TestWindow(pump: true, toolWindow: true);
        var deadline = Stopwatch.StartNew();
        var sawHung = false;

        // Windows decides "not responding" after about 5 seconds without a message being taken.
        WindowProbeResult last;
        do
        {
            last = WindowProbe.Probe(Win32Hung, h => h == hung.Handle || h == alive.Handle ? new WindowTraits(true, false, 0, 0) : new WindowTraits(false, false, 0, 0));
            sawHung = last.Hung.Contains(hung.ProcessId);
            if (!sawHung)
            {
                await Task.Delay(250);
            }
        }
        while (!sawHung && deadline.Elapsed < TimeSpan.FromSeconds(20));

        output.WriteLine($"hung after {deadline.Elapsed.TotalSeconds:F1} s");
        Assert.True(sawHung, "IsHungAppWindow never reported the window of a thread that takes no messages");
        Assert.InRange(deadline.Elapsed.TotalSeconds, 3, 20);

        // Same process, so the pid is in Hung either way: tell the two windows apart with the per-window answers.
        Assert.True(Win32Hung(hung.Handle));
        Assert.False(Win32Hung(alive.Handle));
    }

    private static bool Win32Hung(nint window) => IsHungAppWindow(window);

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(nint window);
#pragma warning restore SYSLIB1054

    [Fact]
    public void Windows_that_cannot_be_read_do_not_stop_the_pass()
    {
        var result = WindowProbe.Probe(static _ => false, static _ => throw new InvalidOperationException("cannot read this window"));

        Assert.True(result.TopLevelWindows > 0);
        Assert.Empty(result.WithWindow);
    }
}

public sealed class ProcessStateRuleTests
{
    private static WindowProbeResult Probe(uint[] withWindow, uint[] hung)
    {
        var r = new WindowProbeResult();
        foreach (var p in withWindow)
        {
            r.WithWindow.Add(p);
        }

        foreach (var p in hung)
        {
            r.Hung.Add(p);
        }

        return r;
    }

    [Fact]
    public void A_hung_window_is_not_responding()
    {
        var state = ProcessStateRule.Decide(false, 10, Probe([10], [10]), ValueStatus.Ok, null);

        Assert.Equal(ProcessState.NotResponding, state.Data);
        Assert.True(state.IsOk);
    }

    [Fact]
    public void Suspended_wins_over_a_hung_window()
    {
        Assert.Equal(ProcessState.Suspended, ProcessStateRule.Decide(true, 10, Probe([10], [10]), ValueStatus.Ok, null).Data);
    }

    [Fact]
    public void Suspended_needs_no_probe_so_a_failed_probe_does_not_hide_it()
    {
        var state = ProcessStateRule.Decide(true, 10, null, ValueStatus.Timeout, "window enumeration: stuck");

        Assert.Equal(ProcessState.Suspended, state.Data);
    }

    [Fact]
    public void A_process_with_a_responding_window_is_running()
    {
        var state = ProcessStateRule.Decide(false, 10, Probe([10], []), ValueStatus.Ok, null);

        Assert.Equal(ProcessState.Running, state.Data);
        Assert.Null(state.Detail);
    }

    [Fact]
    public void A_process_without_a_window_is_running_and_says_hangs_were_not_checkable()
    {
        var state = ProcessStateRule.Decide(false, 10, Probe([11], [11]), ValueStatus.Ok, null);

        Assert.Equal(ProcessState.Running, state.Data);
        Assert.Equal(ProcessStateRule.NoWindowDetail, state.Detail);
    }

    [Fact]
    public void When_the_probe_failed_a_running_process_is_unknown_with_the_reason()
    {
        var state = ProcessStateRule.Decide(false, 10, null, ValueStatus.Timeout, "window enumeration: stuck");

        Assert.False(state.IsOk);
        Assert.Equal(ValueStatus.Timeout, state.Status);
        Assert.Equal("window enumeration: stuck", state.Detail);
        Assert.Equal(ProcessStateRule.HungSource, state.Source);
    }
}
