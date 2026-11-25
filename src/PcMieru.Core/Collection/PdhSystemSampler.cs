using System.Diagnostics;
using System.Globalization;

namespace PcMieru.Core.Collection;

/// <summary>
/// What the always-on PDH query yields for one tick. <see cref="PackageWatts"/> is the CPU package power (missing with its reason when the
/// Energy Meter counters are not there); <see cref="AcquiredTicks"/> is the Stopwatch timestamp taken right after the collection that
/// produced the numbers (0 when there was none), so the recorder can say when these values were acquired.
/// </summary>
internal sealed record PdhSystemReading(IReadOnlyList<DiskInfo> Disks, Value<double> CpuMhz, Value<int> DisksState)
{
    public Value<double> PackageWatts { get; init; } = Value.NotCollected<double>("none");

    public long AcquiredTicks { get; init; }
}

/// <summary>
/// Physical disks (<c>\PhysicalDisk(*)</c>) and the CPU clock from one PDH query that stays open. PDH computes the rates
/// between its own two collections, so the first collection after start yields nothing and says so.
/// Counter paths are English (PdhAddEnglishCounterW), so a Japanese Windows needs no name table.
/// </summary>
internal sealed class PdhSystemSampler : IPdhSampling
{
    private const string DiskSource = "PDH PhysicalDisk";
    private const string ClockSource = "PDH Processor Information";
    private const string PowerSource = "PDH Energy Meter";
    private const double MinWindowMs = 200;

    private readonly IPdhQuery _query;
    private readonly PdhArrayReader _reader = new();
    private readonly nint _idle;
    private readonly nint _read;
    private readonly nint _write;
    private readonly nint _latency;
    private readonly nint _frequency;
    private readonly nint _performance;
    private readonly nint _energy;
    private readonly Dictionary<string, DiskAccumulator> _disks = [];
    private readonly Dictionary<int, string> _models = [];
    private long _lastStamp;
    private bool _baselineValid;

    public PdhSystemSampler()
        : this(new PdhQuery())
    {
    }

    /// <summary>
    /// The sampler over a query that is already open. It owns the query from here: when setting it up throws (a counter that cannot be added, the
    /// first collection failing), the query is closed before the exception goes on. The half-built object never reaches a caller, so nobody else
    /// could close it, and every retry after a failed open would leak one PDH query handle.
    /// </summary>
    internal PdhSystemSampler(IPdhQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
        try
        {
            if (!_query.IsOpen)
            {
                return;
            }

            _idle = _query.Add(@"\PhysicalDisk(*)\% Idle Time", out _);
            _read = _query.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec", out _);
            _write = _query.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec", out _);
            _latency = _query.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer", out _);
            _frequency = _query.Add(@"\Processor Information(_Total)\Processor Frequency", out _);
            _performance = _query.Add(@"\Processor Information(_Total)\% Processor Performance", out _);

            // The CPU package power. Many PCs have no Energy Meter counter set: Add then gives 0 and the value says so, it is never a 0 W reading.
            _energy = _query.Add(@"\Energy Meter(*)\Power", out _);

            // The first collection only starts PDH's clock. Doing it here lets the first tick already have rates.
            if (_query.Collect() == Pdh.Success)
            {
                _lastStamp = Stopwatch.GetTimestamp();
                _baselineValid = true;
            }
        }
        catch
        {
            _query.Dispose();
            throw;
        }
    }

    public bool IsOpen => _query.IsOpen;

    /// <summary>Forget the baseline (resume after a pause or sleep): the next collection restarts PDH's window and reads as "not collected".</summary>
    public void Reset() => _baselineValid = false;

