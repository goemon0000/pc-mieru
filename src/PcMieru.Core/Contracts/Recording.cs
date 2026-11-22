using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core;

/// <summary>
/// The recording side of the sampler (DESIGN-B 2 and 6). A lease is a reference count: while at least one is held the sampler runs at a
/// one-second period whatever the user's interval, minimized state or pause say, collects the GPU, and asks SensorHost for one-second
/// frames. The last release hands everything back to the user's CURRENT settings (nothing is copied at acquire time).
/// </summary>
public interface IRecordingSamples
{
    /// <summary>Takes one recording lease. Disposing the result more than once returns one count only. Thread-safe.</summary>
    IDisposable AcquireRecordingLease();

    /// <summary>
    /// A sample was taken while a lease is held. The handler gets an immutable DTO and runs on a dispatcher thread of its own, never on
    /// the sampling loop: a slow handler cannot delay the next sample. It should still only enqueue the sample for its writer: the
    /// dispatcher is one thread and keeps at most <see cref="SamplerLoop.RecordingQueueLength"/> samples; past that the OLDEST waiting
    /// sample is dropped and the gap shows as a jump in <see cref="RecordingSample.Core"/>'s frame sequence.
    /// </summary>
    event Action<RecordingSample> RecordingSampled;
}

/// <summary>The SensorHost side of a recording lease: the GPU vendor frames come every second instead of every two while a lease is held.</summary>
public interface ISensorCadence
{
    /// <summary>Takes one one-second lease (reference counted; a double dispose returns one count only). Never waits for SensorHost.</summary>
    IDisposable AcquireOneSecondLease();
}

/// <summary>The system-wide monotonic clock (QueryPerformanceCounter) in whole microseconds. SensorHost frames use the same clock, so its values compare with Core's.</summary>
public static class MonotonicClock
{
    public static long NowMicroseconds() => ToMicroseconds(Stopwatch.GetTimestamp());

    /// <summary>Stopwatch ticks to microseconds without the overflow of <c>ticks * 1_000_000</c> (which wraps after about ten days of uptime at 10 MHz).</summary>
    public static long ToMicroseconds(long ticks)
    {
        var frequency = Stopwatch.Frequency;
        return ((ticks / frequency) * 1_000_000) + ((ticks % frequency) * 1_000_000 / frequency);
    }
}

/// <summary>
/// Which acquisition a value came from. <see cref="Generation"/> 0 is Core's own sampler (written <c>c-12</c>); anything else is one
/// SensorHost process (written <c>g1-42</c>: process generation 1, its 42nd frame). Equal ids are the same acquisition, so a writer tells a
/// fresh value from a repeat by comparing them.
/// </summary>
public readonly record struct FrameId(long Generation, long Sequence)
{
    public override string ToString() =>
        Generation == 0
            ? "c-" + Sequence.ToString(CultureInfo.InvariantCulture)
            : "g" + Generation.ToString(CultureInfo.InvariantCulture) + "-" + Sequence.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Why a source has no frame to offer for a sample. Derived from what the sensor client reported, never guessed from the values.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SourceGap>))]
public enum SourceGap
{
    None,

    /// <summary>SensorHost was never asked or has not produced its first frame yet.</summary>
    NotStarted,

    /// <summary>SensorHost exited, hung or is being restarted: no frame until the new process answers.</summary>
    HostRestart,

    /// <summary>A frame arrived but the vendor read in it failed (or the client reported an error).</summary>
    ReadFailed,

    /// <summary>No vendor sensor library or no sensor client on this PC.</summary>
    Unsupported,
}

/// <summary>
/// Where a group of values came from: the frame, when it was acquired (monotonic microseconds) and the nominal period that frame stands for.
/// <see cref="Frame"/> is null (with <see cref="Gap"/> saying why) when there was no frame at all.
/// </summary>
public sealed record SourceOrigin(FrameId? Frame, long AcquiredUs, int PeriodMs, SourceGap Gap = SourceGap.None)
{
    public static SourceOrigin Missing(SourceGap gap, int periodMs = 0) => new(null, 0, periodMs, gap);
}

/// <summary>When each of Core's own sampling stages finished (monotonic microseconds); 0 where a stage gave nothing.</summary>
public sealed record CoreStageTimes(long CpuUs, long MemoryUs, long PdhUs, long GpuCounterUs);

/// <summary>One adapter in a <see cref="RecordingSample"/>: the PDH counters (Core's stage) and the vendor values (their own <see cref="Vendor"/> origin).</summary>
public sealed record RecordedGpu(
    string Id,
    string Name,
    Value<double> UtilizationPercent,
    Value<long> DedicatedUsedBytes,
    Value<double> TemperatureC,
    Value<double> PowerWatts,
    Value<double> FanPercent,
    Value<double> ClockMhz,
    Value<double> TemperatureLimitC,
    Value<ulong> LimitReasons,
    SourceOrigin Vendor);

/// <summary>
/// One Core sample for the recorder. Immutable. <see cref="Core"/> says which sampler pass it is (sequence), when the process table was read
/// and the nominal period (1000 while a lease is held); <see cref="Stages"/> says when each counter stage finished. The vendor GPU values
/// keep their own origin per adapter.
/// </summary>
public sealed record RecordingSample(
    SourceOrigin Core,
    CoreStageTimes Stages,
    DateTime TakenAtUtc,
    int IntervalMs,
    Value<double> CpuUsagePercent,
    Value<double> CpuMhz,
    Value<double> CpuPackageWatts,
    MemoryInfo Memory,
    PowerInfo Power,
    IReadOnlyList<RecordedGpu> Gpus);

/// <summary>
/// When each stage of one real sampler pass finished (Stopwatch ticks; 0 when the stage gave nothing). Carried on the snapshot so the recorder
/// can say when each group of values was acquired. One small object per sample, built from timestamps the pass takes anyway.
/// </summary>
internal sealed record SnapshotProvenance(long ProcessReadTicks, long CpuTicks, long MemoryTicks, long PdhTicks, long GpuCounterTicks);
