namespace PcMieru.Party.Hardware;

/// <summary>
/// One GPU slot the board has handed out, as the save will remember it (DESIGN-C 2.3, step 3). A plain record so System.Text.Json reads and writes it
/// as it stands. The slot number is the old save's <c>gpu#i</c>.
/// </summary>
/// <param name="Slot">0..5.</param>
/// <param name="Name">The adapter name as Core reported it.</param>
/// <param name="VramBytes">Dedicated video memory; 0 when it was not known.</param>
/// <param name="PciBus">The PCI bus number last seen; null when never known.</param>
public sealed record GpuSlotRecord(int Slot, string Name, long VramBytes, uint? PciBus);

/// <summary>What the map did with one adapter. <see cref="Slot"/> is null when it has none; <see cref="Ambiguous"/> says why (it is shown, but earns nothing).</summary>
/// <param name="Id">Core's adapter id (LUID), as given to <see cref="GpuSlotMap.Assign"/>.</param>
/// <param name="Slot">0..5, or null: no slot (a seventh card, an ambiguous match, or an id listed twice).</param>
/// <param name="Ambiguous">
/// True when two or more current adapters match one remembered slot equally well, or one adapter matches several remembered slots equally well,
/// so nothing says which is which. Its progress stops until the match becomes clear; the map never settles it by itself.
/// </param>
public sealed record GpuSlotAssignment(string Id, int? Slot, bool Ambiguous)
{
    /// <summary>The key the experience and the equipment are stored under (the old save's <c>gpu#i</c>); null without a slot.</summary>
    public string? Key => Slot is { } s ? "gpu#" + s : null;
}

/// <param name="Adapters">One entry per adapter given, in the same order.</param>
/// <param name="Records">The remembered slots after this call, by slot number. The save stores this.</param>
public sealed record GpuSlotResult(IReadOnlyList<GpuSlotAssignment> Adapters, IReadOnlyList<GpuSlotRecord> Records);

/// <summary>
/// Which GPU is in which of the board's six slots, and keeps it that way (DESIGN-C 2.3: "the first time reproduce the old order; while the topology
/// stays the same the slot numbers stay fixed; plugging a card in or out does not compact the others; an ambiguous slot stops progressing").
/// Pure and deterministic: no clock, no I/O. The caller keeps one instance for the run, feeds it the adapters of every frame
/// (<see cref="BoardFrame.AllGpus"/>) and writes <see cref="Records"/> into the save when they change.
///
/// ■ Rules
/// 1. First time (nothing remembered): the adapters in the order given (the old roster order, <see cref="GpuRoster.Order"/>) take slots 0, 1, 2, ...
///    A seventh card and beyond have no slot. That is exactly the old app's <c>gpu#i</c>.
/// 2. Afterwards an adapter is matched, strongest first, to a remembered slot by
///    (a) the id it had earlier in this run (a LUID is stable inside one run, so a card that was settled stays settled even when its bus later
///        fails to read; LUIDs change at every boot, so they are never stored);
///    (b) name, dedicated memory and PCI bus all equal;
///    (c) name and dedicated memory equal while the bus differs or is unknown (the bus moved, or could not be read), only when that is the one
///        remembered slot it can be and it is the one adapter that can be that slot. A memory size of 0 (unknown) on either side matches any size.
///    A match is mutual and unique, or it is not a match: when two adapters could be the same slot (twin cards whose bus is unknown or changed),
///    or one adapter could be two slots, all of them are <see cref="GpuSlotAssignment.Ambiguous"/>, take no slot, and the slots they could be stay
///    blocked this call (no new card takes them).
/// 3. A card that is gone leaves its slot reserved (a hole): the others keep theirs, and a replugged card gets its old slot back by (b).
/// 4. An adapter that matches nothing is a new card. It takes the lowest slot that was never used by any card (a hole is "used"); with none left it
///    has no slot.
///
/// Names are compared as the driver gave them: a renamed card (a driver that changes the string) is a new card, not a guess.
/// A card of the same model put in the place of a gone one is the same card to this map; nothing in (name, memory, bus) can tell them apart.
/// </summary>
public sealed class GpuSlotMap
{
    /// <summary>The board has this many slots (the old Parts Life limit).</summary>
    public const int MaxSlots = BoardFrame.MaxGpus;

