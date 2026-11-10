using PcMieru.Wire;

namespace PcMieru.Core.Collection;

/// <summary>One process's network rates over the last window, in bytes per second.</summary>
internal readonly record struct NetRates(Value<double> Send, Value<double> Receive)
{
    public static NetRates Both(Value<double> value) => new(value, value);
}

/// <summary>
/// Turns the meter's cumulative per-process counters into bytes per second (SPEC 4.1). It is a pure function of the readings
/// it is given, so every rule below is tested without a meter.
/// <list type="bullet">
/// <item>The elapsed time is the METER's timestamp difference (the time up to which its counters are complete), never this
/// process's clock: the reading and the arrival of the reply are not the same instant. (The meter stamps bytes with the time
/// of the event, not of its delivery, and reports a horizon a fixed lag behind its clock, so this difference is the span of
/// events the counters cover however the session batched them.)</item>
/// <item>The first reading, the first after <see cref="Reset"/> and the first of a new meter run (another generation) are only a
/// baseline: NotCollected, never 0.</item>
/// <item>A process that was not alive at the previous reading is NotCollected (new): its counter does not cover the whole window.</item>
/// <item>A process that was alive at both and has no entry in either had no traffic: Ok(0). That is a measurement, not a gap.</item>
/// <item>Events lost by Windows (or events whose layout could not be read) in the window make every rate an Error with the count:
/// a lower bound is not a measurement and is never reported as one.</item>
/// <item>A counter that went backwards is NotCollected.</item>
/// <item>The meter's horizon moves in 100 ms steps, so two readings less than that apart can carry the same timestamp (rare:
/// the app samples about once a second). Then no time has passed that the counters know of, and the rates of the last window
/// are repeated (marked in the detail) instead of computing over zero seconds; a process that is not in them is NotCollected (new).</item>
/// </list>
/// Not thread-safe: the snapshot source calls it from the tick only.
/// </summary>
internal sealed class NetRateCalculator
{
    internal const string DetailNoBaseline = ProcessDeltaEngine.DetailNoBaseline;
    internal const string DetailNewProcess = ProcessDeltaEngine.DetailNewProcess;
    internal const string DetailBackwards = ProcessDeltaEngine.DetailBackwards;
    internal const string DetailRepeated = "same as the previous sample: the meter has no newer counters yet";

    private const double TicksPerSecond = 10_000_000;

    private readonly record struct Totals(ulong Send, ulong Receive);

    private Dictionary<ProcessKey, Totals> _previous = new(256);
    private Dictionary<ProcessKey, Totals> _scratch = new(256);
    private HashSet<ProcessKey> _alive = new(512);
    private Dictionary<ProcessKey, NetRates> _lastRates = new(512);
    private Dictionary<ProcessKey, NetRates> _nextRates = new(512);
    private bool _hasPrevious;
    private bool _hasRates;
    private long _generation;
    private long _ticks;
    private ulong _lost;
    private ulong _undecodable;

    public bool HasBaseline => _hasPrevious;

    /// <summary>Drops the baseline: resume from sleep, un-pause, explicit request.</summary>
    public void Reset()
    {
        _previous.Clear();
        _alive.Clear();
        _lastRates.Clear();
        _hasPrevious = false;
        _hasRates = false;
    }

