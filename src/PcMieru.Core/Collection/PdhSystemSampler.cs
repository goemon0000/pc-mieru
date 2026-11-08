using System.Diagnostics;
using System.Globalization;

namespace PcMieru.Core.Collection;

/// <summary>What the always-on PDH query yields for one tick.</summary>
internal sealed record PdhSystemReading(IReadOnlyList<DiskInfo> Disks, Value<double> CpuMhz);

/// <summary>
/// Physical disks (<c>\PhysicalDisk(*)</c>) and the CPU clock from one PDH query that stays open. PDH computes the rates
/// between its own two collections, so the first collection after start yields nothing and says so.
/// Counter paths are English (PdhAddEnglishCounterW), so a Japanese Windows needs no name table.
/// </summary>
internal sealed class PdhSystemSampler : IDisposable
{
    private const string DiskSource = "PDH PhysicalDisk";
    private const string ClockSource = "PDH Processor Information";
    private const double MinWindowMs = 200;

    private readonly PdhQuery _query = new();
    private readonly PdhArrayReader _reader = new();
    private readonly nint _idle;
    private readonly nint _read;
    private readonly nint _write;
    private readonly nint _latency;
    private readonly nint _frequency;
    private readonly nint _performance;
    private readonly Dictionary<string, DiskAccumulator> _disks = [];
    private readonly Dictionary<int, string> _models = [];
    private long _lastStamp;
    private bool _baselineValid;

    public PdhSystemSampler()
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

        // The first collection only starts PDH's clock. Doing it here lets the first tick already have rates.
        if (_query.Collect() == Pdh.Success)
        {
            _lastStamp = Stopwatch.GetTimestamp();
            _baselineValid = true;
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

        foreach (var d in _disks.Values)
        {
            d.Seen = false;
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
        return new PdhSystemReading(disks, ReadClock(primed));
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

        if (!primed || !TryReadTotal(_frequency, out var freq) || !TryReadTotal(_performance, out var perf))
        {
            return Value.NotCollected<double>(ClockSource, primed ? "counter returned no data" : "no baseline yet (first sample)");
        }

        // Task Manager's "Speed": the nominal frequency scaled by how far the cores are above or below it (boost goes past 100 %).
        var mhz = freq * perf / 100.0;
        return mhz > 0 && double.IsFinite(mhz)
            ? Value.Ok(mhz, ClockSource)
            : Value.NotCollected<double>(ClockSource, "counter returned no data");
    }

    private bool TryReadTotal(nint counter, out double value)
    {
        value = 0;
        if (!_reader.Read(counter))
        {
            return false;
        }

        for (var i = 0; i < _reader.Count; i++)
        {
            if (_reader.TryGet(i, out _, out var v, out var cstatus) && cstatus is Pdh.CStatusValidData or Pdh.CStatusNewData)
            {
                value = v;
                return true;
            }
        }

        return false;
    }

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

    private static PdhSystemReading Unavailable(string detail) =>
        new([], Value.Failed<double>(ClockSource, detail));

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
    }
}
