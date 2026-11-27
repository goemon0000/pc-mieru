using System.Text.Json;
using PcMieru.Party.Adventure;
using PcMieru.Party.Hardware;

namespace PcMieru.Party.Game;

/// <summary>What <see cref="GpuSlotKeys.ApplyTo"/> settled for one frame.</summary>
/// <param name="Adapters">
/// One assignment per GPU of the frame (<see cref="BoardFrame.AllGpus"/>), in the same order; empty when the frame has no GPU list. This is the list to DISPLAY
/// from: each entry has the adapter's key (<see cref="GpuSlotAssignment.Key"/>, null without a slot) and says whether it is ambiguous. Having a key here does
/// NOT mean the adapter may earn: see <paramref name="EarningKeys"/>.
/// </param>
/// <param name="EarningKeys">
/// The ONLY keys experience may be added under this frame: the <c>gpu#i</c> of an adapter that holds a slot, is not ambiguous, and has a usable utilization
/// (a number, finite). A settled adapter whose load is missing or unreadable is in <paramref name="Adapters"/> but not here, so a missing reading earns nothing.
/// In adapter order.
/// </param>
/// <param name="SlotsChanged">True when <see cref="PartyState.GpuSlots"/> was rewritten by this call: the save has changed and should be written.</param>
public sealed record GpuSlotFrame(IReadOnlyList<GpuSlotAssignment> Adapters, IReadOnlyList<string> EarningKeys, bool SlotsChanged);

/// <summary>
/// The join between the board frame, the slot map and the save (DESIGN-C 2.3 and 4.2). The map is built once per run from the save
/// (<c>new GpuSlotMap(state.GpuSlots)</c>); every frame then goes through <see cref="ApplyTo"/>. The owner's two decisions about a card that is gone for good
/// are <see cref="ForgetSlot"/> (close the number, keep the progress) and then <see cref="ResetSlot"/> (remove the progress and free the number).
/// </summary>
public static class GpuSlotKeys
{
    /// <summary>The key experience and equipment are stored under for a slot (the old save's <c>gpu#i</c>; the same text as <see cref="GpuSlotAssignment.Key"/>).</summary>
    public static string KeyOf(int slot) => "gpu#" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The prefix of a DISPLAY-ONLY key: what the board gives a GPU that holds no slot (ambiguous or unknown), so it can still be drawn. It is never stored: no
    /// experience, no gear and no entry in any state dictionary may be made under it (<see cref="Director"/> and the adventure engine skip such a key). It has no
    /// <c>#</c>, so it can never be read as a numbered key such as <c>gpu#3</c>.
    /// </summary>
    public const string DisplayOnlyPrefix = "gpu~";

    /// <summary>The display-only key of the adapter at this index of the frame's GPU list.</summary>
    public static string DisplayKeyOf(int adapterIndex) => DisplayOnlyPrefix + adapterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True for a key made by <see cref="DisplayKeyOf"/>: the slot is drawn but owns nothing.</summary>
    public static bool IsDisplayOnly(string? key) => key is not null && key.StartsWith(DisplayOnlyPrefix, StringComparison.Ordinal);

    /// <summary>True when the map holds a retired record for the slot (the only kind <see cref="ResetSlot"/> accepts).</summary>
    public static bool IsRetired(this GpuSlotMap map, int slot)
    {
        ArgumentNullException.ThrowIfNull(map);
        return slot is >= 0 and < GpuSlotMap.MaxSlots && map.Records.Any(r => r.Slot == slot && r.Retired);
    }

    /// <summary>
    /// The owner's reset of a retired number with the gear kept: the gear under the slot's key goes back to the bag (<see cref="AdventureEngine.ReturnGearOf"/>;
    /// what does not fit is sold), then <see cref="ResetSlot"/> removes the experience and frees the number. Nothing is returned for a slot that cannot be reset
    /// (not retired), so the gear is never taken from a card that is still in use. <paramref name="adventure"/> null (no adventure book) just resets.
    /// </summary>
    public static bool ResetSlotKeepingGear(this GpuSlotMap map, PartyState state, AdventureEngine? adventure, int slot)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(state);

        if (!map.IsRetired(slot))
        {
            return false;
        }

