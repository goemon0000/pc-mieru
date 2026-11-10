using System.Runtime.InteropServices;

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
/// <item>Every byte is stamped with the time the EVENT happened (the ETW record's own timestamp), never the time it was
/// delivered. ETW hands events over in batches (the session's flush timer, 64 KB buffers), so dividing a byte difference by
/// the delivery time aliases: one reading holds a batch of events from 1.3 s and the next one from 0.4 s, and a steady
/// stream reads as 0, 2, 4.6, 4, 1.8, 6.4 MB/s although the totals are exact.</item>
/// <item>A reading is complete up to a horizon H: the committed totals hold every event with time before H, and none after
/// it. H is picked at <see cref="Snapshot"/> as the read time minus <see cref="LagTicks"/> rounded down to a
/// <see cref="SlotTicks"/> boundary, and never goes backwards. Events newer than H wait in a few 100 ms slots per entry
/// (<see cref="MaxPendingSlots"/>) and are folded into the committed totals as H passes them. The difference of two readings
/// divided by the difference of their horizons is then exact for the events in (H_previous, H], whatever the batching,
/// as long as everything older than H has been delivered by the time it is read (that is what the lag is for).</item>
/// <item>An event that arrives after H has already passed its time is "late": it is added to the committed totals at once
/// (the total stays exact, only the window it falls in is the next one) and counted in <see cref="LateEvents"/>. A rising
/// count means the lag is shorter than the session's real delivery latency.</item>
/// <item>An event time ahead of the clock (it cannot be, but a timestamp from another base would otherwise stay pending for
/// ever) is treated as happening now.</item>
/// </list>
/// </summary>
public sealed class NetMeterCounters : IDisposable
{
    /// <summary>A slot is 100 ms in 100 ns units: the resolution of the horizon and of the pending buckets.</summary>
    public const long SlotTicks = 1_000_000;

    /// <summary>
    /// How far behind the clock the horizon runs, in 100 ns units: the longest an event may take from happening to being
    /// delivered. Measured on Windows 11 build 26300 with the session flushing every
    /// <see cref="NetMeterEvents.FlushTimerMilliseconds"/> ms: 107 to 324 ms (1 to 3.2 flushes, ProcessTrace orders events across
    /// buffers), also with every logical processor loaded. 750 ms is more than twice the worst of those; SPEC 4.1 has the table.
    /// </summary>
    public const long LagTicks = 7_500_000;

    /// <summary>
    /// The most pending slots one entry keeps. The horizon runs <see cref="LagTicks"/> behind the clock, so an entry holds
    /// that many slots plus the time since the last read; a reader that stays away longer folds the oldest slots into the
    /// committed totals instead of growing (see <see cref="Count"/>).
    /// </summary>
    public const int MaxPendingSlots = 32;

    /// <summary>Bound on the entries (and so on the size of a reply). When it is reached the exited ones are dropped first.</summary>
    public const int MaxTrackedProcesses = NetMeterProtocol.MaxEntries;

    private readonly object _gate = new();
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly INetMeterProcessResolver _resolver;
    private readonly Func<long> _clock;
    private long _horizonSlot;
    private ulong _undecodable;
    private ulong _lostSend;
    private ulong _lostReceive;
    private ulong _lateEvents;
    private ulong _lateBytes;
    private bool _disposed;

    /// <param name="clock">
    /// A monotonic clock in 100 ns units. The reading's timestamps come from it, and the event times given to
    /// <see cref="Count"/> must be on the same base.
    /// </param>
    public NetMeterCounters(INetMeterProcessResolver resolver, Func<long> clock)
    {
        _resolver = resolver;
        _clock = clock;
        _horizonSlot = SlotOf(clock());
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

    /// <summary>Events that arrived after the horizon had passed their time, and their bytes. Not in the reply.</summary>
    public (ulong Events, ulong Bytes) Late
    {
        get
        {
            lock (_gate)
            {
                return (_lateEvents, _lateBytes);
            }
        }
    }

    /// <summary>How many events were late so far (see <see cref="Late"/>).</summary>
    public ulong LateEvents => Late.Events;

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

    /// <summary>The pending slots over all entries (for tests: it is bounded by the entries times <see cref="MaxPendingSlots"/>).</summary>
    public int PendingSlotCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var entry in _entries.Values)
                {
                    count += entry.Pending.Count;
                }