    public PdhSystemReading Sample()
    {
        if (!_query.IsOpen)
        {
            return Unavailable("PDH could not be opened");
        }

        // Rates over a few milliseconds are noise; wait for a real window before calling a value a measurement.
        var primed = _baselineValid && Stopwatch.GetElapsedTime(_lastStamp).TotalMilliseconds >= MinWindowMs;
        var status = _query.Collect();
        if (status != Pdh.Success)
        {
            _baselineValid = false;
            return Unavailable($"PdhCollectQueryData 0x{status:X8}");
        }

        _lastStamp = Stopwatch.GetTimestamp();
        _baselineValid = true;

        // Every counter array starts from nothing: a disk that one array leaves out has that reading missing, not the one of the last tick.
        foreach (var d in _disks.Values)
        {
            d.BeginTick();
        }

        ReadDisks(_idle, static (a, v) => a.Idle = v);
        ReadDisks(_read, static (a, v) => a.Read = v);
        ReadDisks(_write, static (a, v) => a.Write = v);
        ReadDisks(_latency, static (a, v) => a.Latency = v);

        var disks = new List<DiskInfo>(_disks.Count);
        List<string>? gone = null;
        foreach (var (instance, acc) in _disks)
        {
            if (!acc.Seen)
            {
                (gone ??= []).Add(instance);
                continue;
            }

            disks.Add(Build(instance, acc, primed, ModelOf(instance)));
        }

        if (gone is not null)
        {
            foreach (var instance in gone)
            {
                _disks.Remove(instance);
            }
        }

        disks.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new PdhSystemReading(disks, ReadClock(primed), DisksState(disks.Count))
        {
            PackageWatts = ReadPackagePower(primed),
            AcquiredTicks = _lastStamp,
        };
    }

    public void Dispose() => _query.Dispose();

    /// <summary>Instance names are "0 C:" or "1" (no letters) or "0 C: D:"; "_Total" is skipped. False for anything else.</summary>
    internal static bool TryParseInstance(ReadOnlySpan<char> instance, out int index, out string letters)
    {
        index = 0;
        letters = string.Empty;
        instance = instance.Trim();
        if (instance.IsEmpty)
        {
            return false;
        }

        var space = instance.IndexOf(' ');
        var number = space < 0 ? instance : instance[..space];
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0)
        {
            return false;
        }

