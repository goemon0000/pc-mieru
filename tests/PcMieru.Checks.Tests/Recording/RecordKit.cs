using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Core;

namespace PcMieru.Checks.Tests;

/// <summary>
/// An in-memory <see cref="IRunFileSystem"/> for the recorder tests: nothing touches a disk, and a test puts a fault in anywhere through
/// <see cref="Fail"/> or <see cref="Hook"/> instead of locking real files. A file keeps what was written (<c>Pending</c>, readable by everybody like
/// the OS cache) apart from what was forced to the disk (<c>Durable</c>), so a test can tell "written" from "durable": a crash drops the first.
/// </summary>
internal sealed class MemRunFs : IRunFileSystem
{
    private sealed class MemFile
    {
        public readonly List<byte> Durable = [];
        public readonly List<byte> Pending = [];
        public bool IsLock;
    }

    private sealed record Rule(string Op, Func<string, bool>? When, Func<Exception> Make, int Remaining);

    private readonly object _gate = new();
    private readonly Dictionary<string, MemFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Rule> _rules = [];
    private readonly List<string> _log = [];

    /// <summary>The free space every call of <see cref="GetFreeBytes"/> answers; null means unknown.</summary>
    public long? FreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Called with (operation, file name) before every operation, outside any lock. It may block; the exception it returns is thrown instead of the operation.</summary>
    public Func<string, string, Exception?>? Hook { get; set; }

    private static string Key(string path) => Path.GetFullPath(path);

