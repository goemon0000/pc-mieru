using System.Diagnostics;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Collection;

/// <summary>One adapter's numbers as the engine counters give them. Engine value = sum over processes on that physical engine.</summary>
internal sealed class GpuAdapterUsage
{
    public double Busiest { get; set; }

    public int EngineInstances { get; set; }

    /// <summary>engine type to the busiest engine of that type.</summary>
    public Dictionary<string, double> ByType { get; } = [];

    public long? DedicatedUsed { get; set; }

    public long? SharedUsed { get; set; }
}

/// <summary>
/// Folds the PDH GPU Engine array into per-adapter and per-process numbers (SPEC 4, codex-r2 section 4). The identity of an
/// engine is (adapter LUID, phys, eng): the same engine seen through several processes is summed, different engines are never
/// added together, and the representative value is the busiest engine, for the adapter and for each process.
/// Pure and allocation-light: dictionaries are reused between ticks.
/// </summary>
internal sealed class GpuAggregator
{
    private readonly Dictionary<GpuEngineKey, EngineSum> _engines = [];
    private readonly Dictionary<(uint Pid, GpuEngineKey Key), double> _perProcess = [];
    private readonly Dictionary<ulong, GpuAdapterUsage> _adapters = [];
    private readonly Dictionary<uint, double> _processBusiest = [];
    private readonly Dictionary<string, string> _typeNames = [];
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _typeLookup;

    public GpuAggregator() => _typeLookup = _typeNames.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Instances whose name was in a format this parser does not know. They are left out, and counted so the result can say so.</summary>
    public int Unrecognized { get; private set; }

    /// <summary>Instances PDH could not compute a value for (a process that ended mid-interval, for example).</summary>
    public int Invalid { get; private set; }

    public IReadOnlyDictionary<ulong, GpuAdapterUsage> Adapters => _adapters;

    public IReadOnlyDictionary<uint, double> ProcessBusiest => _processBusiest;

    public void Begin()
    {
        _engines.Clear();
        _perProcess.Clear();
        _adapters.Clear();
        _processBusiest.Clear();
        Unrecognized = 0;
        Invalid = 0;
    }

    public void AddEngine(ReadOnlySpan<char> instance, double value, uint cstatus)
    {
        if (!GpuCounterName.TryParseEngine(instance, out var pid, out var key, out var type))
        {
            Unrecognized++;
            return;
        }

        if (cstatus is not (Pdh.CStatusValidData or Pdh.CStatusNewData) || !double.IsFinite(value) || value < 0)
        {
            Invalid++;
            return;
        }

        var typeName = _typeLookup.TryGetValue(type, out var known) ? known : Intern(type);
        _engines.TryGetValue(key, out var sum);
        _engines[key] = new EngineSum(sum.Value + value, typeName);
        _perProcess.TryGetValue((pid, key), out var mine);
        _perProcess[(pid, key)] = mine + value;
    }

    public void AddAdapterMemory(ReadOnlySpan<char> instance, double value, uint cstatus, bool dedicated)
    {
        if (!GpuCounterName.TryParseAdapter(instance, out var luid, out _))
        {
            Unrecognized++;
            return;
        }

        if (cstatus is not (Pdh.CStatusValidData or Pdh.CStatusNewData) || !double.IsFinite(value) || value < 0)
        {
            Invalid++;
            return;
        }

        var usage = Adapter(luid);
        var bytes = (long)Math.Min(value, long.MaxValue);
        if (dedicated)
        {
            usage.DedicatedUsed = (usage.DedicatedUsed ?? 0) + bytes;
        }
        else
        {
            usage.SharedUsed = (usage.SharedUsed ?? 0) + bytes;
        }
    }

    /// <summary>Call once after all instances were added.</summary>
    public void Finish()
    {
        foreach (var (key, sum) in _engines)
        {
            var value = Math.Min(sum.Value, 100.0);
            var usage = Adapter(key.Luid);
            usage.EngineInstances++;
            usage.Busiest = Math.Max(usage.Busiest, value);
            usage.ByType[sum.Type] = Math.Max(usage.ByType.GetValueOrDefault(sum.Type), value);
        }

        foreach (var ((pid, _), value) in _perProcess)
        {
            var capped = Math.Min(value, 100.0);
            _processBusiest[pid] = Math.Max(_processBusiest.GetValueOrDefault(pid), capped);
        }
    }