        letters = space < 0 ? string.Empty : instance[(space + 1)..].Trim().ToString();
        return true;
    }

    internal static string DisplayName(int index, string letters, string? model)
    {
        var label = string.IsNullOrWhiteSpace(model) ? $"Disk {index}" : model;
        return letters.Length == 0 ? label : $"{label} ({letters.Replace(' ', ',')})";
    }

    internal static DiskInfo Build(string instance, DiskAccumulator a, bool primed, string? model)
    {
        _ = TryParseInstance(instance, out var index, out var letters);
        var id = $"PhysicalDrive{index}";
        var name = DisplayName(index, letters, model);
        return new DiskInfo(
            id,
            name,
            Pick(a.Idle, primed, static v => Math.Clamp(100.0 - v, 0.0, 100.0)),
            Pick(a.Read, primed, static v => Math.Max(0.0, v)),
            Pick(a.Write, primed, static v => Math.Max(0.0, v)),
            Pick(a.Latency, primed, static v => Math.Max(0.0, v * 1000.0)));
    }

    private static Value<double> Pick(Reading r, bool primed, Func<double, double> map)
    {
        if (!r.Present)
        {
            return Value.Unsupported<double>(DiskSource, "counter not available");
        }

        if (!primed)
        {
            return Value.NotCollected<double>(DiskSource, "no baseline yet (first sample)");
        }

        return r.Valid
            ? Value.Ok(map(r.Value), DiskSource)
            : Value.NotCollected<double>(DiskSource, $"PDH status 0x{r.CStatus:X8}");
    }

    private void ReadDisks(nint counter, Action<DiskAccumulator, Reading> store)
    {
        if (counter == 0 || !_reader.Read(counter))
        {
            // Not readable: leave the field "not present" for every disk (the first collection also lands here).
            if (counter != 0 && _reader.Status != Pdh.Success)
            {
                MarkUnreadable(store, _reader.Status);
            }

            return;
        }

        for (var i = 0; i < _reader.Count; i++)
        {
            if (!_reader.TryGet(i, out var name, out var value, out var cstatus) || name.SequenceEqual("_Total"))
            {
                continue;
            }

            if (!TryParseInstance(name, out _, out _))
            {
                continue;
            }

            var key = name.ToString();
            if (!_disks.TryGetValue(key, out var acc))
            {
                acc = new DiskAccumulator();
                _disks[key] = acc;
            }

            acc.Seen = true;
            store(acc, new Reading(true, value, cstatus));
        }
    }

    /// <summary>"No data yet" (0xC0000BBA) is expected on the first collection and means "not collected", not "not supported".</summary>
    private void MarkUnreadable(Action<DiskAccumulator, Reading> store, uint status)
    {
        foreach (var acc in _disks.Values)
        {
            acc.Seen = true;
            store(acc, new Reading(true, 0, status));
        }
    }

    private Value<double> ReadClock(bool primed)
    {
        if (_frequency == 0 || _performance == 0)
        {
            return Value.Unsupported<double>(ClockSource, "counter not available");
        }

        if (!primed)
        {
            return Value.NotCollected<double>(ClockSource, "no baseline yet (first sample)");
        }

        if (!PdhReadRule.TryReadTotal(_reader, _frequency, out var freq, out var why)
            || !PdhReadRule.TryReadTotal(_reader, _performance, out var perf, out why))
        {
            return Value.NotCollected<double>(ClockSource, why);
        }

        // Task Manager's "Speed" (the rule is shared with the CPU check: PdhReadRule.EffectiveMhz).
        return PdhReadRule.EffectiveMhz(freq, perf) is { } mhz
            ? Value.Ok(mhz, ClockSource)
            : Value.NotCollected<double>(ClockSource, "counter returned no data");
    }

    private Value<double> ReadPackagePower(bool primed)
    {
        if (_energy == 0)
        {
            return PackagePowerValue(new PackagePowerReading(PackagePowerState.NoCounter, 0), primed);
        }

        if (!_reader.Read(_energy))
        {
            // "No instance" is the answer of a machine whose meter lists nothing; any other failure is a sample that did not come.
            return PackagePowerValue(
                new PackagePowerReading(_reader.Status == Pdh.NoInstance ? PackagePowerState.NoPackageInstance : PackagePowerState.NoValidSample, 0),
                primed);
        }

        return PackagePowerValue(PdhReadRule.PackagePower(_reader.Bytes, _reader.BaseAddress, _reader.Count), primed);
    }

    /// <summary>
    /// A package power reading as a value: watts when valid, otherwise missing with the reason. An absent counter set or package instance is
    /// Unsupported (this PC cannot say), a sample that did not come is NotCollected; neither is ever a 0 W reading.
    /// </summary>
    internal static Value<double> PackagePowerValue(PackagePowerReading reading, bool primed) => reading.State switch
    {
        PackagePowerState.Valid => Value.Ok(reading.Watts, PowerSource),
        PackagePowerState.NoCounter => Value.Unsupported<double>(PowerSource, @"this Windows has no Energy Meter counters (\Energy Meter(*)\Power), so the CPU package power is not available"),
        PackagePowerState.NoPackageInstance => Value.Unsupported<double>(PowerSource, "the Energy Meter lists no CPU package (_PKG) instance on this PC"),
        _ => primed
            ? Value.NotCollected<double>(PowerSource, "the CPU package power sample was not usable")
            : Value.NotCollected<double>(PowerSource, "no baseline yet (first sample)"),
    };

    private string? ModelOf(string instance)
    {
        if (!TryParseInstance(instance, out var index, out _))
        {
            return null;
        }

        if (!_models.TryGetValue(index, out var model))
        {
            model = StorageModel.Read(index) ?? string.Empty;
            _models[index] = model;
        }

        return model.Length == 0 ? null : model;
    }

    internal static PdhSystemReading Unavailable(string detail) =>
        new([], Value.Failed<double>(ClockSource, detail), Value.Failed<int>(DiskSource, detail))
        {
            PackageWatts = Value.Failed<double>(PowerSource, detail),
        };

    /// <summary>The disk list is Ok (zero is then a real answer) unless the PhysicalDisk counters were never there to read.</summary>
    private Value<int> DisksState(int count) =>
        _idle == 0
            ? Value.Unsupported<int>(DiskSource, "the PhysicalDisk counters are not available on this machine")
            : Value.Ok(count, DiskSource);

    internal readonly record struct Reading(bool Present, double Value, uint CStatus)
    {
        public bool Valid => Present && CStatus is Pdh.CStatusValidData or Pdh.CStatusNewData;
    }

    internal sealed class DiskAccumulator
    {
        public bool Seen { get; set; }

        public Reading Idle { get; set; }

        public Reading Read { get; set; }

        public Reading Write { get; set; }

        public Reading Latency { get; set; }

        /// <summary>Forgets everything the last tick read, so a counter array that omits this disk leaves its reading absent instead of stale.</summary>
        public void BeginTick()
        {
            Seen = false;
            Idle = Read = Write = Latency = default;
        }
    }
}
