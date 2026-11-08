namespace PcMieru.Core.Collection;

/// <summary>
/// How often and how much to measure. Owned by whoever drives sampling (the GUI timer, <see cref="SamplerLoop"/>);
/// the sampler reads <see cref="GpuActive"/> every tick, so GPU counters really stop when nobody looks at them.
/// </summary>
public interface ISamplerControl
{
    /// <summary>Normal interval. SPEC 4 offers 0.5 / 1 / 2 / 5 s; any value is clamped to 0.25 s .. 60 s.</summary>
    TimeSpan Interval { get; set; }

    /// <summary>Window minimized or hidden: sampling slows to <see cref="SamplerSettings.MinimizedInterval"/> (never faster than <see cref="Interval"/>).</summary>
    bool Minimized { get; set; }

    /// <summary>Paused: no sampling. Resuming drops the baseline so the paused gap never becomes a rate.</summary>
    bool Paused { get; set; }

    /// <summary>The interval a loop should wait right now (Interval, or the slow one while minimized).</summary>
    TimeSpan EffectiveInterval { get; }

    /// <summary>
    /// True while the GPU counters are being collected: at least one live <see cref="SubscribeGpu"/> subscription, or
    /// <see cref="GpuDefaultOn"/>. When false the snapshot says GPU is not collected instead of paying for it.
    /// </summary>
    bool GpuActive { get; }

    /// <summary>
    /// The GPU view (tab, process column) calls this when it becomes visible and disposes the result when hidden.
    /// Subscriptions are counted, so two views can overlap.
    /// </summary>
    IDisposable SubscribeGpu();
}

/// <summary>Default <see cref="ISamplerControl"/>. Thread-safe: the UI thread writes, the sampling thread reads.</summary>
public sealed class SamplerSettings : ISamplerControl
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);

    private long _intervalTicks = TimeSpan.FromSeconds(1).Ticks;
    private int _minimized;
    private int _paused;
    private int _gpuDefaultOn = 1;
    private int _gpuSubscribers;

    /// <summary>Interval while minimized (SPEC 4: 5 s).</summary>
    public TimeSpan MinimizedInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether GPU is collected with no subscriber. True so a CLI one-shot gets GPU numbers;
    /// the GUI sets it false and subscribes while a GPU view is visible (SPEC 4).
    /// </summary>
    public bool GpuDefaultOn
    {
        get => Volatile.Read(ref _gpuDefaultOn) != 0;
        set => Volatile.Write(ref _gpuDefaultOn, value ? 1 : 0);
    }

    public TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _intervalTicks));
        set => Volatile.Write(ref _intervalTicks, Math.Clamp(value.Ticks, MinInterval.Ticks, MaxInterval.Ticks));
    }

    public bool Minimized
    {
        get => Volatile.Read(ref _minimized) != 0;
        set => Volatile.Write(ref _minimized, value ? 1 : 0);
    }

    public bool Paused
    {
        get => Volatile.Read(ref _paused) != 0;
        set => Volatile.Write(ref _paused, value ? 1 : 0);
    }

    public TimeSpan EffectiveInterval
    {
        get
        {
            var normal = Interval;
            return Minimized && MinimizedInterval > normal ? MinimizedInterval : normal;
        }
    }

    public bool GpuActive => GpuDefaultOn || Volatile.Read(ref _gpuSubscribers) > 0;

    public IDisposable SubscribeGpu()
    {
        Interlocked.Increment(ref _gpuSubscribers);
        return new GpuSubscription(this);
    }

    private sealed class GpuSubscription(SamplerSettings owner) : IDisposable
    {
        private SamplerSettings? _owner = owner;

        public void Dispose()
        {
            // Disposing twice must not take someone else's subscription away.
            if (Interlocked.Exchange(ref _owner, null) is { } o)
            {
                Interlocked.Decrement(ref o._gpuSubscribers);
            }
        }
    }
}