    private GpuAdapterUsage Adapter(ulong luid)
    {
        if (!_adapters.TryGetValue(luid, out var usage))
        {
            usage = new GpuAdapterUsage();
            _adapters[luid] = usage;
        }

        return usage;
    }

    private string Intern(ReadOnlySpan<char> type)
    {
        var text = type.ToString();
        if (_typeNames.Count < 64)
        {
            _typeNames[text] = text;
        }

        return text;
    }

    private readonly record struct EngineSum(double Value, string Type);
}

/// <summary>What the GPU part of a tick yields.</summary>
internal sealed record GpuReading(
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyDictionary<uint, double> ProcessBusiest,
    bool ProcessesCollected,
    string? ProcessDetail,
    Value<int> State)
{
    public static GpuReading Off(IReadOnlyList<GpuInfo> gpus) =>
        new(gpus, new Dictionary<uint, double>(), false, GpuSampler.OffDetail, GpuSampler.ListState(gpus, ValueStatus.NotCollected, GpuSampler.OffDetail));
}

/// <summary>
/// GPU utilization and memory from PDH (<c>GPU Engine</c>, <c>GPU Adapter Memory</c>), with the adapter identity from DXGI.
/// The query exists only while something asks for GPU numbers (<see cref="ISamplerControl.GpuActive"/>): when nobody does,
/// no collection happens, and after a while the query is closed. After a gap the first collection restarts PDH's window and
/// reads as "not collected" instead of being averaged over the gap.
/// </summary>
internal sealed class GpuSampler : IDisposable
{
    private const string EngineSource = "PDH GPU Engine";
    private const string MemorySource = "PDH GPU Adapter Memory";
    private const string AdapterSource = "DXGI";
    private const double MinWindowMs = 200;
    private static readonly TimeSpan KeepQueryWhenIdle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(60);

    private readonly GpuAggregator _aggregator = new();
    private readonly PdhArrayReader _reader = new();
    private PdhQuery? _query;
    private nint _engine;
    private nint _dedicated;
    private nint _shared;
    private IReadOnlyList<GpuAdapter>? _adapters;
    private long _lastCollect;
    private long _idleSince;
    private long _failedAt;
    private string? _unsupported;
    private bool _baselineValid;
    private IReadOnlyList<GpuInfo> _lastGpus = [];
    private IReadOnlyList<GpuInfo> _primedGpus = [];

    /// <summary>Forget the baseline (resume after a pause or sleep). The learned adapter list is kept.</summary>
    public void Reset() => _baselineValid = false;

    public GpuReading Sample(bool active)
    {
        if (!active)
        {
            return SampleOff();
        }

        _idleSince = 0;
        _adapters ??= GpuAdapters.Enumerate();
        if (!EnsureQuery())
        {
            var detail = _unsupported ?? "GPU counters could not be opened";
            _lastGpus = Interim(_primedGpus, _adapters, ValueStatus.Unsupported, detail);
            return new GpuReading(_lastGpus, new Dictionary<uint, double>(), false, detail, ListState(_lastGpus, ValueStatus.Unsupported, detail));
        }

        var primed = _baselineValid && Stopwatch.GetElapsedTime(_lastCollect).TotalMilliseconds >= MinWindowMs;
        var status = _query!.Collect();
        if (status != Pdh.Success)
        {
            _baselineValid = false;
            var detail = $"PdhCollectQueryData 0x{status:X8}";
            _lastGpus = Interim(_primedGpus, _adapters, ValueStatus.Error, detail);
            return new GpuReading(_lastGpus, new Dictionary<uint, double>(), false, detail, ListState(_lastGpus, ValueStatus.Error, detail));
        }

        _lastCollect = Stopwatch.GetTimestamp();
        _baselineValid = true;
        if (!primed)
        {
            const string first = "no baseline yet (first GPU sample)";
            _lastGpus = Interim(_primedGpus, _adapters, ValueStatus.NotCollected, first);
            return new GpuReading(_lastGpus, new Dictionary<uint, double>(), false, first, ListState(_lastGpus, ValueStatus.NotCollected, first));
        }

        _aggregator.Begin();
        var engineOk = ReadInto(_engine, (n, v, s) => _aggregator.AddEngine(n, v, s));
        var engineStatus = _reader.Status;
        _ = ReadInto(_dedicated, (n, v, s) => _aggregator.AddAdapterMemory(n, v, s, dedicated: true));
        _ = ReadInto(_shared, (n, v, s) => _aggregator.AddAdapterMemory(n, v, s, dedicated: false));
        _aggregator.Finish();

        if (!engineOk)
        {
            var unreadable = $"GPU Engine counter could not be read (PDH 0x{engineStatus:X8})";
            _lastGpus = Interim(_primedGpus, _adapters, ValueStatus.Error, unreadable);
            return new GpuReading(_lastGpus, new Dictionary<uint, double>(), false, unreadable, ListState(_lastGpus, ValueStatus.Error, unreadable));
        }

        _lastGpus = _primedGpus = Describe(_adapters, _aggregator, primed: true, null, ValueStatus.NotCollected);
        var note = _aggregator.Unrecognized > 0 ? $"{_aggregator.Unrecognized} GPU instance(s) in an unknown name format were ignored" : null;
        return new GpuReading(_lastGpus, new Dictionary<uint, double>(_aggregator.ProcessBusiest), true, note, Value.Ok(_lastGpus.Count, EngineSource));
    }