    private static string Name(string path) => Path.GetFileName(path);

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public int Count(string op, string? nameSuffix = null)
    {
        lock (_gate)
        {
            return _log.Count(e => e.StartsWith(op + " ", StringComparison.Ordinal) && (nameSuffix is null || e.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>Makes the next <paramref name="times"/> calls of <paramref name="op"/> (whose file passes <paramref name="when"/>) throw; -1 for every call.</summary>
    public void Fail(string op, Func<Exception> make, Func<string, bool>? when = null, int times = -1)
    {
        lock (_gate)
        {
            _rules.Add(new Rule(op, when, make, times));
        }
    }

    public void ClearFaults()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    private void Enter(string op, string path)
    {
        var name = Name(path);
        Exception? ex = null;
        lock (_gate)
        {
            _log.Add(op + " " + name);
            for (var i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                if (rule.Op == op && rule.Remaining != 0 && (rule.When is null || rule.When(name)))
                {
                    ex = rule.Make();
                    _rules[i] = rule with { Remaining = rule.Remaining < 0 ? -1 : rule.Remaining - 1 };
                    break;
                }
            }
        }

        if (ex is null && Hook is { } hook)
        {
            ex = hook(op, name);
        }

        if (ex is not null)
        {
            throw ex;
        }
    }

    // ----------------------------------------------------------------- the interface

    public void CreateDirectory(string directory)
    {
        Enter("CreateDirectory", directory);
        lock (_gate)
        {
            _directories.Add(Key(directory));
        }
    }

    public IRunFile CreateNew(string path)
    {
        Enter("CreateNew", path);
        lock (_gate)
        {
            var key = Key(path);
            if (_files.ContainsKey(key))
            {
                throw new IOException("exists", unchecked((int)0x80070050));
            }

            var file = new MemFile();
            _files[key] = file;
            return new Handle(this, key, file);
        }
    }

    public IRunFile Create(string path)
    {
        Enter("Create", path);
        lock (_gate)
        {
            var key = Key(path);
            var file = new MemFile();
            _files[key] = file;
            return new Handle(this, key, file);
        }
    }

    public void ReplaceAtomic(string temp, string destination)
    {
        Enter("ReplaceAtomic", destination);
        lock (_gate)
        {
            var from = Key(temp);
            if (!_files.TryGetValue(from, out var source))
            {
                throw new FileNotFoundException("no temp file", temp);
            }

            // Only what was forced to the disk comes across: a save that forgot FlushToDisk shows up as an empty file.
            var moved = new MemFile();
            moved.Durable.AddRange(source.Durable);
            _files[Key(destination)] = moved;
            _files.Remove(from);
        }
    }

    public IDisposable? TryLock(string lockPath)
    {
        Enter("TryLock", lockPath);
        lock (_gate)
        {
            var key = Key(lockPath);
            if (!_locks.Add(key))
            {
                return null;
            }

            _files[key] = new MemFile { IsLock = true };
            return new LockHandle(this, key);
        }
    }

    public bool IsLockHeld(string lockPath)
    {
        lock (_gate)
        {
            return _locks.Contains(Key(lockPath));
        }
    }

    public bool Exists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(Key(path));
        }
    }

    public IReadOnlyList<string> ListFiles(string directory, string pattern)
    {
        Enter("ListFiles", directory);
        var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var folder = Key(directory);
        lock (_gate)
        {
            var list = new List<string>();
            foreach (var key in _files.Keys)
            {
                if (string.Equals(Path.GetDirectoryName(key), folder, StringComparison.OrdinalIgnoreCase) && regex.IsMatch(Path.GetFileName(key)))
                {
                    list.Add(Path.GetFileName(key));
                }
            }

            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }

    public byte[] ReadAllBytes(string path, int maxBytes)
    {
        Enter("ReadAllBytes", path);
        lock (_gate)
        {
            if (!_files.TryGetValue(Key(path), out var file))
            {
                throw new FileNotFoundException("no such file", path);
            }

            var bytes = new byte[file.Durable.Count + file.Pending.Count];
            file.Durable.CopyTo(bytes, 0);
            file.Pending.CopyTo(bytes, file.Durable.Count);
            if (bytes.Length > maxBytes)
            {
                throw new InvalidDataException("too long");
            }

            return bytes;
        }
    }

    public void Delete(string path)
    {
        Enter("Delete", path);
        lock (_gate)
        {
            _files.Remove(Key(path));
        }
    }

    public long? GetFreeBytes(string directory)
    {
        Enter("GetFreeBytes", directory);
        return FreeBytes;
    }

    // ----------------------------------------------------------------- what a test does with it

    /// <summary>Puts a file there as it would be after a run: all of it durable.</summary>
    public void Put(string path, byte[] bytes)
    {
        lock (_gate)
        {
            var file = new MemFile();
            file.Durable.AddRange(bytes);
            _files[Key(path)] = file;
        }
    }

    public void Put(string path, string text) => Put(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>Everything readable now (what a reader sees), as text.</summary>
    public string Text(string path) => Encoding.UTF8.GetString(ReadAllBytes(path, int.MaxValue));

    /// <summary>Only what was forced to the disk.</summary>
    public byte[] DurableBytes(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(Key(path), out var file) ? [.. file.Durable] : [];
        }
    }

    public int PendingLength(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(Key(path), out var file) ? file.Pending.Count : 0;
        }
    }

    public IReadOnlyList<string> Names(string directory, string pattern = "*") => ListFiles(directory, pattern);

    /// <summary>Somebody else (another process) holds this lock.</summary>
    public IDisposable HoldLock(string lockPath) =>
        TryLock(lockPath) ?? throw new InvalidOperationException("already held");

    /// <summary>A power cut: what was only written is gone, every lock is let go, the files stay.</summary>
    public void PowerCut()
    {
        lock (_gate)
        {
            foreach (var key in _locks.ToArray())
            {
                _files.Remove(key);
            }

            _locks.Clear();
            foreach (var file in _files.Values)
            {
                file.Pending.Clear();
            }
        }
    }

    private sealed class Handle(MemRunFs owner, string key, MemFile file) : IRunFile
    {
        private bool _closed;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            owner.Enter("Write", key);
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                file.Pending.AddRange(bytes.ToArray());
            }
        }

        public void FlushToDisk()
        {
            owner.Enter("FlushToDisk", key);
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                file.Durable.AddRange(file.Pending);
                file.Pending.Clear();
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _closed = true;
            }
        }
    }

    private sealed class LockHandle(MemRunFs owner, string key) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._locks.Remove(key);
                owner._files.Remove(key);
            }
        }
    }
}

/// <summary>A fake <see cref="IRecordingSamples"/>: counts the leases and the subscribers, and a test raises the samples itself.</summary>
internal sealed class FakeSamples : IRecordingSamples
{
    private readonly object _gate = new();
    private Action<RecordingSample>? _handlers;
    private int _taken;
    private int _held;
    private int _disposals;

    /// <summary>Thrown by the next <see cref="AcquireRecordingLease"/> when set (the loop was disposed).</summary>
    public Exception? LeaseFault { get; set; }

    public int LeasesTaken => Volatile.Read(ref _taken);

    /// <summary>Leases taken and not yet returned.</summary>
    public int LeasesHeld => Volatile.Read(ref _held);

    /// <summary>Every call of Dispose on a lease, the repeated ones too (the real lease counts a second one as nothing).</summary>
    public int LeaseDisposals => Volatile.Read(ref _disposals);

