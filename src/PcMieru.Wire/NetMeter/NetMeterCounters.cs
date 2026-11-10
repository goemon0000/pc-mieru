namespace PcMieru.Wire;

/// <summary>A process the meter holds open, which is what keeps its pid from being handed to another process.</summary>
public interface INetMeterProcessPin : IDisposable
{
    long CreationFileTime { get; }

    bool HasExited { get; }
}

/// <summary>Opens a process at first sight. Null when it cannot be opened (it already exited, or it is not a process).</summary>
public interface INetMeterProcessResolver
{
    INetMeterProcessPin? Pin(uint pid);
}

/// <summary>
/// The cumulative send and receive bytes of every process since the meter started, keyed by (pid, creation time). Pure
/// bookkeeping with no Windows calls of its own, so the rules that matter are tested without ETW:
/// <list type="bullet">
/// <item>The key is the pid plus the creation time read from a handle the meter keeps open. While that handle exists the
/// pid cannot be given to another process, so bytes are never added to a reused pid; when the process is gone the entry is
/// dropped and a later process with the same pid starts from zero under its own creation time.</item>
/// <item>Bytes of a process that cannot be opened (it exited before its event arrived, or pid 0) go into one separate
/// "lost to exited processes" total. They are never given to somebody else, and pid 4 is just a process like any other.</item>
/// <item>The reading's timestamp is the horizon up to which the counters are complete. ETW hands the events over in batches
/// about once a second (the session's FlushTimer), so "now" would sometimes include a batch and sometimes not, and a
/// 0.5 second reader would see 0 then double. The horizon is the time of the latest delivery, or, when nothing has arrived
/// for longer than a flush can take (<see cref="IdleLagTicks"/>), the read time minus that lag: by then everything that
/// happened earlier has surely been delivered, so a quiet connection decays to zero instead of repeating its last rate.
/// It never goes backwards.</item>
/// </list>
/// </summary>
public sealed class NetMeterCounters : IDisposable
{
    /// <summary>1.5 s in 100 ns units: a one second flush plus slack for a busy machine.</summary>
    public const long IdleLagTicks = 15_000_000;

    /// <summary>Bound on the entries (and so on the size of a reply). When it is reached the exited ones are dropped first.</summary>
    public const int MaxTrackedProcesses = NetMeterProtocol.MaxEntries;

    private readonly object _gate = new();
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly INetMeterProcessResolver _resolver;
    private readonly Func<long> _clock;
    private long _lastDelivery;
    private long _horizon;
    private ulong _undecodable;
    private ulong _lostSend;
    private ulong _lostReceive;
    private bool _disposed;

    /// <param name="clock">A monotonic clock in 100 ns units (the reading's timestamps come from it).</param>
    public NetMeterCounters(INetMeterProcessResolver resolver, Func<long> clock)
    {
        _resolver = resolver;
        _clock = clock;
        _lastDelivery = _horizon = clock();
    }

    /// <summary>Bytes of events whose process could not be opened, by direction. For diagnostics and tests; not in the reply.</summary>
    public (ulong Send, ulong Receive) LostToExitedProcesses
    {
        get
        {
            lock (_gate)
            {
                return (_lostSend, _lostReceive);
            }
        }
    }

    public int ProcessCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Counts one decoded send or receive event.</summary>
    public void Count(NetMeterDirection direction, uint pid, uint bytes)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastDelivery = _clock();
            var entry = Find(pid);
            if (entry is null)
            {
                if (direction == NetMeterDirection.Send)
                {
                    _lostSend += bytes;
                }
                else
                {
                    _lostReceive += bytes;
                }

                return;
            }

            if (direction == NetMeterDirection.Send)
            {
                entry.Send += bytes;
            }
            else
            {
                entry.Receive += bytes;
            }
        }
    }

    /// <summary>An event of the provider that is not counted (or could not be decoded) still proves a batch was delivered.</summary>
    public void NoteDelivery(bool undecodable = false)
    {
        lock (_gate)
        {
            _lastDelivery = _clock();
            if (undecodable)
            {
                _undecodable++;
            }
        }
    }

    public NetMeterReading Snapshot(ulong eventsLost, ulong buffersLost, bool sessionHealthy, uint errorCode)
    {
        lock (_gate)
        {
            var horizon = Math.Max(_lastDelivery, _clock() - IdleLagTicks);
            if (horizon < _horizon)
            {
                horizon = _horizon;
            }

            _horizon = horizon;
            var entries = new NetMeterEntry[_entries.Count];
            var i = 0;
            foreach (var (pid, entry) in _entries)
            {
                entries[i++] = new NetMeterEntry(pid, entry.Pin.CreationFileTime, entry.Send, entry.Receive);
            }

            return new NetMeterReading(horizon, eventsLost, buffersLost, _undecodable, sessionHealthy, errorCode, entries);
        }
    }

    /// <summary>Drops the entries whose process has exited and lets go of their handles. Returns how many were dropped.</summary>
    public int RemoveExited()
    {
        lock (_gate)
        {
            return DropExited();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Pin.Dispose();
            }

            _entries.Clear();
        }
    }

    private Entry? Find(uint pid)
    {
        if (_entries.TryGetValue(pid, out var found))
        {
            return found;
        }

        if (pid == 0)
        {
            return null;
        }

        if (_entries.Count >= MaxTrackedProcesses)
        {
            DropExited();
            if (_entries.Count >= MaxTrackedProcesses)
            {
                return null;
            }
        }

        var pin = _resolver.Pin(pid);
        if (pin is null)
        {
            return null;
        }

        var entry = new Entry(pin);
        _entries[pid] = entry;
        return entry;
    }

    private int DropExited()
    {
        List<uint>? gone = null;
        foreach (var (pid, entry) in _entries)
        {
            if (entry.Pin.HasExited)
            {
                (gone ??= []).Add(pid);
            }
        }

        if (gone is null)
        {
            return 0;
        }

        foreach (var pid in gone)
        {
            _entries[pid].Pin.Dispose();
            _entries.Remove(pid);
        }

        return gone.Count;
    }

    private sealed class Entry(INetMeterProcessPin pin)
    {
        public INetMeterProcessPin Pin { get; } = pin;

        public ulong Send;
        public ulong Receive;
    }
}