                return count;
            }
        }
    }

    /// <summary>Counts one decoded send or receive event that happened at <paramref name="eventTime"/> (the clock's base).</summary>
    public void Count(NetMeterDirection direction, uint pid, uint bytes, long eventTime)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

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

            var slot = SlotOf(Math.Min(eventTime, _clock()));
            if (slot < _horizonSlot)
            {
                entry.AddCommitted(direction, bytes);
                _lateEvents++;
                _lateBytes += bytes;
                return;
            }

            entry.AddPending(slot, direction, bytes);
            if (entry.Pending.Count > MaxPendingSlots)
            {
                // Nobody has read for longer than the pending buckets cover. Move the horizon past this entry's oldest slot,
                // which folds it (and, to keep one horizon for everybody, the same slots of all other entries) into the
                // totals. It stays below what the next read would pick, because the oldest slot is more than a lag old.
                AdvanceHorizon(entry.Pending[0].Index + 1);
            }
        }
    }

    /// <summary>An event that arrived but could not be decoded: its bytes are unknown, so only the count is kept.</summary>
    public void NoteUndecodable()
    {
        lock (_gate)
        {
            _undecodable++;
        }
    }

    public NetMeterReading Snapshot(ulong eventsLost, ulong buffersLost, bool sessionHealthy, uint errorCode)
    {
        lock (_gate)
        {
            var target = SlotOf(_clock() - LagTicks);
            if (target > _horizonSlot)
            {
                AdvanceHorizon(target);
            }

            var entries = new NetMeterEntry[_entries.Count];
            var i = 0;
            foreach (var (pid, entry) in _entries)
            {
                entries[i++] = new NetMeterEntry(pid, entry.Pin.CreationFileTime, entry.Send, entry.Receive);
            }

            return new NetMeterReading(_horizonSlot * SlotTicks, eventsLost, buffersLost, _undecodable, sessionHealthy, errorCode, entries);
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

    /// <summary>The slot a time falls in (rounded down, so a time before the origin does not land in slot 0).</summary>
    private static long SlotOf(long time) => time >= 0 ? time / SlotTicks : -((-time + SlotTicks - 1) / SlotTicks);

    private void AdvanceHorizon(long slot)
    {
        _horizonSlot = slot;
        foreach (var entry in _entries.Values)
        {
            entry.Commit(slot);
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

    /// <summary>The bytes of one 100 ms slot that is not yet behind the horizon.</summary>
    private struct Slot(long index)
    {
        public long Index = index;
        public ulong Send;
        public ulong Receive;
    }

    private sealed class Entry(INetMeterProcessPin pin)
    {
        /// <summary>Slots at or after the horizon, ascending by index. Almost always the last one is the one being added to.</summary>
        public List<Slot> Pending { get; } = [];

        public INetMeterProcessPin Pin { get; } = pin;

        /// <summary>Everything that happened before the horizon.</summary>
        public ulong Send;
        public ulong Receive;

        public void AddCommitted(NetMeterDirection direction, uint bytes)
        {
            if (direction == NetMeterDirection.Send)
            {
                Send += bytes;
            }
            else
            {
                Receive += bytes;
            }
        }

        public void AddPending(long slot, NetMeterDirection direction, uint bytes)
        {
            var i = Pending.Count - 1;
            while (i >= 0 && Pending[i].Index > slot)
            {
                i--;
            }

            if (i < 0 || Pending[i].Index != slot)
            {
                Pending.Insert(i + 1, new Slot(slot));
                i++;
            }

            ref var target = ref CollectionsMarshal.AsSpan(Pending)[i];
            if (direction == NetMeterDirection.Send)
            {
                target.Send += bytes;
            }
            else
            {
                target.Receive += bytes;
            }
        }

        /// <summary>Folds every pending slot before <paramref name="horizonSlot"/> into the committed totals.</summary>
        public void Commit(long horizonSlot)
        {
            var folded = 0;
            while (folded < Pending.Count && Pending[folded].Index < horizonSlot)
            {
                Send += Pending[folded].Send;
                Receive += Pending[folded].Receive;
                folded++;
            }

            if (folded > 0)
            {
                Pending.RemoveRange(0, folded);
            }
        }
    }
}