    private readonly GpuSlotRecord?[] _records = new GpuSlotRecord?[MaxSlots];
    private readonly Dictionary<string, int> _sessionSlots = new(StringComparer.Ordinal);

    /// <param name="persisted">What the save remembered. Null or empty on the first run and with an old save. A record outside 0..5, and a second record for one slot, is ignored.</param>
    public GpuSlotMap(IEnumerable<GpuSlotRecord>? persisted = null)
    {
        foreach (var r in persisted ?? [])
        {
            if (r is not null && r.Slot is >= 0 and < MaxSlots && _records[r.Slot] is null)
            {
                _records[r.Slot] = r with { Name = r.Name ?? string.Empty, VramBytes = Math.Max(0, r.VramBytes) };
            }
        }
    }

    /// <summary>The remembered slots now, by slot number.</summary>
    public IReadOnlyList<GpuSlotRecord> Records => _records.Where(static r => r is not null).Select(static r => r!).ToArray();

    /// <summary>
    /// Settles the slots for the adapters of one frame, in the order the roster gives them (<see cref="BoardFrame.AllGpus"/>). An empty list
    /// changes nothing (a frame without GPUs forgets no card).
    /// </summary>
    public GpuSlotResult Assign(IReadOnlyList<BoardGpu> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var pass = new Pass(adapters);
        if (_records.All(static r => r is null))
        {
            NumberFirstTime(pass);
        }
        else
        {
            MatchSession(pass);
            Match(pass, Exact);
            Match(pass, SameModel);
            GiveNewCards(pass);
        }

        Commit(pass);
        var result = new GpuSlotAssignment[adapters.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new GpuSlotAssignment(adapters[i].Id, pass.Slot[i] >= 0 ? pass.Slot[i] : null, pass.Ambiguous[i]);
        }

        return new GpuSlotResult(result, Records);
    }

    private sealed class Pass
    {
        public Pass(IReadOnlyList<BoardGpu> adapters)
        {
            Adapters = adapters;
            Slot = new int[adapters.Count];
            Ambiguous = new bool[adapters.Count];
            Duplicate = new bool[adapters.Count];
            Array.Fill(Slot, -1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < adapters.Count; i++)
            {
                Duplicate[i] = !seen.Add(adapters[i].Id);
            }
        }

        public IReadOnlyList<BoardGpu> Adapters { get; }

        /// <summary>The slot given, or -1.</summary>
        public int[] Slot { get; }

        public bool[] Ambiguous { get; }

        /// <summary>An id listed twice: the later entries get nothing (Core never lists one, but the map must not give one slot to two entries).</summary>
        public bool[] Duplicate { get; }

        public bool[] Claimed { get; } = new bool[MaxSlots];

        /// <summary>Slots that several adapters could be this call: no one takes them.</summary>
        public bool[] Blocked { get; } = new bool[MaxSlots];

        /// <summary>Adapters still looking for a slot.</summary>
        public IEnumerable<int> Open => Enumerable.Range(0, Adapters.Count).Where(i => Slot[i] < 0 && !Ambiguous[i] && !Duplicate[i]);

        public void Give(int adapter, int slot)
        {
            Slot[adapter] = slot;
            Claimed[slot] = true;
        }
    }

    private static long VramOf(BoardGpu g) => g.VramTotalBytes is { } v && v > 0 ? v : 0;

    private static bool Exact(BoardGpu g, GpuSlotRecord r) =>
        string.Equals(g.Name, r.Name, StringComparison.Ordinal) && VramOf(g) == r.VramBytes && g.PciBus == r.PciBus;

