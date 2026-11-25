using System.Text;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests;

/// <summary>The GPU slots in the save (PartyState.GpuSlots), GpuSlotMap.Forget, and the join GpuSlotKeys.ApplyTo.</summary>
public class GpuSlotSaveTests
{
    private const long GiB = 1L << 30;

    private static BoardGpu Gpu(string id, string name, long vram, uint? bus) => new(id, name, 10, 1, vram, bus);

    private static GpuSlotRecord Rec(int slot, string name, long vram, uint? bus) => new(slot, name, vram, bus);

    private static BoardFrame Frame(params BoardGpu[]? gpus) =>
        new(8, 10, null, gpus, null, new BoardDisk(null, false), new BoardNet(null, null, false)) { AllGpus = gpus };

    private static int?[] Slots(GpuSlotResult r) => r.Adapters.Select(static a => a.Slot).ToArray();

    // ---- the save ----

    [Fact]
    public void The_slots_survive_a_save_and_load_with_a_null_bus_and_a_zero_memory_kept_as_they_are()
    {
        var state = new PartyState();
        state.Migrate();
        state.GpuSlots = [Rec(0, "GPU A", 12 * GiB, 3u), Rec(1, "GPU B", 0, null), Rec(4, "日本語の名前", 8 * GiB, 0u)];

        var json = state.ToJson();
        var back = PartyState.FromJson(json, out var repaired);

        Assert.False(repaired);
        Assert.Contains("GpuSlots", json);
        Assert.Equal(state.GpuSlots, back.GpuSlots);
        Assert.Null(back.GpuSlots[1].PciBus);
        Assert.Equal(0L, back.GpuSlots[1].VramBytes);
        Assert.Equal(0u, back.GpuSlots[2].PciBus);
    }