    public int Subscribers
    {
        get
        {
            lock (_gate)
            {
                return _handlers?.GetInvocationList().Length ?? 0;
            }
        }
    }

    public IDisposable AcquireRecordingLease()
    {
        if (LeaseFault is { } fault)
        {
            throw fault;
        }

        Interlocked.Increment(ref _taken);
        Interlocked.Increment(ref _held);
        return new Lease(this);
    }

    public event Action<RecordingSample> RecordingSampled
    {
        add
        {
            lock (_gate)
            {
                _handlers += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _handlers -= value;
            }
        }
    }

    public void Raise(RecordingSample sample)
    {
        Action<RecordingSample>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }

        handlers?.Invoke(sample);
    }

    private sealed class Lease(FakeSamples owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            Interlocked.Increment(ref owner._disposals);
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref owner._held);
            }
        }
    }
}

/// <summary>The monotonic clock of a test, in microseconds, moved by hand.</summary>
internal sealed class FakeClock(long startUs)
{
    private long _us = startUs;

    public long Us
    {
        get => Interlocked.Read(ref _us);
        set => Interlocked.Exchange(ref _us, value);
    }

    public long Now() => Us;
}

/// <summary>Builds the samples the sampler would hand over.</summary>
internal static class SampleKit
{
    public static readonly DateTime Wall = new(2026, 11, 21, 3, 0, 0, DateTimeKind.Utc);

    private static Value<double> D(double? v) => v is { } x ? Value.Ok(x, "t") : Value.NotCollected<double>("t");

    private static Value<long> L(long? v) => v is { } x ? Value.Ok(x, "t") : Value.NotCollected<long>("t");

    public static Value<ulong> Bits(ulong? v) => v is { } x ? Value.Ok(x, "t") : Value.NotCollected<ulong>("t");

    /// <summary>A GPU as one sample carries it: the PDH values and the vendor values with their own frame.</summary>
    public static RecordedGpu Gpu(
        string id = "gpu-a",
        string name = "Test GPU A",
        long generation = 1,
        long sequence = 1,
        long acquiredUs = 0,
        int periodMs = 1000,
        double? util = 50,
        long? vram = 4_000_000_000,
        double? temp = 60,
        double? power = 100,
        double? fan = 40,
        double? clock = 2000,
        double? limitC = 83,
        ulong? bits = 0,
        SourceOrigin? vendor = null) =>
        new(
            id,
            name,
            D(util),
            L(vram),
            D(temp),
            D(power),
            D(fan),
            D(clock),
            D(limitC),
            Bits(bits),
            vendor ?? new SourceOrigin(new FrameId(generation, sequence), acquiredUs, periodMs));

    /// <summary>A Core sample: frame <c>c-seq</c> acquired at <paramref name="acquiredUs"/> on the monotonic clock.</summary>
    public static RecordingSample Sample(
        long sequence,
        long acquiredUs,
        double? cpu = 10,
        IReadOnlyList<RecordedGpu>? gpus = null,
        int periodMs = 1000,
        SourceOrigin? core = null,
        DateTime? taken = null,
        string? batteryState = null)
    {
        var origin = core ?? new SourceOrigin(new FrameId(0, sequence), acquiredUs, periodMs);
        return new RecordingSample(
            origin,
            new CoreStageTimes(acquiredUs, acquiredUs, acquiredUs, acquiredUs),
            taken ?? Wall.AddTicks(acquiredUs * 10),
            periodMs,
            D(cpu),
            D(3500),
            D(45),
            new MemoryInfo(
                32L * 1024 * 1024 * 1024,
                20L * 1024 * 1024 * 1024,
                L(12L * 1024 * 1024 * 1024),
                L(40L * 1024 * 1024 * 1024),
                L(1L * 1024 * 1024 * 1024),
                L(300_000_000),
                L(200_000_000)),
            new PowerInfo(
                Value.Ok(true, "t"),
                Value.Ok(80, "t"),
                batteryState is null ? Value.NotCollected<string>("t") : Value.Ok(batteryState, "t")),
            gpus ?? []);
    }
}

/// <summary>A recorder with everything faked: the files, the samples, the clocks and the ids. <see cref="StartUs"/> is the monotonic clock when a run starts.</summary>
internal sealed class RecHarness : IDisposable
{
    public const string Root = @"C:\mem\pcmieru\runs";

    /// <summary>The monotonic time a run starts at (5 s since "boot" in the fake clock).</summary>
    public const long StartUs = 5_000_000;