    private static bool SameModel(BoardGpu g, GpuSlotRecord r) =>
        string.Equals(g.Name, r.Name, StringComparison.Ordinal) && (VramOf(g) == r.VramBytes || VramOf(g) == 0 || r.VramBytes == 0);

    /// <summary>Rule 1. The first time nothing is remembered: slots follow the order given; a seventh card has none.</summary>
    private static void NumberFirstTime(Pass pass)
    {
        var next = 0;
        foreach (var i in pass.Open.ToArray())
        {
            if (next < MaxSlots)
            {
                pass.Give(i, next++);
            }
        }
    }

    /// <summary>Rule 2a. An adapter settled earlier in this run keeps its slot while the record there is still the same model.</summary>
    private void MatchSession(Pass pass)
    {
        foreach (var i in pass.Open.ToArray())
        {
            if (_sessionSlots.TryGetValue(pass.Adapters[i].Id, out var s)
                && _records[s] is { } r && !pass.Claimed[s] && SameModel(pass.Adapters[i], r))
            {
                pass.Give(i, s);
            }
        }
    }

    /// <summary>
    /// Rules 2b and 2c, one tier. Decided on one snapshot, so the order of the adapters cannot change the outcome: an adapter with exactly one
    /// candidate slot that no other open adapter is a candidate for takes it; one with any other non-empty set of candidates is ambiguous.
    /// </summary>
    private void Match(Pass pass, Func<BoardGpu, GpuSlotRecord, bool> same)
    {
        var open = pass.Open.ToArray();
        var candidates = new Dictionary<int, List<int>>();
        var claimants = new int[MaxSlots];
        foreach (var i in open)
        {
            var list = new List<int>();
            for (var s = 0; s < MaxSlots; s++)
            {
                if (_records[s] is { } r && !pass.Claimed[s] && !pass.Blocked[s] && same(pass.Adapters[i], r))
                {
                    list.Add(s);
                    claimants[s]++;
                }
            }

            candidates[i] = list;
        }

        var toBlock = new List<int>();
        foreach (var i in open)
        {
            var list = candidates[i];
            if (list.Count == 0)
            {
                continue;
            }

            if (list.Count == 1 && claimants[list[0]] == 1)
            {
                pass.Give(i, list[0]);
            }
            else
            {
                pass.Ambiguous[i] = true;
                toBlock.AddRange(list);
            }
        }

        foreach (var s in toBlock)
        {
            pass.Blocked[s] = true;
        }
    }

    /// <summary>Rule 4. What is left matched nothing: a new card, onto the lowest slot no card ever had.</summary>
    private void GiveNewCards(Pass pass)
    {
        foreach (var i in pass.Open.ToArray())
        {
            for (var s = 0; s < MaxSlots; s++)
            {
                if (_records[s] is null && !pass.Claimed[s])
                {
                    pass.Give(i, s);
                    break;
                }
            }
        }
    }

    /// <summary>Writes the pass into the remembered slots and the run's ids. An adapter with no slot, and a slot with no adapter, change nothing.</summary>
    private void Commit(Pass pass)
    {
        for (var i = 0; i < pass.Adapters.Count; i++)
        {
            var g = pass.Adapters[i];
            var s = pass.Slot[i];
            if (s < 0)
            {
                // Settled before in this run, ambiguous now: forget the id, so a stale answer cannot bring the slot back.
                if (pass.Ambiguous[i])
                {
                    _sessionSlots.Remove(g.Id);
                }

                continue;
            }

            // What is known now wins, but a number that could not be read this time does not erase the one remembered.
            var old = _records[s];
            _records[s] = new GpuSlotRecord(s, g.Name, VramOf(g) > 0 ? VramOf(g) : old?.VramBytes ?? 0, g.PciBus ?? old?.PciBus);
            foreach (var other in _sessionSlots.Where(kv => kv.Value == s && kv.Key != g.Id).Select(static kv => kv.Key).ToArray())
            {
                _sessionSlots.Remove(other);
            }

            _sessionSlots[g.Id] = s;
        }
    }
}