        adventure?.ReturnGearOf(KeyOf(slot));
        return map.ResetSlot(state, slot);
    }

    /// <summary>
    /// Assigns the slots of the frame's GPUs, writes the remembered slots into <paramref name="state"/> when they changed (the only part of the state this
    /// touches), and gives back the keys the rules layer may add experience under (<see cref="GpuSlotFrame.EarningKeys"/>). A frame whose adapter list Core
    /// could not read (<see cref="BoardFrame.AllGpus"/> null) assigns nothing, forgets nothing and gives no key: an unknown list is not "no GPU" and not a
    /// reason to move a slot. Progress is never reassigned here: an ambiguous adapter has no key until the match becomes clear.
    /// When this call numbers the cards for the first time (the map remembered nothing), every <c>gpu#i</c> that holds progress and got no card is closed
    /// with a retired placeholder in the same step (<see cref="UnrecordedProgress"/>), so a card plugged in later cannot inherit it. A frame with no adapter at
    /// all numbers nothing and so reserves nothing.
    /// </summary>
    public static GpuSlotFrame ApplyTo(this GpuSlotMap map, PartyState state, BoardFrame frame)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.AllGpus is not { } all)
        {
            return new GpuSlotFrame([], [], false);
        }

        var firstRun = map.Records.Count == 0;
        var result = map.Assign(all);
        if (firstRun && map.Records.Count > 0)
        {
            map.Reserve(UnrecordedProgress(state, map.Records));
        }

        var earning = new List<string>();
        for (var i = 0; i < result.Adapters.Count; i++)
        {
            // Settled, not ambiguous, and a reading that is a number: anything else is shown but earns nothing.
            if (result.Adapters[i] is { Ambiguous: false, Key: { } key } && all[i].Percent is { } load && double.IsFinite(load))
            {
                earning.Add(key);
            }
        }

        state.GpuSlots ??= new List<GpuSlotRecord>();
        var records = map.Records;
        var changed = !state.GpuSlots.SequenceEqual(records);
        if (changed)
        {
            state.GpuSlots = [.. records];
        }

        return new GpuSlotFrame(result.Adapters, earning, changed);
    }

    /// <summary>
    /// The owner lets a card's number go: <see cref="GpuSlotMap.Forget"/>, and the retired record is written into <paramref name="state"/> at once (so a frame
    /// without a GPU list cannot leave it out of the save). The progress under the key is NOT touched and the number is closed, in this run and after a
    /// restart, until <see cref="ResetSlot"/>. False when nothing changed (no such slot, no record, already retired).
    /// </summary>
    public static bool ForgetSlot(this GpuSlotMap map, PartyState state, int slot)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(state);

        var before = map.Records;
        var after = map.Forget(slot);
        state.GpuSlots = [.. after];
        return !before.SequenceEqual(after);
    }

    /// <summary>
    /// The one way to take a card's progress away and free its number, in one step: removes the experience and the equipment stored under the slot's key
    /// AND the retired record, so the number may go to a new card, which starts from nothing. The equipment is removed, not returned to the bag (the bag is the
    /// adventure engine's; a UI that wants the gear back must take it out before it calls this).
    /// Only a RETIRED slot can be reset (<see cref="ForgetSlot"/> first, which also turns a disputed record into a retired one); anything else returns
    /// false and changes nothing. It takes the map as well as the
    /// state because the map holds the records in memory and would write the retired one back at the next <see cref="ApplyTo"/>.
    /// </summary>
    public static bool ResetSlot(this GpuSlotMap map, PartyState state, int slot)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(state);

        if (slot is < 0 or >= GpuSlotMap.MaxSlots || !map.Records.Any(r => r.Slot == slot && r.Retired))
        {
            return false;
        }

        var key = KeyOf(slot);
        state.Experience?.Remove(key);
        state.Adventure?.Equipped?.Remove(key);
        map.Release(slot);
        state.GpuSlots = [.. map.Records];
        return true;
    }

    // ---------------------------------------------------------------------
    // A save whose GpuSlots field is damaged
    // ---------------------------------------------------------------------

    /// <summary>
    /// How the GpuSlots field stood in the saved text: absent, or present (as an array of this length, or as something that is not an array, null included).
    /// <paramref name="Lost"/> holds the elements of the array that are objects with no integer <c>Slot</c> property, by their index in the array: the
    /// deserializer reads such a record as slot 0 (or drops it), which is a guess, not what the text said. Each value is what the TEXT said about that
    /// element (name, memory, bus, retired; the slot is -1), so the identity of a row that was discarded can still be compared after the reader dropped it.
    /// </summary>
    internal readonly record struct RawGpuSlots(bool Present, int? ArrayLength, IReadOnlyDictionary<int, GpuSlotRecord>? Lost = null)
    {
        /// <summary>True when an element of the array has no integer <c>Slot</c>.</summary>
        public bool SlotLost => Lost is { Count: > 0 };
    }

    /// <summary>
    /// Reads from the saved text only whether the GpuSlots field is there and what shape it has (the deserializer cannot say: it turns an absent field, a null
    /// and a wrong shape into the same empty list). Called after the text has been read once, so it is valid JSON; if it cannot be read here after all, the
    /// field counts as present and broken, the safe side.
    /// </summary>
    internal static RawGpuSlots Probe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new RawGpuSlots(true, null);
            }

            RawGpuSlots found = new(false, null);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // Ordinal and the last one wins, like the serializer (a differently spelled name is an unknown field, not this one).
                if (string.Equals(p.Name, nameof(PartyState.GpuSlots), StringComparison.Ordinal))
                {
                    var isArray = p.Value.ValueKind == JsonValueKind.Array;
                    found = new RawGpuSlots(true, isArray ? p.Value.GetArrayLength() : null, isArray ? LostRows(p.Value) : null);
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return new RawGpuSlots(true, null);
        }
    }

    /// <summary>
    /// The elements of the array (by index) that are objects with no integer <c>Slot</c>, each with what its text said about the card. An element that is not
    /// an object produced no record at all and is not listed.
    /// </summary>
    private static Dictionary<int, GpuSlotRecord> LostRows(JsonElement array)
    {
        var lost = new Dictionary<int, GpuSlotRecord>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (LacksSlot(element))
            {
                lost[index] = RawIdentity(element);
            }

            index++;
        }

        return lost;
    }

    /// <summary>A name the text says; empty when it is not a string or cannot be decoded (an unpaired surrogate escape): an empty name is an unknown identity, which disputes nothing.</summary>
    private static string ReadName(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        try
        {
            return value.GetString() ?? string.Empty;
        }
        catch (Exception e) when (LenientJson.IsReadError(e))
        {
            return string.Empty;
        }
    }

    /// <summary>The name, memory, bus and retired flag an object says (ordinal names, the last one wins); a value of the wrong kind counts as absent. Slot -1: it has none.</summary>
    private static GpuSlotRecord RawIdentity(JsonElement element)
    {
        var name = string.Empty;
        long vram = 0;
        uint? bus = null;
        var retired = false;
        foreach (var p in element.EnumerateObject())
        {
            switch (p.Name)
            {
                case nameof(GpuSlotRecord.Name):
                    name = ReadName(p.Value);
                    break;
                case nameof(GpuSlotRecord.VramBytes):
                    vram = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var v) ? Math.Max(0, v) : 0;
                    break;
                case nameof(GpuSlotRecord.PciBus):
                    bus = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetUInt32(out var b) ? b : null;
                    break;
                case nameof(GpuSlotRecord.Retired):
                    retired = p.Value.ValueKind == JsonValueKind.True;
                    break;
            }
        }

        return new GpuSlotRecord(-1, name, vram, bus, retired);
    }

    /// <summary>
    /// An array element that is an object but has no integer <c>Slot</c> (absent, spelled differently, or not a whole number). Named like the serializer names
    /// it (ordinal, the last one wins). An element that is not an object produced no record at all and is not counted here.
    /// </summary>
    private static bool LacksSlot(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, nameof(GpuSlotRecord.Slot), StringComparison.Ordinal))
            {
                found = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out _);
            }
        }

        return !found;
    }

    /// <summary>
    /// Called by <see cref="PartyState.FromJson(string, out bool)"/> once the state is read and migrated. Tells the two cases apart:
    ///
    /// (a) the field is ABSENT: an old save. Nothing changes; the first run numbers the cards in the old Parts Life order, as before.
    ///
    /// (b) the field is PRESENT but broken: null, not an array, an element that could not be read (or is null), an element with no integer Slot (it would be
    ///     read as slot 0), a slot number outside 0..5 (negative included), one slot number twice, or two live (not retired) records with the same exact
    ///     identity. Then the records that are valid and unambiguous are kept; two records of one identity are kept too, as DISPUTED (they block matching
    ///     and keep their numbers closed, see <see cref="GpuSlotRecord.Disputed"/>; dropping them would make both cards look new on the next run). A kept
    ///     record is disputed as well when a row that was discarded (no integer Slot, or a slot number claimed twice) said the same identity: the discarded
    ///     row may be this card's twin, and which of the two owns the number is not known. A row that is null or out of range has no usable identity and is
    ///     not compared. And for every
    ///     <c>gpu#i</c> that holds experience or equipment and has no kept record, a RETIRED placeholder is added (<see cref="UnrecordedProgress"/>), so that
    ///     number is reserved and no card inherits what is stored under it. Progress is never dropped. <see cref="PartyState.GpuSlotsRepaired"/> is set.
    ///
    /// A present, well-formed list (an empty one too) is not damaged and is not reported, but it is the list the map will number from, so the same
    /// reservation applies: a <c>gpu#i</c> that holds progress and has no record gets a retired placeholder, and nothing else changes. Without it an empty
    /// list, or one that records fewer cards than the progress covers, would leave those numbers free for the next card.
    ///
    /// Returns true when (b) happened.
    /// </summary>
    internal static bool RepairLoaded(PartyState state, RawGpuSlots raw)
    {
        if (!raw.Present)
        {
            return false;
        }

        state.GpuSlots ??= new List<GpuSlotRecord>();
        // A field that is not an array, or an array whose elements were dropped while reading, has fewer records here than the text had.
        var shapeBroken = raw.ArrayLength != state.GpuSlots.Count;
        var (kept, listBroken) = Sort(state.GpuSlots, raw, shapeBroken);
        var broken = shapeBroken || listBroken;
        var reserved = UnrecordedProgress(state, kept).Select(Placeholder).ToList();
        if (!broken && reserved.Count == 0)
        {
            return false;
        }

        state.GpuSlots = [.. kept.Concat(reserved).OrderBy(static r => r.Slot)];
        if (broken)
        {
            state.GpuSlotsRepaired = true;
        }

        return broken;
    }

    /// <summary>The closed record that keeps a number nobody owns: it matches no adapter, and the number is never handed out (<see cref="GpuSlotRecord.Retired"/>).</summary>
    internal static GpuSlotRecord Placeholder(int slot) => new(slot, string.Empty, 0, null, Retired: true);

    /// <summary>
    /// The numbers 0..5 that hold progress (<see cref="HoldsProgress"/>) and that none of <paramref name="records"/> claims. The one rule for what has to be
    /// reserved, used when a list is loaded and when the map numbers the cards for the first time.
    /// </summary>
    internal static IEnumerable<int> UnrecordedProgress(PartyState state, IEnumerable<GpuSlotRecord> records)
    {
        var recorded = records.Select(static r => r.Slot).ToHashSet();
        for (var slot = 0; slot < GpuSlotMap.MaxSlots; slot++)
        {
            if (!recorded.Contains(slot) && HoldsProgress(state, KeyOf(slot)))
            {
                yield return slot;
            }
        }
    }

    /// <summary>Experience or equipment stored under the key. A key that holds nothing (no entry, no experience above 0, only empty gear slots) needs no reserving.</summary>
    private static bool HoldsProgress(PartyState state, string key) =>
        (state.Experience is { } exp && exp.TryGetValue(key, out var value) && value > 0)
        || (state.Adventure?.Equipped is { } worn && worn.TryGetValue(key, out var gear) && gear is not null && gear.Any(static id => !string.IsNullOrEmpty(id)));

    /// <summary>
    /// The records that can be trusted (by slot number), and whether anything was wrong. A slot number that is claimed twice is trusted for none. A row with
    /// no integer Slot is discarded: when the list that was read has as many rows as the text (<paramref name="shapeBroken"/> false) the rows line up with
    /// the text, so exactly the rows the text marked are discarded and a genuine <c>Slot</c> 0 stays; otherwise the alignment is unknown, so slot 0 is
    /// doubtful (the 0 the deserializer put there may be a guess). Two live records of one exact identity are KEPT, marked disputed: the save cannot say which
    /// card is which, and a record that vanished would let both cards be taken for new ones. So is a live record whose identity a DISCARDED row had too (the
    /// memory counts as equal when it is 0 = unknown on either side, see <see cref="SameDiscardedIdentity"/>; see
    /// <see cref="RepairLoaded"/>), because the discarded row may be its twin. Retired and already disputed records are not compared (so a repaired list
    /// reads back without being repaired again), and neither is a retired discarded row.
    /// </summary>
    private static (List<GpuSlotRecord> Kept, bool Broken) Sort(IReadOnlyList<GpuSlotRecord?> raw, RawGpuSlots probe, bool shapeBroken)
    {
        var broken = false;
        var lost = probe.Lost ?? new Dictionary<int, GpuSlotRecord>();
        var inRange = new List<GpuSlotRecord>();
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            if (r is null || r.Slot is < 0 or >= GpuSlotMap.MaxSlots)
            {
                broken = true;
                continue;
            }

            if (!shapeBroken && lost.ContainsKey(i))
            {
                broken = true;    // the row has no Slot of its own; what its text said is in `lost`
                continue;
            }

            inRange.Add(r with { Name = r.Name ?? string.Empty, VramBytes = Math.Max(0, r.VramBytes), Disputed = r.Disputed && !r.Retired });
        }

        var doubtful = new HashSet<int>();
        if (shapeBroken && lost.Count > 0)
        {
            doubtful.Add(0);
        }

        foreach (var g in inRange.GroupBy(static r => r.Slot).Where(static g => g.Count() > 1))
        {
            doubtful.Add(g.Key);
        }

        // The identities of the rows thrown away: those with no Slot (as the text said them, they may be gone from the list) and those whose number is doubtful.
        var discarded = lost.Values.Concat(inRange.Where(r => doubtful.Contains(r.Slot))).Where(static r => !r.Retired).ToList();

        // At most one record per remaining slot, so at most six to compare.
        var disputed = new HashSet<int>();
        var live = inRange.Where(r => !r.Retired && !r.Disputed && !doubtful.Contains(r.Slot)).ToList();
        for (var i = 0; i < live.Count; i++)
        {
            if (discarded.Any(d => SameDiscardedIdentity(live[i], d)))
            {
                disputed.Add(live[i].Slot);
            }

            for (var j = i + 1; j < live.Count; j++)
            {
                if (SameIdentity(live[i], live[j]))
                {
                    disputed.Add(live[i].Slot);
                    disputed.Add(live[j].Slot);
                }
            }
        }

        broken |= doubtful.Count > 0 || disputed.Count > 0;
        var kept = inRange
            .Where(r => !doubtful.Contains(r.Slot))
            .Select(r => disputed.Contains(r.Slot) ? r with { Disputed = true } : r)
            .OrderBy(static r => r.Slot)
            .ToList();
        return (kept, broken);
    }

    /// <summary>
    /// A discarded row against a kept record: the same name and the same known bus, and a memory that does not contradict them (equal, or 0 = unknown on either
    /// side). A row that was lost or doubtful may have lost its memory too, so unknown memory is not a reason to trust the kept record (the map's exact match
    /// also lets an unknown memory through when the bus is the same).
    /// </summary>
    private static bool SameDiscardedIdentity(GpuSlotRecord kept, GpuSlotRecord discarded) =>
        !string.IsNullOrEmpty(kept.Name) && string.Equals(kept.Name, discarded.Name, StringComparison.Ordinal)
        && kept.PciBus is { } x && discarded.PciBus is { } y && x == y
        && (kept.VramBytes == discarded.VramBytes || kept.VramBytes == 0 || discarded.VramBytes == 0);

    /// <summary>The same name, the same known bus and the same memory: two such records are one card as far as the map can tell (see GpuSlotMap's exact match).</summary>
    private static bool SameIdentity(GpuSlotRecord a, GpuSlotRecord b) =>
        !string.IsNullOrEmpty(a.Name) && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.PciBus is { } x && b.PciBus is { } y && x == y
        && a.VramBytes == b.VramBytes;
}