    /// <summary>Fills <paramref name="output"/> (same order and length as <paramref name="alive"/>, the processes alive now).</summary>
    public void Compute(NetMeterReading reading, long generation, IReadOnlyList<ProcessKey> alive, Span<NetRates> output)
    {
        if (output.Length < alive.Count)
        {
            throw new ArgumentException("output is shorter than alive", nameof(output));
        }

        if (!_hasPrevious || generation != _generation || reading.TimestampTicks < _ticks)
        {
            // A new meter run (its counters start at zero and its clock somewhere else), or a clock that went back: nothing
            // before this reading can be compared with it.
            Remember(reading, generation, alive);
            _lastRates.Clear();
            _hasRates = false;
            Fill(output, alive.Count, NetRates.Both(NotCollected(DetailNoBaseline)));
            return;
        }

        var ticks = reading.TimestampTicks - _ticks;
        if (ticks == 0)
        {
            Repeat(alive, output);
            return;
        }

        var lost = Saturating(reading.EventsLost, reading.BuffersLost);
        var lostNow = lost > _lost ? lost - _lost : 0UL;
        var undecodableNow = reading.Undecodable > _undecodable ? reading.Undecodable - _undecodable : 0UL;
        if (lostNow > 0 || undecodableNow > 0)
        {
            var failed = NetRates.Both(Value.Failed<double>(NetMeterValues.Source, LostDetail(lostNow, undecodableNow)));
            Fill(output, alive.Count, failed);
            Remember(reading, generation, alive);
            _lastRates.Clear();
            foreach (var key in alive)
            {
                _lastRates[key] = failed;
            }

            _hasRates = true;
            return;
        }

        var seconds = ticks / TicksPerSecond;
        Index(reading, _scratch);
        _nextRates.Clear();
        for (var i = 0; i < alive.Count; i++)
        {
            var key = alive[i];
            NetRates rates;
            if (!_alive.Contains(key))
            {
                rates = NetRates.Both(NotCollected(DetailNewProcess));
            }
            else
            {
                _ = _previous.TryGetValue(key, out var before);
                _ = _scratch.TryGetValue(key, out var after);
                rates = new NetRates(Rate(before.Send, after.Send, seconds), Rate(before.Receive, after.Receive, seconds));
            }

            output[i] = rates;
            _nextRates[key] = rates;
        }

        (_previous, _scratch) = (_scratch, _previous);
        (_lastRates, _nextRates) = (_nextRates, _lastRates);
        _alive.Clear();
        _alive.UnionWith(alive);
        _ticks = reading.TimestampTicks;
        _lost = lost;
        _undecodable = reading.Undecodable;
        _hasRates = true;
    }

    private static Value<double> Rate(ulong before, ulong after, double seconds) =>
        after < before
            ? NotCollected(DetailBackwards)
            : Value.Ok((after - before) / seconds, NetMeterValues.Source);

    private void Repeat(IReadOnlyList<ProcessKey> alive, Span<NetRates> output)
    {
        for (var i = 0; i < alive.Count; i++)
        {
            output[i] = _lastRates.TryGetValue(alive[i], out var last)
                ? Mark(last)
                : NetRates.Both(NotCollected(_hasRates ? DetailNewProcess : DetailNoBaseline));
        }
    }

    /// <summary>A repeated Ok value says so; a repeated gap or error keeps its own words.</summary>
    private static NetRates Mark(NetRates rates) => new(Mark(rates.Send), Mark(rates.Receive));

    private static Value<double> Mark(Value<double> value) =>
        value.Status == ValueStatus.Ok ? value with { Detail = DetailRepeated } : value;

    private void Remember(NetMeterReading reading, long generation, IReadOnlyList<ProcessKey> alive)
    {
        Index(reading, _previous);
        _alive.Clear();
        _alive.UnionWith(alive);
        _generation = generation;
        _ticks = reading.TimestampTicks;
        _lost = Saturating(reading.EventsLost, reading.BuffersLost);
        _undecodable = reading.Undecodable;
        _hasPrevious = true;
    }

    private static void Index(NetMeterReading reading, Dictionary<ProcessKey, Totals> into)
    {
        into.Clear();
        foreach (var entry in reading.Entries)
        {
            into[new ProcessKey(entry.Pid, entry.CreationFileTime)] = new Totals(entry.SendBytes, entry.ReceiveBytes);
        }
    }

    private static void Fill(Span<NetRates> output, int count, NetRates value)
    {
        for (var i = 0; i < count; i++)
        {
            output[i] = value;
        }
    }

    private static Value<double> NotCollected(string detail) => Value.NotCollected<double>(NetMeterValues.Source, detail);

    private static ulong Saturating(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;

    private static string LostDetail(ulong lost, ulong undecodable)
    {
        if (lost > 0 && undecodable > 0)
        {
            return $"Windows dropped {lost} network events and {undecodable} could not be read in this window; the counters are incomplete";
        }

        return lost > 0
            ? $"Windows dropped {lost} network events in this window; the counters are incomplete"
            : $"{undecodable} network events could not be read in this window; the counters are incomplete";
    }
}
