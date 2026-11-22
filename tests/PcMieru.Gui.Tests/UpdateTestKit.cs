using PcMieru.Update.Feed;

namespace PcMieru.Gui.Tests;

/// <summary>
/// Timers that fire when a test says so: nothing in the update tests waits for time to pass. A timer that was disposed never fires (as a real one does not),
/// and a "once" timer fires once.
/// </summary>
internal sealed class ManualTimers : IUpdateTimers
{
    private readonly List<ManualTimer> _made = [];

    public IReadOnlyList<ManualTimer> Made => _made;

    public int OnceMade => _made.Count(t => !t.Repeats);

    public int EveryMade => _made.Count(t => t.Repeats);

    /// <summary>Timers made and not yet disposed.</summary>
    public int Live => _made.Count(t => !t.Disposed);

    /// <summary>Called as a timer is made (before it is returned): a test looks at the world at that moment.</summary>
    public Action<ManualTimer>? OnMade { get; set; }

    public IDisposable Once(TimeSpan after, Action tick) => Add(new ManualTimer(after, tick, repeats: false));

    public IDisposable Every(TimeSpan period, Action tick) => Add(new ManualTimer(period, tick, repeats: true));

    public ManualTimer Once() => _made.Single(t => !t.Repeats);

    public ManualTimer Poll() => _made.Single(t => t.Repeats);

    private ManualTimer Add(ManualTimer timer)
    {
        _made.Add(timer);
        OnMade?.Invoke(timer);
        return timer;
    }
}

internal sealed class ManualTimer(TimeSpan interval, Action tick, bool repeats) : IDisposable
{
    public TimeSpan Interval { get; } = interval;

    public bool Repeats { get; } = repeats;

    public bool Disposed { get; private set; }

    public int Fired { get; private set; }

    public void Fire()
    {
        if (Disposed || (!Repeats && Fired > 0))
        {
            return;
        }

        Fired++;
        tick();
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A service whose answer a test hands over when it likes (a check that is "in progress"), and that sees the token it was given.</summary>
internal sealed class GatedUpdateService : IUpdateService
{
    private readonly TaskCompletionSource<UpdateCheck> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    /// <summary>True: the check ends (cancelled) when its token is cancelled. False: it ignores the token and still answers later (a slow network).</summary>
    public bool ObeyCancel { get; set; } = true;

    public CancellationToken Seen { get; private set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Seen = ct;
        if (ObeyCancel)
        {
            ct.Register(() => _answer.TrySetCanceled(ct));
        }

        return _answer.Task;
    }

    public UpdateStatus ReadStatus() => UpdateStatus.Empty;

    public void Release(UpdateCheck answer) => _answer.TrySetResult(answer);
}

/// <summary>Holds gui.ini.lock for the length of a test: another run is saving, as far as a save can tell.</summary>
internal sealed class HeldLock : IDisposable
{
    private readonly FileStream _stream;

    public HeldLock(string iniPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        _stream = new FileStream(iniPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => _stream.Dispose();
}

internal static class UpdateKit
{
    public static readonly AppVersion Running = new(0, 4, 0);

    /// <summary>Settings of a test: in memory, or in <paramref name="iniPath"/>; <paramref name="mode"/> is the word of the file ("notify", "auto", "off") or null for no choice.</summary>
    public static GuiSettings Settings(string? iniPath = null, string? mode = null)
    {
        var settings = iniPath is null ? new GuiSettings() : new GuiSettings(iniPath);
        if (mode is not null)
        {
            settings.Apply(UpdateModes.IniKey + "=" + mode);
        }

        return settings;
    }

    /// <summary>A settings object that gives up on a held lock after a few quick tries (a test must not wait seconds for it).</summary>
    public static GuiSettings Impatient(GuiSettings settings)
    {
        settings.LockAttempts = 3;
        settings.LockWaitMs = 1;
        return settings;
    }

    public static string Text(string path) => File.ReadAllText(path);
}
