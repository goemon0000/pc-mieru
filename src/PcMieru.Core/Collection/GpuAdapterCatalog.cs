namespace PcMieru.Core.Collection;

/// <summary>
/// The DXGI adapter list of <see cref="GpuSampler"/>, read when GPU sampling first starts and read again when it can no longer be right: a graphics
/// driver restart (an update, a crash and recovery, a card removed and put back) gives an adapter a new LUID, and PDH reports the new one at once. A list
/// that is never refreshed would show that card as "GPU &lt;luid&gt;" with no name and no memory size for the rest of the process, and a recording could
/// not tell it from a new device. The list is re-read when PDH reports a LUID that was not in it, or when a LUID PDH reported last time is gone; at most
/// once per <see cref="MinInterval"/>, and a LUID that was already looked for once does not ask again (an adapter only PDH knows would otherwise make
/// every tenth second a DXGI call). It runs on the sampler's own thread, the same one that took the first list.
/// </summary>
internal sealed class GpuAdapterCatalog(Func<IReadOnlyList<GpuAdapter>> enumerate, Func<long> nowMs)
{
    /// <summary>The least time between two reads of the list.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    private readonly HashSet<ulong> _triedUnknown = [];
    private IReadOnlyList<GpuAdapter>? _adapters;
    private HashSet<ulong> _lastPdh = [];
    private long _enumeratedAtMs;
    private bool _vanishedPending;

    /// <summary>How many times the list has been read (the first read included).</summary>
    public int Enumerations { get; private set; }

    /// <summary>The list: read on first use.</summary>
    public IReadOnlyList<GpuAdapter> Adapters
    {
        get
        {
            if (_adapters is null)
            {
                Read();
            }

            return _adapters!;
        }
    }

    /// <summary>
    /// Tells the catalog which LUIDs PDH reported in this sample. Returns true when the list was read again (and <see cref="Adapters"/> is new).
    /// </summary>
    public bool Observe(IReadOnlyCollection<ulong> pdhLuids)
    {
        var known = Adapters.Select(static a => a.Luid).ToHashSet();
        var current = new HashSet<ulong>(pdhLuids);
        var unknown = false;
        foreach (var luid in current)
        {
            if (!known.Contains(luid) && !_triedUnknown.Contains(luid))
            {
                unknown = true;
            }
        }

        var vanishedNow = false;
        foreach (var luid in _lastPdh)
        {
            if (!current.Contains(luid))
            {
                vanishedNow = true;
            }
        }

        _lastPdh = current;
        _vanishedPending |= vanishedNow;
        if (!unknown && !_vanishedPending)
        {
            return false;
        }

        if (nowMs() - _enumeratedAtMs < (long)MinInterval.TotalMilliseconds)
        {
            // Too soon: the LUID that asked stays "not yet tried" and asks again at the next sample after the interval.
            return false;
        }

        foreach (var luid in current)
        {
            if (!known.Contains(luid))
            {
                _triedUnknown.Add(luid);
            }
        }

        Read();
        return true;
    }

    private void Read()
    {
        var list = enumerate();
        _enumeratedAtMs = nowMs();
        Enumerations++;
        _vanishedPending = false;

        // A read that finds nothing (DXGI failing for a moment) does not wipe a list that had adapters in it.
        if (_adapters is null || list.Count > 0)
        {
            _adapters = list;
        }
    }
}
