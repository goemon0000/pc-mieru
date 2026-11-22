namespace PcMieru.Gui;

/// <summary>
/// The two timers of the update check (DESIGN-B 3.1): one that fires once 60 s after the start, one that looks every hour whether a check is due.
/// Behind an interface so that a test drives them by hand and a fake clock is enough: nothing in the tests waits for time to pass.
/// A timer is made only when the mode allows the network (Notify, Automatic); in Ask and Off none of this exists.
/// </summary>
internal interface IUpdateTimers
{
    /// <summary>Calls <paramref name="tick"/> once after <paramref name="after"/>. Disposing the result before that cancels it.</summary>
    IDisposable Once(TimeSpan after, Action tick);

    /// <summary>Calls <paramref name="tick"/> every <paramref name="period"/> until the result is disposed.</summary>
    IDisposable Every(TimeSpan period, Action tick);
}

/// <summary>The window's timers: <see cref="System.Windows.Forms.Timer"/>, so the tick runs on the UI thread with the controller's state.</summary>
internal sealed class WinFormsUpdateTimers : IUpdateTimers
{
    public IDisposable Once(TimeSpan after, Action tick)
    {
        var timer = new System.Windows.Forms.Timer { Interval = Milliseconds(after) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tick();
        };
        timer.Start();
        return timer;
    }

    public IDisposable Every(TimeSpan period, Action tick)
    {
        var timer = new System.Windows.Forms.Timer { Interval = Milliseconds(period) };
        timer.Tick += (_, _) => tick();
        timer.Start();
        return timer;
    }

    private static int Milliseconds(TimeSpan span) => (int)Math.Clamp(span.TotalMilliseconds, 1, int.MaxValue);
}
