using System.Diagnostics;
using PcMieru.Core;
using PcMieru.Core.Collection;
using PcMieru.Party.Hardware;

namespace PcMieru.Party;

/// <summary>
/// The real feed (DESIGN-C 2.2, 8 step 4b): this machine is sampled by Core itself, with the aggregate source (no process or window enumeration) driven by a
/// <see cref="SamplerLoop"/>. 1 s while the window is open, 2 s while it is folded; the sampling never stops (progress runs on measurements).
/// Every sample gets a monotonic stamp the moment it arrives, so a wall-clock correction cannot reorder or age it.
/// </summary>
internal sealed class SamplerFeed : IBoardFeed, IDisposable
{
    private static readonly TimeSpan Open = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Folded = TimeSpan.FromSeconds(2);

    // SamplerSettings is the plain ISamplerControl of Core. "Minimized" is what a folded window is called there: the slow interval, never faster than Interval.
    private readonly SamplerSettings _control = new() { Interval = Open, MinimizedInterval = Folded };
    private readonly SamplerLoop _loop;
    private BoardSample? _latest;

    public SamplerFeed()
    {
        _loop = new SamplerLoop(new AggregateSnapshotSource(() => DateTime.UtcNow, _control), _control);
        _loop.Sampled += OnSampled;
    }

    /// <summary>Starts sampling (the first sample is taken at once). Calling it again does nothing.</summary>
    public void Start() => _loop.Start();

    public BoardSample? Latest => Volatile.Read(ref _latest);

    public TimeSpan Period => _control.EffectiveInterval;

    public void SetFolded(bool folded) => _control.Minimized = folded;

    // Runs on a thread-pool thread of the loop.
    private void OnSampled(Snapshot snapshot)
    {
        long stamp = Stopwatch.GetTimestamp();
        BoardFrame frame;
        try
        {
            frame = BoardFrame.Project(snapshot);
        }
        catch (Exception)
        {
            return;   // a sample that cannot be read is no sample: the previous one simply gets old (and is shown as old)
        }

        Volatile.Write(ref _latest, new BoardSample(frame, DateTime.UtcNow, stamp));
    }

    public void Dispose()
    {
        _loop.Sampled -= OnSampled;
        _loop.Dispose();
    }
}