    public void Dispose() => CloseQuery();

    /// <summary>
    /// One GpuInfo per adapter DXGI lists (minus software adapters) plus any adapter that only PDH knows and that has engine
    /// activity. Pure so it can be tested with a hand-filled aggregator.
    /// </summary>
    internal static IReadOnlyList<GpuInfo> Describe(
        IReadOnlyList<GpuAdapter> adapters,
        GpuAggregator? data,
        bool primed,
        string? missingDetail,
        ValueStatus missingStatus)
    {
        var result = new List<GpuInfo>();
        var seen = new HashSet<ulong>();
        foreach (var adapter in adapters)
        {
            if (adapter.IsSoftware)
            {
                continue;
            }

            // Once counters have been read, a DXGI adapter that has neither engine nor memory instances is not a GPU a user
            // can look at (a second DXGI entry for the same card, a remote-display stub): listing it would be a row of dashes.
            GpuAdapterUsage? usage = null;
            if (primed && data is not null && !data.Adapters.TryGetValue(adapter.Luid, out usage))
            {
                continue;
            }

            if (usage is { EngineInstances: 0, DedicatedUsed: null, SharedUsed: null })
            {
                continue;
            }

            seen.Add(adapter.Luid);
            result.Add(Info(adapter.Id, adapter.Name, adapter.DedicatedBytes, data, usage, primed, missingDetail, missingStatus));
        }

        if (primed && data is not null)
        {
            var known = adapters.Select(a => a.Luid).ToHashSet();
            foreach (var (luid, usage) in data.Adapters)
            {
                if (!known.Contains(luid) && usage.EngineInstances > 0 && seen.Add(luid))
                {
                    result.Add(Info(GpuCounterName.AdapterId(luid), "GPU " + GpuCounterName.AdapterId(luid), 0, data, usage, primed, missingDetail, missingStatus));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The GPU list to show on a tick with no fresh PDH data (counters not open, a collect error, the first sample before a
    /// baseline). Once a primed sample has learned the real adapters, that list is reused with every live number replaced by
    /// the failure reason, rather than re-listing every DXGI adapter: a phantom second DXGI entry for a card (distinct LUID,
    /// no engine and no memory instance, so dropped once primed) would otherwise flicker back in and show a 3rd GPU. A real
    /// adapter always has a memory instance, so it is never in a phantom's shoes and is never dropped. Before anything has
    /// primed there is nothing better than what DXGI reports, so the full list is shown (names now, numbers once primed).
    /// Pure so the behaviour can be tested without PDH.
    /// </summary>
    internal static IReadOnlyList<GpuInfo> Interim(
        IReadOnlyList<GpuInfo> lastPrimed,
        IReadOnlyList<GpuAdapter> adapters,
        ValueStatus status,
        string detail) =>
        lastPrimed.Count > 0
            ? Failed(lastPrimed, status, detail)
            : Describe(adapters, null, primed: false, detail, status);

    private static GpuInfo Info(string id, string name, long dedicatedTotal, GpuAggregator? data, GpuAdapterUsage? usage, bool primed, string? missingDetail, ValueStatus missingStatus)
    {
        Value<double> Missing(string source) => Value.Missing<double>(missingStatus, source, missingDetail);

        Value<double> utilization;
        IReadOnlyList<GpuEngineInfo> engines;
        if (usage is { EngineInstances: > 0 })
        {
            var note = data!.Unrecognized > 0 ? $"{data.Unrecognized} instance(s) in an unknown name format were ignored" : null;
            utilization = new Value<double>(usage.Busiest, ValueStatus.Ok, EngineSource, note);
            engines = usage.ByType
                .OrderBy(static t => t.Key == "3D" ? 0 : 1)
                .ThenBy(static t => t.Key, StringComparer.Ordinal)
                .Select(static t => new GpuEngineInfo(t.Key, Value.Ok(t.Value, EngineSource)))
                .ToArray();
        }
        else if (primed && missingDetail is null && usage is not null)
        {
            // The adapter's memory counters work but no process has an engine context on it: nothing can be running there.
            utilization = new Value<double>(0, ValueStatus.Ok, EngineSource, "no process has a context on this adapter's engines");
            engines = [];
        }
        else
        {
            utilization = Missing(EngineSource);
            engines = [];
        }

        Value<long> Bytes(long? used, string detail) =>
            used is { } u
                ? Value.Ok(u, MemorySource)
                : Value.Missing<long>(missingDetail is not null ? missingStatus : ValueStatus.NotCollected, MemorySource, missingDetail ?? detail);

        var total = dedicatedTotal > 0
            ? Value.Ok(dedicatedTotal, "DXGI")
            : Value.Unsupported<long>("DXGI", "adapter reports no dedicated video memory");

        return new GpuInfo(
            id,
            name,
            utilization,
            engines,
            Bytes(usage?.DedicatedUsed, "no memory counter for this adapter"),
            total,
            Bytes(usage?.SharedUsed, "no memory counter for this adapter"),
            Value.NotCollected<double>("SensorHost", "not read yet"),
            Value.NotCollected<double>("SensorHost", "not read yet"),
            Value.NotCollected<double>("SensorHost", "not read yet"),
            Value.NotCollected<double>("SensorHost", "not read yet"));
    }

    private GpuReading SampleOff()
    {
        _baselineValid = false;
        if (_query is not null)
        {
            _idleSince = _idleSince == 0 ? Stopwatch.GetTimestamp() : _idleSince;
            if (Stopwatch.GetElapsedTime(_idleSince) > KeepQueryWhenIdle)
            {
                CloseQuery();
            }
        }

        // The adapters seen last time stay in the list (names and memory sizes do not change), but with no live numbers.
        return GpuReading.Off(Failed(_lastGpus, ValueStatus.NotCollected, OffDetail));
    }

    internal const string OffDetail = "GPU sampling is off (no GPU view is open)";

    /// <summary>
    /// What the GPU list itself says. Adapters that are listed make the list Ok whatever their live numbers are (those carry their own
    /// status). An empty list is "none" only after a real sample; before that, or when the counters cannot be read, it says why.
    /// </summary>
    internal static Value<int> ListState(IReadOnlyList<GpuInfo> gpus, ValueStatus whenEmpty, string detail) =>
        gpus.Count > 0 ? Value.Ok(gpus.Count, AdapterSource) : Value.Missing<int>(whenEmpty, EngineSource, detail);

    /// <summary>
    /// The adapters already known, with every live number replaced by one status and reason (sampling is off, the counters
    /// timed out). Names and total memory are facts that do not change, so they stay.
    /// </summary>
    internal static IReadOnlyList<GpuInfo> Failed(IReadOnlyList<GpuInfo> known, ValueStatus status, string detail) =>
        known.Select(g => g with
        {
            UtilizationPercent = Value.Missing<double>(status, EngineSource, detail),
            Engines = [],
            DedicatedUsedBytes = Value.Missing<long>(status, MemorySource, detail),
            SharedUsedBytes = Value.Missing<long>(status, MemorySource, detail),
            TemperatureC = Value.Missing<double>(status, "SensorHost", detail),
            PowerWatts = Value.Missing<double>(status, "SensorHost", detail),
            FanPercent = Value.Missing<double>(status, "SensorHost", detail),
            ClockMhz = Value.Missing<double>(status, "SensorHost", detail),
        }).ToArray();

    private bool EnsureQuery()
    {
        if (_query is not null)
        {
            return true;
        }

        if (_failedAt != 0 && Stopwatch.GetElapsedTime(_failedAt) < RetryAfterFailure)
        {
            return false;
        }

        var query = new PdhQuery();
        if (!query.IsOpen)
        {
            query.Dispose();
            Fail($"PdhOpenQuery 0x{query.OpenStatus:X8}");
            return false;
        }

        var engine = query.Add(@"\GPU Engine(*)\Utilization Percentage", out var status);
        if (engine == 0)
        {
            query.Dispose();
            Fail($"the GPU Engine counter set is not available (PDH 0x{status:X8})");
            return false;
        }

        _engine = engine;
        _dedicated = query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage", out _);
        _shared = query.Add(@"\GPU Adapter Memory(*)\Shared Usage", out _);
        _query = query;
        _unsupported = null;
        _failedAt = 0;
        _baselineValid = false;
        return true;
    }

    private void Fail(string reason)
    {
        _unsupported = reason;
        _failedAt = Stopwatch.GetTimestamp();
    }

    private void CloseQuery()
    {
        _query?.Dispose();
        _query = null;
        _engine = _dedicated = _shared = 0;
        _baselineValid = false;
        _idleSince = 0;
    }

    private bool ReadInto(nint counter, Action<ReadOnlySpan<char>, double, uint> add)
    {
        if (counter == 0 || !_reader.Read(counter))
        {
            return false;
        }

        for (var i = 0; i < _reader.Count; i++)
        {
            if (_reader.TryGet(i, out var name, out var value, out var cstatus))
            {
                add(name, value, cstatus);
            }
        }

        return true;
    }
}

/// <summary>Adds the vendor-API sensors (temperature, power, fan, clock) onto GPU infos by adapter id, falling back to the name.</summary>
internal static class GpuSensorMerge
{
    public static IReadOnlyList<GpuInfo> Apply(IReadOnlyList<GpuInfo> gpus, Value<IReadOnlyList<GpuSensorReading>> sensors)
    {
        if (gpus.Count == 0)
        {
            return gpus;
        }

        var result = new GpuInfo[gpus.Count];
        for (var i = 0; i < gpus.Count; i++)
        {
            var gpu = gpus[i];
            if (!sensors.IsOk || sensors.Data is null)
            {
                // The sensor client's own answer (Unsupported, Timeout, ...) is what the user should read.
                var status = sensors.IsOk ? ValueStatus.Error : sensors.Status;
                var detail = sensors.IsOk ? "the sensor client returned no data" : sensors.Detail;
                Value<double> same() => Value.Missing<double>(status, sensors.Source, detail);
                result[i] = gpu with { TemperatureC = same(), PowerWatts = same(), FanPercent = same(), ClockMhz = same(), TemperatureLimitC = same() };
                continue;
            }

            var match = Find(sensors.Data, gpu);
            if (match is null)
            {
                Value<double> none() => Value.Unsupported<double>(sensors.Source, "no sensor reading for this adapter");
                result[i] = gpu with { TemperatureC = none(), PowerWatts = none(), FanPercent = none(), ClockMhz = none(), TemperatureLimitC = none() };
                continue;
            }

            result[i] = gpu with
            {
                TemperatureC = match.TemperatureC,
                PowerWatts = match.PowerWatts,
                FanPercent = match.FanPercent,
                ClockMhz = match.ClockMhz,
                TemperatureLimitC = match.Extras?.TemperatureLimitC ?? Value.NotCollected<double>("none"),
            };
        }

        return result;
    }

    private static GpuSensorReading? Find(IReadOnlyList<GpuSensorReading> readings, GpuInfo gpu)
    {
        foreach (var r in readings)
        {
            if (string.Equals(r.Id, gpu.Id, StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }

        foreach (var r in readings)
        {
            if (string.Equals(r.Name.Trim(), gpu.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return r;
            }
        }

        return null;
    }
}
