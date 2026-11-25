using System.Text.Json.Serialization;
using PcMieru.Party.Game;

namespace PcMieru.Party.Hardware;

/// <summary>
/// One GPU slot the board has handed out, as the save will remember it (DESIGN-C 2.3, step 3). A plain record so System.Text.Json reads and writes it
/// as it stands. The slot number is the old save's <c>gpu#i</c>.
/// </summary>
/// <param name="Slot">0..5.</param>
/// <param name="Name">The adapter name as Core reported it.</param>
/// <param name="VramBytes">Dedicated video memory; 0 when it was not known.</param>
/// <param name="PciBus">The PCI bus number last seen; null when never known.</param>
/// <param name="Retired">
/// True when the number is closed: the owner let the card go (<see cref="GpuSlotMap.Forget"/>), or the save was damaged and the number holds progress that no
/// readable record claims. A retired record matches no adapter and its number is never handed to any card, in this run or after a restart; the progress
/// stored under <c>gpu#i</c> stays where it is until <see cref="GpuSlotKeys.ResetSlot"/> removes it together with the record. A save written before this
/// field existed has none and reads as false. It is left out of the save while false, so an ordinary record keeps its old shape.
/// </param>
/// <param name="Disputed">
/// True when the save held two or more live records of one exact identity (same name, memory and bus) and so cannot say which card owns which number
/// (<see cref="GpuSlotKeys.RepairLoaded"/> keeps such records instead of dropping them). It is NOT <paramref name="Retired"/>: a retired record matches
/// no adapter, so a matching card is a new one; a disputed record BLOCKS matching, so a card that fits it is ambiguous (no slot, no earning) and is never a
/// new card. Its number is never handed out. Only the owner's <see cref="GpuSlotKeys.ForgetSlot"/> (which turns it into a retired record) and then
/// <see cref="GpuSlotKeys.ResetSlot"/> free it. Left out of the save while false, like <paramref name="Retired"/>.
/// </param>
public sealed record GpuSlotRecord(
    int Slot, string Name, long VramBytes, uint? PciBus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Retired = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Disputed = false);

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
///    (b) name and PCI bus equal and known on both sides (a bus that is not null), and dedicated memory equal and above 0 or unknown (0) on both
///        sides (then the memory is simply not part of the identity): an unknown bus is never "equal" to another unknown, so a card whose bus
///        could not be read never matches a record whose bus was not known either;
///    (c) name and dedicated memory equal while the bus differs or is unknown on either side (the bus moved, or could not be read), only when that is
///        the one remembered slot it can be and it is the one adapter that can be that slot. A memory size of 0 (unknown) on either side matches any size.
///    Before (b), an adapter that fits a DISPUTED record (<see cref="GpuSlotRecord.Disputed"/>) is ambiguous at once: same model (the memory rule of (c)),
///    and a bus that is unknown on either side or equal. Its candidate slots are that disputed slot, and the live slots it would be a candidate for in
///    (b) (in (c) when (b) has none); they are blocked this call, so no other adapter takes them. Without this, a card whose memory is unknown would
///    exactly match its live record and never see that the disputed one fits it just as well. An adapter whose known bus differs from the disputed
///    record's known bus does not fit it and goes through the tiers as usual.
///    A match is mutual and unique, or it is not a match: when two adapters could be the same slot (twin cards whose bus is unknown or changed),
///    or one adapter could be two slots (two remembered twins and one survivor whose bus cannot tell which), all of them are
///    <see cref="GpuSlotAssignment.Ambiguous"/>, take no slot, and the slots they could be stay blocked this call (no new card takes them). An
///    adapter that could be one of those blocked slots is ambiguous as well: having a candidate, blocked or not, it is never a new card.
/// 3. A card that is gone leaves its slot reserved (a hole): the others keep theirs, and a replugged card gets its old slot back by (b).
/// 4. An adapter that has no candidate slot at all is a new card. It takes the lowest slot that was never used by any card (a hole is "used", and so is
///    a retired slot, see <see cref="GpuSlotRecord.Retired"/>); with none left it has no slot. A card's progress is never handed to another card by
///    itself: a number the owner let go of (<see cref="Forget"/>) stays closed until <see cref="GpuSlotKeys.ResetSlot"/> has removed the progress under it.
///    A disputed record (<see cref="GpuSlotRecord.Disputed"/>, two records of one identity that a damaged save left) is a candidate like any live one,
///    but never a match: a card that fits it is <see cref="GpuSlotAssignment.Ambiguous"/>, and its number stays closed.
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

    /// <param name="persisted">
    /// What the save remembered. Null or empty on the first run and with an old save. A record outside 0..5, and a second record for one slot, is ignored.
    /// A save whose records are damaged is repaired before it gets here (<see cref="GpuSlotKeys.RepairLoaded"/>); this constructor trusts what it is given
    /// (a record that is both retired and disputed counts as retired).
    /// </param>
    public GpuSlotMap(IEnumerable<GpuSlotRecord>? persisted = null)
    {
        foreach (var r in persisted ?? [])
        {
            if (r is not null && r.Slot is >= 0 and < MaxSlots && _records[r.Slot] is null)
            {
                _records[r.Slot] = r with { Name = r.Name ?? string.Empty, VramBytes = Math.Max(0, r.VramBytes), Disputed = r.Disputed && !r.Retired };
            }
        }
    }

    /// <summary>The remembered slots now, by slot number.</summary>
    public IReadOnlyList<GpuSlotRecord> Records => _records.Where(static r => r is not null).Select(static r => r!).ToArray();

    /// <summary>
    /// Lets one remembered slot go (a card taken out for good) by RETIRING its record (<see cref="GpuSlotRecord.Retired"/>); it does not delete it. Nothing
    /// calls this by itself: an unplugged card keeps its slot as a hole (rule 3), so a hole never goes away on its own. The experience and equipment stored
    /// under the key are not touched here, and because they stay, the number is closed: a retired record matches no adapter, and no card is given its number
    /// in this run or after a restart (the record is in <see cref="Records"/>, so it is in the save). A card that is still plugged in is a new card from the
    /// next <see cref="Assign"/> on (it was the one that lost the slot) and gets a different free number. The number is free again only after
    /// <see cref="GpuSlotKeys.ResetSlot"/> has removed the progress under it together with the record.
    /// Returns the remembered slots after this call (the same list <see cref="Records"/> gives, for the save; <see cref="GpuSlotKeys.ForgetSlot"/> also
    /// writes it into the state); a slot outside 0..5, one with no record, and one that is already retired change nothing.
    /// A DISPUTED record (<see cref="GpuSlotRecord.Disputed"/>) is let go the same way: the owner's decision turns it into a retired one.
    /// </summary>
    public IReadOnlyList<GpuSlotRecord> Forget(int slot)
    {
        if (slot is >= 0 and < MaxSlots && _records[slot] is { Retired: false } record)
        {
            _records[slot] = record with { Retired = true, Disputed = false };
            foreach (var id in _sessionSlots.Where(kv => kv.Value == slot).Select(static kv => kv.Key).ToArray())
            {
                _sessionSlots.Remove(id);
            }
        }

        return Records;
    }

    /// <summary>
    /// Removes the record of a RETIRED slot, so that the number is free again. Only <see cref="GpuSlotKeys.ResetSlot"/> calls this, after it has removed the
    /// progress under the key in the same step; a slot that is not retired (or has no record) changes nothing and gives false.
    /// </summary>
    internal bool Release(int slot)
    {
        if (slot is >= 0 and < MaxSlots && _records[slot] is { Retired: true })
        {
            _records[slot] = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Closes every given number that has no record yet with a retired placeholder (<see cref="GpuSlotKeys.Placeholder"/>), so no card is given it. Used when
    /// the list is first set up and numbers hold progress that no record claims (<see cref="GpuSlotKeys.UnrecordedProgress"/>); a number that has a record,
    /// or is outside 0..5, changes nothing.
    /// </summary>
    internal void Reserve(IEnumerable<int> slots)
    {
        foreach (var s in slots)
        {
            if (s is >= 0 and < MaxSlots && _records[s] is null)
            {
                _records[s] = GpuSlotKeys.Placeholder(s);
            }
        }
    }

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
            BlockDisputedFits(pass);
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

    /// <summary>
    /// A name and a bus that are known on both sides and equal, and a memory size that does not contradict them: equal and above 0, or unknown (0) on
    /// both sides, in which case the memory is simply not part of this card's identity (some adapters never report any, and twins of such a model
    /// still differ by their bus). An unknown name or bus is never "equal to an unknown", and a memory size known on one side only is not an exact
    /// match: such a pair is left to <see cref="SameModel"/>, which only ever settles a unique pair.
    /// </summary>
    private static bool Exact(BoardGpu g, GpuSlotRecord r) =>
        !string.IsNullOrEmpty(g.Name) && string.Equals(g.Name, r.Name, StringComparison.Ordinal)
        && g.PciBus is { } bus && r.PciBus is { } remembered && bus == remembered
        && (VramOf(g) > 0 ? VramOf(g) == r.VramBytes : r.VramBytes == 0);

    private static bool SameModel(BoardGpu g, GpuSlotRecord r) =>
        string.Equals(g.Name, r.Name, StringComparison.Ordinal) && (VramOf(g) == r.VramBytes || VramOf(g) == 0 || r.VramBytes == 0);

    /// <summary>Rule 1. The first time nothing is remembered: slots follow the order given; a seventh card has none.</summary>
    private void NumberFirstTime(Pass pass)
    {
        foreach (var i in pass.Open.ToArray())
        {
            var s = LowestFree(pass);
            if (s >= 0)
            {
                pass.Give(i, s);
            }
        }
    }

    /// <summary>The lowest slot with no record (a retired record is a record) that no one took this call; -1 when none.</summary>
    private int LowestFree(Pass pass)
    {
        for (var s = 0; s < MaxSlots; s++)
        {
            if (_records[s] is null && !pass.Claimed[s])
            {
                return s;
            }
        }

        return -1;
    }

    /// <summary>Rule 2a. An adapter settled earlier in this run keeps its slot while the record there is still the same model (and not retired).</summary>
    private void MatchSession(Pass pass)
    {
        foreach (var i in pass.Open.ToArray())
        {
            if (_sessionSlots.TryGetValue(pass.Adapters[i].Id, out var s)
                && _records[s] is { Retired: false, Disputed: false } r && !pass.Claimed[s] && SameModel(pass.Adapters[i], r))
            {
                pass.Give(i, s);
            }
        }
    }

    /// <summary>
    /// Rule 2, the disputed records. An adapter that fits a disputed record (<see cref="FitsDisputed"/>) is ambiguous now, before any tier can hand it a live
    /// record that it fits only because its memory is unknown. What it could be is blocked for this call: the disputed slots it fits, and the live slots
    /// it would be a candidate for in the normal tiers (the exact ones, or when there is none, the same-model ones). A live record of another bus that the
    /// adapter could only be by its unknown memory is not blocked while an exact one exists: the card that owns it still gets it. Decided on one snapshot
    /// (the adapters still open after rule 2a), so the order of the adapters cannot change the outcome.
    /// </summary>
    private void BlockDisputedFits(Pass pass)
    {
        var disputed = Enumerable.Range(0, MaxSlots).Where(s => _records[s] is { Retired: false, Disputed: true }).ToArray();
        if (disputed.Length == 0)
        {
            return;
        }

        var toBlock = new List<int>();
        foreach (var i in pass.Open.ToArray())
        {
            var g = pass.Adapters[i];
            var fits = disputed.Where(s => FitsDisputed(g, _records[s]!)).ToArray();
            if (fits.Length == 0)
            {
                continue;
            }

            pass.Ambiguous[i] = true;
            toBlock.AddRange(fits);
            var live = LiveCandidates(pass, g, Exact);
            toBlock.AddRange(live.Count > 0 ? live : LiveCandidates(pass, g, SameModel));
        }

        foreach (var s in toBlock)
        {
            pass.Blocked[s] = true;
        }
    }

    /// <summary>The same model as the disputed record (memory as in <see cref="SameModel"/>), and a bus that is unknown on either side or equal.</summary>
    private static bool FitsDisputed(BoardGpu g, GpuSlotRecord r) =>
        SameModel(g, r) && (g.PciBus is null || r.PciBus is null || g.PciBus == r.PciBus);

    /// <summary>The live (not retired, not disputed) slots nobody took yet that <paramref name="same"/> says the adapter could be.</summary>
    private List<int> LiveCandidates(Pass pass, BoardGpu g, Func<BoardGpu, GpuSlotRecord, bool> same)
    {
        var list = new List<int>();
        for (var s = 0; s < MaxSlots; s++)
        {
            if (_records[s] is { Retired: false, Disputed: false } r && !pass.Claimed[s] && same(g, r))
            {
                list.Add(s);
            }
        }

        return list;
    }

    /// <summary>
    /// Rules 2b and 2c, one tier. Decided on one snapshot, so the order of the adapters cannot change the outcome: an adapter with exactly one
    /// candidate slot that no other open adapter is a candidate for, and that an earlier tier did not block, takes it; one with any other
    /// non-empty set of candidates is ambiguous. A blocked slot still counts as a candidate: an adapter that could be one of the slots an
    /// ambiguous match holds is ambiguous too, never a new card (only an adapter with no candidate at all is one). A DISPUTED record is a candidate
    /// that can never be taken: the adapter that fits it is ambiguous, however clear the fit looks.
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
                if (_records[s] is { Retired: false } r && !pass.Claimed[s] && same(pass.Adapters[i], r))
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

            if (list.Count == 1 && claimants[list[0]] == 1 && !pass.Blocked[list[0]] && _records[list[0]] is { Disputed: false })
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

    /// <summary>Rule 4. What is left matched nothing: a new card, onto the lowest slot no card ever had (a retired slot counts as had).</summary>
    private void GiveNewCards(Pass pass)
    {
        foreach (var i in pass.Open.ToArray())
        {
            var s = LowestFree(pass);
            if (s >= 0)
            {
                pass.Give(i, s);
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
