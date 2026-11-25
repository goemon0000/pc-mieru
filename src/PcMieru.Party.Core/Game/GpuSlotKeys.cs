using PcMieru.Party.Hardware;

namespace PcMieru.Party.Game;

/// <summary>What <see cref="GpuSlotKeys.ApplyTo"/> settled for one frame.</summary>
/// <param name="Adapters">One entry per GPU of the frame (<see cref="BoardFrame.AllGpus"/>), in the same order; empty when the frame has no GPU list.</param>
/// <param name="Keys">The <c>gpu#i</c> keys of the adapters that hold a slot, in adapter order. An ambiguous adapter, or one with no slot left, has no key here, so it earns nothing.</param>
/// <param name="SlotsChanged">True when <see cref="PartyState.GpuSlots"/> was rewritten by this call: the save has changed and should be written.</param>
public sealed record GpuSlotFrame(IReadOnlyList<GpuSlotAssignment> Adapters, IReadOnlyList<string> Keys, bool SlotsChanged);

/// <summary>
/// The join between the board frame, the slot map and the save (DESIGN-C 2.3 and 4.2). The map is built once per run from the save
/// (<c>new GpuSlotMap(state.GpuSlots)</c>); every frame then goes through <see cref="ApplyTo"/>.
/// </summary>
public static class GpuSlotKeys
{
    /// <summary>
    /// Assigns the slots of the frame's GPUs, writes the remembered slots into <paramref name="state"/> when they changed (the only part of the state this
    /// touches), and gives back the keys the rules layer may add experience under. A frame whose adapter list Core could not read
    /// (<see cref="BoardFrame.AllGpus"/> null) assigns nothing, forgets nothing and gives no key: an unknown list is not "no GPU" and not a reason to move a slot.
    /// Progress is never reassigned here: an ambiguous adapter has no key until the match becomes clear.
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

        var result = map.Assign(all);
        var keys = result.Adapters.Select(static a => a.Key).OfType<string>().ToArray();
        state.GpuSlots ??= new List<GpuSlotRecord>();
        var changed = !state.GpuSlots.SequenceEqual(result.Records);
        if (changed)
        {
            state.GpuSlots = [.. result.Records];
        }

        return new GpuSlotFrame(result.Adapters, keys, changed);
    }
}