    [Fact]
    public void An_old_save_without_the_field_loads_with_no_slots()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"cpu#0":10},"Journey":1,"Chapter":0,"Scene":0}""", out var repaired);

        Assert.False(repaired);
        Assert.NotNull(back.GpuSlots);
        Assert.Empty(back.GpuSlots);
        Assert.Equal(10, back.Experience["cpu#0"]);
    }

    [Fact]
    public void A_null_slot_list_with_no_progress_loads_as_an_empty_one_and_is_reported_as_repaired()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{},"GpuSlots":null}""", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.NotNull(back.GpuSlots);
        Assert.Empty(back.GpuSlots);
    }

    [Fact]
    public void A_slot_list_of_the_wrong_shape_keeps_the_save_and_reserves_the_number_that_holds_progress()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7},"GpuSlots":"oops"}""", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Equal([new GpuSlotRecord(0, "", 0, null, Retired: true)], back.GpuSlots);
        Assert.Equal(7, back.Experience["gpu#0"]);
    }

    [Fact]
    public void The_importer_accepts_an_old_state_and_one_that_has_slots()
    {
        const string old = """{"SaveVersion":1,"Experience":{"cpu#0":10},"Journey":1,"Chapter":0,"Scene":0}""";
        const string withSlots =
            """{"SaveVersion":1,"Experience":{"cpu#0":10},"Journey":1,"Chapter":0,"Scene":0,"GpuSlots":[{"Slot":0,"Name":"A","VramBytes":0,"PciBus":null},{"Slot":1,"Name":"B","VramBytes":1024,"PciBus":5}]}""";

        var a = StateImport.Inspect(Encoding.UTF8.GetBytes(old));
        var b = StateImport.Inspect(Encoding.UTF8.GetBytes(withSlots));

        Assert.Equal(ImportStatus.Acceptable, a.Status);
        Assert.Empty(a.RepairedFields);
        Assert.Equal(ImportStatus.Acceptable, b.Status);
        Assert.Empty(b.RepairedFields);
        Assert.Empty(b.DroppedFields);
        Assert.Equal(2, PartyState.FromJson(Encoding.UTF8.GetString(b.Converted!)).GpuSlots.Count);
    }

    // ---- Forget ----

    [Fact]
    public void Forget_retires_only_that_slot_and_a_slot_with_no_record_changes_nothing()
    {
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u), Rec(2, "C", 8 * GiB, 3u)]);

        var after = map.Forget(1);

        Assert.Equal([0, 1, 2], after.Select(static r => r.Slot).ToArray());
        Assert.Equal([false, true, false], after.Select(static r => r.Retired).ToArray());
        Assert.Equal(after, map.Records);
        Assert.Equal(after, map.Forget(1));    // already gone
        Assert.Equal(after, map.Forget(5));    // never had one
        Assert.Equal(after, map.Forget(-1));
        Assert.Equal(after, map.Forget(6));
    }

    [Fact]
    public void A_forgotten_slot_is_not_handed_to_a_different_card_in_the_same_run()
    {
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u), Rec(2, "C", 8 * GiB, 3u)]);
        map.Forget(1);

        var r = map.Assign([Gpu("a", "A", 8 * GiB, 1u), Gpu("c", "C", 8 * GiB, 3u), Gpu("d", "D", 8 * GiB, 9u)]);

        Assert.Equal([0, 2, 3], Slots(r));    // the new card D takes 3, not the forgotten 1
        Assert.DoesNotContain(r.Adapters, static a => a.Slot == 1);
        Assert.True(r.Records.Single(static x => x.Slot == 1).Retired);    // still in the list, closed
    }

    [Fact]
    public void A_card_that_is_still_plugged_in_after_Forget_is_a_new_card_and_does_not_get_its_old_number_back()
    {
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u)]);
        Assert.Equal([0, 1], Slots(map.Assign([Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)])));
        map.Forget(0);

        var r = map.Assign([Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)]);

        Assert.Equal([2, 1], Slots(r));
    }

    [Fact]
    public void The_first_time_numbering_after_forgetting_everything_also_skips_the_forgotten_slot()
    {
        var map = new GpuSlotMap();
        Assert.Equal([0], Slots(map.Assign([Gpu("a", "A", 8 * GiB, 1u)])));
        map.Forget(0);

        var r = map.Assign([Gpu("a", "A", 8 * GiB, 1u)]);

        Assert.Equal([1], Slots(r));
    }

    [Fact]
    public void A_card_with_no_candidate_never_takes_a_forgotten_slot_even_when_it_is_the_one_slot_left()
    {
        var map = new GpuSlotMap(Enumerable.Range(0, 6).Select(i => Rec(i, "C" + i, 8 * GiB, (uint)(10 + i))));
        map.Forget(2);

        var r = map.Assign(
        [
            Gpu("0", "C0", 8 * GiB, 10u), Gpu("1", "C1", 8 * GiB, 11u), Gpu("3", "C3", 8 * GiB, 13u),
            Gpu("4", "C4", 8 * GiB, 14u), Gpu("5", "C5", 8 * GiB, 15u), Gpu("n", "NEW", 8 * GiB, 20u),
        ]);

        Assert.Equal([0, 1, 3, 4, 5, null], Slots(r));    // the new card waits without a number; 2 stays closed until it is reset
    }

    [Fact]
    public void An_ambiguous_card_never_takes_the_forgotten_slot()
    {
        // Two twins that the save cannot tell apart (no bus on either side), and a third card whose slot is let go.
        var map = new GpuSlotMap([Rec(0, "TWIN", 8 * GiB, null), Rec(1, "TWIN", 8 * GiB, null), Rec(2, "X", 8 * GiB, 7u)]);
        map.Forget(2);

        var r = map.Assign([Gpu("t1", "TWIN", 8 * GiB, null), Gpu("t2", "TWIN", 8 * GiB, null)]);

        Assert.Equal([null, null], Slots(r));
        Assert.All(r.Adapters, static a => Assert.True(a.Ambiguous));
    }

    // ---- ApplyTo ----

    [Fact]
    public void ApplyTo_gives_the_keys_of_the_adapters_that_hold_a_slot_and_saves_the_slots_only_when_they_changed()
    {
        var state = new PartyState();
        state.Migrate();
        var map = new GpuSlotMap(state.GpuSlots);
        var frame = Frame(Gpu("a", "A", 12 * GiB, 3u), Gpu("b", "B", 0, null));

        var first = map.ApplyTo(state, frame);
        var second = map.ApplyTo(state, frame);

        Assert.Equal(["gpu#0", "gpu#1"], first.EarningKeys);
        Assert.True(first.SlotsChanged);
        Assert.Equal([Rec(0, "A", 12 * GiB, 3u), Rec(1, "B", 0, null)], state.GpuSlots);
        Assert.Equal(["gpu#0", "gpu#1"], second.EarningKeys);
        Assert.False(second.SlotsChanged);
    }

    [Fact]
    public void ApplyTo_with_no_adapter_list_assigns_nothing_and_leaves_the_saved_slots_alone()
    {
        var state = new PartyState();
        state.Migrate();
        state.GpuSlots = [Rec(0, "A", 8 * GiB, 1u)];
        var map = new GpuSlotMap(state.GpuSlots);

        var r = map.ApplyTo(state, Frame(null));

        Assert.Empty(r.EarningKeys);
        Assert.Empty(r.Adapters);
        Assert.False(r.SlotsChanged);
        Assert.Equal([Rec(0, "A", 8 * GiB, 1u)], state.GpuSlots);
    }

    [Fact]
    public void ApplyTo_gives_no_key_to_an_ambiguous_adapter_so_it_earns_nothing()
    {
        var state = new PartyState();
        state.Migrate();
        state.GpuSlots = [Rec(0, "TWIN", 8 * GiB, null), Rec(1, "TWIN", 8 * GiB, null)];
        var map = new GpuSlotMap(state.GpuSlots);

        var r = map.ApplyTo(state, Frame(Gpu("t1", "TWIN", 8 * GiB, null), Gpu("t2", "TWIN", 8 * GiB, null)));

        Assert.Empty(r.EarningKeys);
        Assert.All(r.Adapters, static a => Assert.True(a.Ambiguous));
        Assert.False(r.SlotsChanged);
    }

    [Fact]
    public void The_slots_kept_in_the_save_give_the_same_keys_after_a_restart_whatever_the_order()
    {
        var state = new PartyState();
        state.Migrate();
        var cards = new[] { Gpu("a", "TWIN", 0, 3u), Gpu("b", "TWIN", 0, 4u), Gpu("c", "OTHER", 8 * GiB, 5u) };
        var before = new GpuSlotMap(state.GpuSlots).ApplyTo(state, Frame(cards));

        var restarted = PartyState.FromJson(state.ToJson());
        var after = new GpuSlotMap(restarted.GpuSlots).ApplyTo(restarted, Frame(cards[2], Gpu("b2", "TWIN", 0, 4u), Gpu("a2", "TWIN", 0, 3u)));

        Assert.Equal(["gpu#0", "gpu#1", "gpu#2"], before.EarningKeys);
        Assert.Equal(["gpu#2", "gpu#1", "gpu#0"], after.EarningKeys);
        Assert.False(after.SlotsChanged);
    }
}