    private int _ids;

    /// <summary>The files, the clocks and the ids are replaced by the fakes; the other options (flush interval, free-space floor, queue size) are the caller's.</summary>
    public RecHarness(long flushIntervalUs = 10_000_000, long minFreeBytes = RunRecorder.DefaultMinFreeBytes, int queueCapacity = 256, int firstId = 1)
    {
        _ids = firstId - 1;
        Clock = new FakeClock(StartUs);
        Time = new FakeTime(new DateTimeOffset(SampleKit.Wall, TimeSpan.Zero));
        var options = new RunRecorderOptions
        {
            FileSystem = Fs,
            Time = Time,
            NowUs = Clock.Now,
            App = "PCMieru 0.5.0-test",
            FlushIntervalUs = flushIntervalUs,
            MinFreeBytes = minFreeBytes,
            QueueCapacity = queueCapacity,
            NewId = () => IdOf(Interlocked.Increment(ref _ids)),
        };
        Recorder = new RunRecorder(Samples, Root, options);
    }

    public MemRunFs Fs { get; } = new();

    public FakeSamples Samples { get; } = new();

    public FakeClock Clock { get; }

    public FakeTime Time { get; }

    public RunRecorder Recorder { get; }

    public static Guid IdOf(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    public string JsonPath(Guid id) => Path.Combine(Root, RunFiles.JsonName(id));

    public string CsvPath(Guid id) => Path.Combine(Root, RunFiles.CsvName(id));

    public string LockPath(Guid id) => Path.Combine(Root, RunFiles.LockName(id));

    public string TempPath(Guid id) => Path.Combine(Root, RunFiles.TempName(id));

    /// <summary>The absolute monotonic time that is <paramref name="offsetUs"/> after the start of a run.</summary>
    public static long At(long offsetUs) => StartUs + offsetUs;

    /// <summary>Moves the clock to <paramref name="offsetUs"/> after the start and hands one sample over, acquired then.</summary>
    public void Tick(long sequence, long offsetUs, double? cpu = 10, IReadOnlyList<RecordedGpu>? gpus = null)
    {
        Clock.Us = At(offsetUs);
        Samples.Raise(SampleKit.Sample(sequence, At(offsetUs), cpu, gpus));
    }

    /// <summary>
    /// Everything handed to the writer before this call has been handled when it returns: a mark is queued behind the samples and finishes when the
    /// JSON holding it is on the disk. (It does add a <c>mark</c> marker to the run.)
    /// </summary>
    public async Task<RunMarker> Barrier(RunHandle run) =>
        await Recorder.MarkAsync(run, MarkerKind.Mark, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    public async Task<RunHandle> StartAsync() => await Recorder.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    public RunHeader Header(Guid id) => RunJson.Read(Fs.DurableBytes(JsonPath(id)));

    public void Dispose()
    {
        // Nothing real is held: the files and the clocks are in memory. A run a test left recording is stopped so its writer thread ends.
        try
        {
            Recorder.StopCurrentAsync(RunStopReason.ProcessExit).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException)
        {
            // The test's own assertion is the one that matters.
        }
    }
}

/// <summary>Series for the summary, written straight from the spec.</summary>
internal static class SeriesKit
{
    public const double Us = 1_000_000.0;

    public static MetricTrack NewTrack(string key)
    {
        var def = RunMetrics.Describe(key);
        var kind = key == "cpu.perf_limit_flags" ? RunMetricKind.Bitmask : def?.Kind ?? RunMetricKind.Number;
        var source = key[..key.IndexOf('.', StringComparison.Ordinal)];
        return new MetricTrack(key, def?.Unit ?? "", source, kind);
    }

    public static void Add(MetricTrack track, double seconds, double value, int periodMs = 1000)
    {
        if (double.IsNaN(value))
        {
            track.AddMissing((long)(seconds * Us));
        }
        else
        {
            track.AddValue((long)(seconds * Us), periodMs, value, track.Kind == RunMetricKind.Bitmask ? (ulong)value : 0);
        }
    }

    /// <summary>
    /// n seconds of a source at P = 1 s: one acquisition at (t - 1) s for t = 1..n; a NaN is a "could not be read" at that time. The run ends at n s with
    /// a normal stop. (The old fixture's t = 1..n, shifted to the run's own zero.)
    /// </summary>
    public static RunSeries Series(int n, params (string Key, Func<int, double> F)[] columns) =>
        Series(n, RunStatus.Completed, RunStopReason.User, markers: null, columns);

    public static RunSeries Series(int n, RunStatus status, RunStopReason? stop, IReadOnlyList<RunMarker>? markers, params (string Key, Func<int, double> F)[] columns)
    {
        var series = new RunSeries
        {
            EndUs = (long)(n * Us),
            NormalEnd = status == RunStatus.Completed,
            Rows = n,
            Status = status,
            StopReason = stop,
            Markers = markers ?? [],
        };
        foreach (var (key, f) in columns)
        {
            var track = NewTrack(key);
            for (var t = 1; t <= n; t++)
            {
                Add(track, t - 1, f(t));
            }

            series.Tracks.Add(track);
        }

        return series;
    }

    /// <summary>The old ThermalRamp: one GPU for 300 s, load 100%, temperature 40 to 80 over 120 s, clock 2800 to 2000 over the same time, power 250 W; the CPU idles at 5%.</summary>
    public static RunSeries ThermalRamp(Func<int, double>? util = null, Func<int, double>? clock = null)
    {
        static double Ramp(int t) => Math.Min(t, 120) / 120.0;
        return Series(
            300,
            ("core.cpu_usage_pct", _ => 5),
            ("core.gpu1_util_pct", util ?? (_ => 100)),
            ("gpu1.temp_c", t => 40 + (40 * Ramp(t))),
            ("gpu1.clock_mhz", clock ?? (t => 2800 - (800 * Ramp(t)))),
            ("gpu1.power_w", _ => 250));
    }

    public static RunMarker Marker(MarkerKind kind, double seconds) =>
        new(kind, (long)(seconds * Us), SampleKit.Wall.AddSeconds(seconds));

    public static RunSummary Summarize(RunSeries series) => RunSummarizer.Summarize(series);
}

/// <summary>Runs built by hand (a run the recorder did not write: a crashed one, a half-deleted one, a run from another tool).</summary>
internal static class RunFileKit
{
    /// <summary>A CSV as the writer writes it: the header, then one row per sample (row time = acquire time - <paramref name="startUs"/>).</summary>
    public static string CsvText(RunLayout layout, long startUs, IEnumerable<RecordingSample> samples)
    {
        var formatter = new RunRowFormatter(layout);
        var text = new StringBuilder(layout.HeaderLine());
        foreach (var sample in samples)
        {
            text.Append(formatter.Format(sample, sample.Core.AcquiredUs - startUs, startUs)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A header for <paramref name="layout"/>; the caller says how the run ended.</summary>
    public static RunHeader HeaderOf(
        RunLayout layout,
        Guid id,
        RunStatus status,
        RunStopReason? stop,
        long durationUs = 0,
        long rows = 0,
        DateTime? started = null,
        DateTime? ended = null,
        IReadOnlyList<RunMarker>? markers = null) =>
        new(
            id,
            "PCMieru 0.5.0-test",
            started ?? SampleKit.Wall,
            ended,
            durationUs,
            status,
            stop,
            0,
            rows,
            RunFiles.CsvName(id),
            layout.Metrics,
            layout.SourceInfos,
            markers ?? []);

    /// <summary>Puts the run's JSON (and its CSV when <paramref name="csv"/> is not null) in <paramref name="root"/>, all of it durable.</summary>
    public static void PutRun(MemRunFs fs, string root, RunHeader header, string? csv)
    {
        fs.Put(Path.Combine(root, RunFiles.JsonName(header.RunId)), RunJson.Write(header));
        if (csv is not null)
        {
            fs.Put(Path.Combine(root, RunFiles.CsvName(header.RunId)), csv);
        }
    }
}

/// <summary>
/// Stops one file-system call of a test until the test lets it go (the "Hook" of <see cref="MemRunFs"/>): the first call that matches blocks, the test
/// waits until it is in there, looks at the recorder, and releases it. Nothing sleeps. After the release the call may fail with <paramref name="thenFail"/>.
/// </summary>
internal sealed class FsGate(Func<string, string, bool> match, Func<Exception>? thenFail = null) : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _armed = 1;

    public Exception? Hook(string op, string name)
    {
        if (match(op, name) && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _entered.Set();
            _release.Wait(Limit);
            return thenFail?.Invoke();
        }

        return null;
    }

    public void WaitEntered() => Assert.True(_entered.Wait(Limit), "The operation was never reached.");

    public void Release() => _release.Set();

    /// <summary>A test that ends early lets the blocked call go, so no thread is left waiting.</summary>
    public void Dispose() => _release.Set();
}
