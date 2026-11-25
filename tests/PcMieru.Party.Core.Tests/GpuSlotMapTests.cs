using System.Text.Json;
using PcMieru.Party.Hardware;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// The GPU slot map (DESIGN-C 2.3): the first time the old order, then the numbers stay put. Plugging a card in or out never compacts the others,
/// no card inherits another card's <c>gpu#i</c>, and a match nothing can settle stops progressing instead of being guessed.
/// </summary>
public class GpuSlotMapTests
{
    private const long GiB = 1L << 30;

    private static BoardGpu Card(string id, string name, long vram, uint? bus) => new(id, name, 10, GiB, vram, bus);

    // Three different cards (VRAM descending is also the old roster order) and a twin pair.
    private static BoardGpu Ti(string id = "a", uint? bus = 9) => Card(id, "GeForce RTX 3080 Ti", 12 * GiB, bus);

    private static BoardGpu Rtx(string id = "b", uint? bus = 1) => Card(id, "GeForce RTX 5070", 11 * GiB, bus);

    private static BoardGpu Radeon(string id = "c", uint? bus = 17) => Card(id, "Radeon Graphics", GiB / 2, bus);

    private static BoardGpu Fourth(string id = "d", uint? bus = 21) => Card(id, "GeForce GTX 1060", 6 * GiB, bus);

    private static int?[] Slots(GpuSlotResult r) => r.Adapters.Select(a => a.Slot).ToArray();

    /// <summary>The same machine after a reboot: the remembered slots come back from the save, every LUID is new.</summary>
    private static GpuSlotMap Reboot(GpuSlotMap map) => new(map.Records);

    [Fact]
    public void The_first_run_numbers_the_roster_order_exactly_like_the_old_app()
    {
        // Same data as the roster test of the dev machine; the numbers are the old gpu#0, gpu#1, gpu#2.
        var real = GpuRoster.Real(
        [
            new("0:24F84", "", 12_520_390_656, null, false),
            new("0:18844", "Radeon Graphics", 508_559_360, 17, false),
            new("0:177E8", "GeForce RTX 3080 Ti", 12_670_992_384, 9, false),
            new("0:16136", "GeForce RTX 5070", 12_520_390_656, 1, false),
            new("0:177A6", "", 0, null, true),
        ]);
        var board = real.Select(a => Card(a.LuidKey, a.Name, (long)a.DedicatedBytes, a.PciBus)).ToArray();

        var result = new GpuSlotMap().Assign(board);

        Assert.Equal(new int?[] { 0, 1, 2 }, Slots(result));
        Assert.Equal(new[] { "0:177E8", "0:16136", "0:18844" }, result.Adapters.Select(a => a.Id).ToArray());
        Assert.Equal(new[] { "gpu#0", "gpu#1", "gpu#2" }, result.Adapters.Select(a => a.Key).ToArray());
        Assert.All(result.Adapters, a => Assert.False(a.Ambiguous));
        Assert.Equal(3, result.Records.Count);
        Assert.Equal(new GpuSlotRecord(0, "GeForce RTX 3080 Ti", 12_670_992_384, 9), result.Records[0]);
    }

    [Fact]
    public void Equal_vram_twins_on_a_first_run_follow_the_roster_order_which_the_bus_now_settles()
    {
        var lowBus = Card("x9", "Twin", 8 * GiB, 3);
        var highBus = Card("x1", "Twin", 8 * GiB, 9);

        // The roster puts the lower bus first (BoardFrame does the sorting; the map only follows the order it is given).
        var ordered = GpuRoster.Order([new("x9", "Twin", 8UL << 30, 3, false), new("x1", "Twin", 8UL << 30, 9, false)]);
        Assert.Equal(new[] { "x9", "x1" }, ordered.Select(a => a.LuidKey).ToArray());

        var result = new GpuSlotMap().Assign([lowBus, highBus]);

        Assert.Equal(new int?[] { 0, 1 }, Slots(result));
    }

    [Fact]
    public void The_same_topology_keeps_every_slot_even_when_the_order_and_the_ids_change()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx(), Radeon()]);

        var again = Reboot(map).Assign([Radeon("z1"), Ti("z2"), Rtx("z3")]);

        Assert.Equal(new int?[] { 2, 0, 1 }, Slots(again));
        Assert.All(again.Adapters, a => Assert.False(a.Ambiguous));
    }

    [Fact]
    public void Unplugging_the_first_card_leaves_a_hole_and_the_others_keep_their_slots()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx(), Radeon()]);

        var without = map.Assign([Rtx(), Radeon()]);

        Assert.Equal(new int?[] { 1, 2 }, Slots(without));
        Assert.Equal(3, without.Records.Count); // the gone card's slot is still reserved
        Assert.Equal(0, without.Records[0].Slot);
    }

    [Fact]
    public void A_replugged_card_gets_its_old_slot_back_under_a_new_id()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx(), Radeon()]);
        map.Assign([Rtx(), Radeon()]);

        var back = map.Assign([Ti("new-luid"), Rtx(), Radeon()]);

        Assert.Equal(new int?[] { 0, 1, 2 }, Slots(back));
        Assert.All(back.Adapters, a => Assert.False(a.Ambiguous));
    }

    [Fact]
    public void A_new_card_takes_the_lowest_slot_nobody_ever_had_and_never_a_hole()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx(), Radeon()]);
        map.Assign([Rtx(), Radeon()]); // slot 0 is a hole now

        var result = map.Assign([Rtx(), Radeon(), Fourth()]);

        Assert.Equal(new int?[] { 1, 2, 3 }, Slots(result)); // 3, not the hole at 0
        Assert.Equal(4, result.Records.Count);
    }

    [Fact]
    public void A_new_card_in_front_of_the_roster_does_not_push_the_others_along()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx()]);

        // The new card is the biggest, so it comes first in the roster; the old compact list would have moved everyone down.
        var big = Card("big", "Titan", 24 * GiB, 5);
        var result = map.Assign([big, Ti(), Rtx()]);

        Assert.Equal(new int?[] { 2, 0, 1 }, Slots(result));
    }

    [Fact]
    public void With_no_slot_left_a_new_card_has_none_and_a_hole_is_not_free()
    {
        var six = Enumerable.Range(0, 6).Select(i => Card("id" + i, "Card " + i, (20 - i) * GiB, (uint)i)).ToArray();
        var map = new GpuSlotMap();
        map.Assign(six);

        // Card 0 is gone and a different card is plugged in: the hole is reserved, so the newcomer waits without a slot.
        var result = map.Assign([.. six.Skip(1), Card("newcomer", "Another", 30 * GiB, 40)]);

        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, null }, Slots(result));
        Assert.False(result.Adapters[^1].Ambiguous);
        Assert.Null(result.Adapters[^1].Key);
        Assert.Equal(6, result.Records.Count);
    }

    [Fact]
    public void More_than_six_cards_the_seventh_and_beyond_have_no_slot()
    {
        var eight = Enumerable.Range(0, 8).Select(i => Card("id" + i, "Card " + i, (20 - i) * GiB, (uint)i)).ToArray();
        var map = new GpuSlotMap();

        var first = map.Assign(eight);
        var next = map.Assign(eight);

        Assert.Equal(new int?[] { 0, 1, 2, 3, 4, 5, null, null }, Slots(first));
        Assert.Equal(Slots(first), Slots(next));
        Assert.Equal(6, next.Records.Count);
        Assert.All(next.Adapters, a => Assert.False(a.Ambiguous));
    }

    [Fact]
    public void Zero_cards_give_nothing_and_forget_nothing()
    {
        var map = new GpuSlotMap();
        var none = map.Assign([]);
        Assert.Empty(none.Adapters);
        Assert.Empty(none.Records);

        map.Assign([Ti(), Rtx()]);
        var gone = map.Assign([]);
        Assert.Empty(gone.Adapters);
        Assert.Equal(2, gone.Records.Count);

        // The list came back after a frame with no GPUs: the same slots, not a fresh numbering.
        Assert.Equal(new int?[] { 1, 0 }, Slots(map.Assign([Rtx(), Ti()])));
    }

    [Fact]
    public void Twin_cards_whose_bus_is_unknown_after_a_reboot_are_ambiguous_and_take_no_slot()
    {
        var map = new GpuSlotMap();
        var first = map.Assign([Card("t1", "Twin", 8 * GiB, 3), Card("t2", "Twin", 8 * GiB, 9)]);
        Assert.Equal(new int?[] { 0, 1 }, Slots(first));

        var after = Reboot(map).Assign([Card("n1", "Twin", 8 * GiB, null), Card("n2", "Twin", 8 * GiB, null)]);

        Assert.Equal(new int?[] { null, null }, Slots(after));
        Assert.All(after.Adapters, a => Assert.True(a.Ambiguous));
        Assert.Equal(first.Records, after.Records); // nothing was reassigned, nothing forgotten
    }

    [Fact]
    public void Ambiguous_twins_do_not_let_a_new_card_take_their_slots_and_stay_ambiguous_until_the_bus_is_known()
    {
        var map = new GpuSlotMap();
        map.Assign([Card("t1", "Twin", 8 * GiB, 3), Card("t2", "Twin", 8 * GiB, 9)]);
        var rebooted = Reboot(map);

        var blind = rebooted.Assign([Card("n1", "Twin", 8 * GiB, null), Card("n2", "Twin", 8 * GiB, null), Ti()]);
        var again = rebooted.Assign([Card("n1", "Twin", 8 * GiB, null), Card("n2", "Twin", 8 * GiB, null), Ti()]);
        var clear = rebooted.Assign([Card("n1", "Twin", 8 * GiB, 9), Card("n2", "Twin", 8 * GiB, 3), Ti()]);

        Assert.Equal(new int?[] { null, null, 2 }, Slots(blind));
        Assert.Equal(Slots(blind), Slots(again));
        Assert.Equal(new int?[] { 1, 0, 2 }, Slots(clear)); // the bus settled it, by the bus and not by the order
        Assert.All(clear.Adapters, a => Assert.False(a.Ambiguous));
    }

    [Fact]
    public void One_twin_left_with_an_unknown_bus_cannot_say_which_twin_it_is()
    {
        var map = new GpuSlotMap();
        map.Assign([Card("t1", "Twin", 8 * GiB, 3), Card("t2", "Twin", 8 * GiB, 9)]);

        var result = Reboot(map).Assign([Card("n1", "Twin", 8 * GiB, null)]);

        Assert.Equal(new int?[] { null }, Slots(result));
        Assert.True(result.Adapters[0].Ambiguous);
    }

    [Fact]
    public void One_twin_left_with_a_known_bus_is_the_twin_with_that_bus()
    {
        var map = new GpuSlotMap();
        map.Assign([Card("t1", "Twin", 8 * GiB, 3), Card("t2", "Twin", 8 * GiB, 9)]);

        var result = Reboot(map).Assign([Card("n1", "Twin", 8 * GiB, 9)]);

        Assert.Equal(new int?[] { 1 }, Slots(result));
    }

    [Fact]
    public void A_new_twin_next_to_a_card_that_was_alone_makes_both_ambiguous_when_the_bus_cannot_tell()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(bus: null)]);

        var result = Reboot(map).Assign([Ti("p", null), Ti("q", null)]);

        Assert.Equal(new int?[] { null, null }, Slots(result));
        Assert.All(result.Adapters, a => Assert.True(a.Ambiguous));
    }

    [Fact]
    public void Twins_settled_in_this_run_stay_settled_when_a_bus_reads_null_later()
    {
        var map = new GpuSlotMap();
        var first = map.Assign([Card("t1", "Twin", 8 * GiB, 3), Card("t2", "Twin", 8 * GiB, 9)]);

        // A frame where the kernel did not answer for either card. The ids are the same inside one run, so nothing changes.
        var blind = map.Assign([Card("t1", "Twin", 8 * GiB, null), Card("t2", "Twin", 8 * GiB, null)]);

        Assert.Equal(Slots(first), Slots(blind));
        Assert.All(blind.Adapters, a => Assert.False(a.Ambiguous));
        Assert.Equal(first.Records, blind.Records); // the bus that was remembered is not erased by a read that failed
    }

    [Fact]
    public void Twins_without_any_bus_on_a_first_run_keep_their_numbers_in_the_run_and_are_ambiguous_after_a_reboot()
    {
        var map = new GpuSlotMap();
        var one = map.Assign([Card("t1", "Twin", 8 * GiB, null), Card("t2", "Twin", 8 * GiB, null)]);
        var two = map.Assign([Card("t1", "Twin", 8 * GiB, null), Card("t2", "Twin", 8 * GiB, null)]);

        var rebooted = Reboot(map).Assign([Card("u1", "Twin", 8 * GiB, null), Card("u2", "Twin", 8 * GiB, null)]);

        Assert.Equal(new int?[] { 0, 1 }, Slots(one));
        Assert.Equal(Slots(one), Slots(two));
        Assert.All(rebooted.Adapters, a => Assert.True(a.Ambiguous));
    }

    [Fact]
    public void A_card_whose_bus_moved_keeps_its_slot_when_it_is_the_only_one_it_can_be()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(bus: 9), Rtx(bus: 1)]);

        var moved = Reboot(map).Assign([Ti("m", 12), Rtx("n", 1)]);

        Assert.Equal(new int?[] { 0, 1 }, Slots(moved));
        Assert.Equal(12u, moved.Records[0].PciBus); // the new bus is remembered
    }

    [Fact]
    public void A_card_with_a_different_name_is_a_new_card_not_a_guess()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx()]);

        var result = Reboot(map).Assign([Card("x", "NVIDIA GeForce RTX 3080 Ti", 12 * GiB, 9), Rtx()]);

        Assert.Equal(new int?[] { 2, 1 }, Slots(result)); // slot 0 stays reserved for the card that is gone
    }

    [Fact]
    public void An_unknown_memory_size_matches_the_remembered_card_instead_of_making_a_new_one()
    {
        var map = new GpuSlotMap();
        map.Assign([Ti(), Rtx()]);

        var result = Reboot(map).Assign([Card("x", "GeForce RTX 3080 Ti", 0, null), Rtx()]);

        Assert.Equal(new int?[] { 0, 1 }, Slots(result));
        Assert.Equal(12 * GiB, result.Records[0].VramBytes); // the size remembered is not erased by one that was not read
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_the_adapters_inside_one_tier()
    {
        var map = new GpuSlotMap();
        map.Assign([Card("t1", "Twin", 8 * GiB, 3), Card("t2", "Twin", 8 * GiB, 9), Ti()]);
        var rebooted = Reboot(map);

        var a = rebooted.Assign([Card("n1", "Twin", 8 * GiB, 9), Ti("k"), Card("n2", "Twin", 8 * GiB, null)]);
        var b = Reboot(map).Assign([Card("n2", "Twin", 8 * GiB, null), Card("n1", "Twin", 8 * GiB, 9), Ti("k")]);

        Assert.Equal(new int?[] { 1, 2, 0 }, Slots(a)); // n1 is the bus-9 twin; n2 is the one twin left, so it is the bus-3 twin
        Assert.Equal(0, a.Adapters[2].Slot);
        Assert.Equal(a.Adapters.ToDictionary(x => x.Id, x => x.Slot), b.Adapters.ToDictionary(x => x.Id, x => x.Slot));
    }

    private static GpuSlotMap Remembered(params GpuSlotRecord[] records) => new(records);

    private static IEnumerable<T[]> Permutations<T>(T[] items) =>
        items.Length <= 1
            ? [items]
            : items.SelectMany((item, i) => Permutations(items.Where((_, j) => j != i).ToArray()).Select(rest => new[] { item }.Concat(rest).ToArray()));

    [Fact]
    public void A_surviving_twin_with_an_unreadable_bus_does_not_inherit_the_absent_twins_slot_whose_bus_was_never_known()
    {
        // Twin A was remembered with its bus, twin B was not (its bus did not read that time). After a reboot one twin is left and its bus does not read.
        var map = Remembered(new GpuSlotRecord(0, "Twin", 8 * GiB, 3), new GpuSlotRecord(1, "Twin", 8 * GiB, null));

        var result = map.Assign([Card("n1", "Twin", 8 * GiB, null)]);

        Assert.Equal(new int?[] { null }, Slots(result));
        Assert.True(result.Adapters[0].Ambiguous); // null == null used to hand it slot 1 as an "exact" match
        Assert.Equal(2, result.Records.Count);
        Assert.Null(result.Records[1].PciBus);
    }

    [Fact]
    public void The_same_survivor_with_the_other_twin_unknown_is_ambiguous_too_and_one_with_a_known_bus_is_still_settled()
    {
        // The other way round: the remembered twin with the unknown bus comes first.
        var records = new[] { new GpuSlotRecord(0, "Twin", 8 * GiB, null), new GpuSlotRecord(1, "Twin", 8 * GiB, 9) };

        var blind = Remembered(records).Assign([Card("n1", "Twin", 8 * GiB, null)]);
        var known = Remembered(records).Assign([Card("n2", "Twin", 8 * GiB, 9)]);

        Assert.Equal(new int?[] { null }, Slots(blind));
        Assert.True(blind.Adapters[0].Ambiguous);
        Assert.Equal(new int?[] { 1 }, Slots(known)); // known on both sides and equal: exact
        Assert.False(known.Adapters[0].Ambiguous);
    }

    [Fact]
    public void A_remembered_twin_with_no_bus_does_not_make_the_known_bus_survivor_lose_its_exact_slot()
    {
        var records = new[] { new GpuSlotRecord(0, "Twin", 8 * GiB, 3), new GpuSlotRecord(1, "Twin", 8 * GiB, null) };

        var result = Remembered(records).Assign([Card("n1", "Twin", 8 * GiB, 3)]);

        Assert.Equal(new int?[] { 0 }, Slots(result));
        Assert.False(result.Adapters[0].Ambiguous);
    }

    [Fact]
    public void A_name_and_memory_match_with_no_bus_on_either_side_still_settles_when_it_is_unique()
    {
        var map = Remembered(new GpuSlotRecord(3, "Lone", 4 * GiB, null));

        var result = map.Assign([Card("n1", "Lone", 4 * GiB, null)]);

        Assert.Equal(new int?[] { 3 }, Slots(result));
        Assert.False(result.Adapters[0].Ambiguous);
    }

    [Fact]
    public void An_unknown_memory_size_is_not_an_exact_match_even_with_the_same_bus_so_twins_stay_ambiguous()
    {
        var map = Remembered(new GpuSlotRecord(0, "Twin", 0, 3), new GpuSlotRecord(1, "Twin", 0, 9));

        var result = map.Assign([Card("n1", "Twin", 0, 3)]);

        Assert.Equal(new int?[] { null }, Slots(result));
        Assert.True(result.Adapters[0].Ambiguous);
    }

    [Fact]
    public void An_adapter_whose_only_candidates_were_blocked_by_an_ambiguous_match_is_ambiguous_never_a_new_card_in_any_order()
    {
        // Two remembered twins share one bus (a bus that moved onto the other's). n1 matches both exactly, so both slots are held back; n2 (another
        // bus) could be either of them, so it is not "a new card" either. Only the third card, which no record knows, takes a free slot.
        var records = new[] { new GpuSlotRecord(0, "Twin", 8 * GiB, 3), new GpuSlotRecord(1, "Twin", 8 * GiB, 3) };
        var adapters = new[] { Card("n1", "Twin", 8 * GiB, 3), Card("n2", "Twin", 8 * GiB, 7), Ti("k") };

        Dictionary<string, (int? Slot, bool Ambiguous)>? first = null;
        foreach (var order in Permutations(adapters))
        {
            var result = Remembered(records).Assign(order);
            var byId = result.Adapters.ToDictionary(a => a.Id, a => (a.Slot, a.Ambiguous));

            Assert.Equal((null, true), byId["n1"]);
            Assert.Equal((null, true), byId["n2"]);
            Assert.Equal(((int?)2, false), byId["k"]); // not slot 2 for n2 and k to fight over by their order
            Assert.Equal(3, result.Records.Count);
            first ??= byId;
            Assert.Equal(first, byId);
        }
    }

    [Fact]
    public void An_adapter_that_could_be_a_blocked_slot_or_a_free_one_is_ambiguous_and_the_result_ignores_the_order()
    {
        // Slots 0 and 1 hold the same twin identity (blocked by n1's exact double match); n2 also fits slot 2, a third twin of the same model
        // remembered with another bus. n2 could be slot 0, 1 or 2, so it takes none.
        var records = new[]
        {
            new GpuSlotRecord(0, "Twin", 8 * GiB, 3),
            new GpuSlotRecord(1, "Twin", 8 * GiB, 3),
            new GpuSlotRecord(2, "Twin", 8 * GiB, 9),
        };
        var adapters = new[] { Card("n1", "Twin", 8 * GiB, 3), Card("n2", "Twin", 8 * GiB, 12) };

        foreach (var order in Permutations(adapters))
        {
            var result = Remembered(records).Assign(order);
            var byId = result.Adapters.ToDictionary(a => a.Id, a => (a.Slot, a.Ambiguous));

            Assert.Equal((null, true), byId["n1"]);
            Assert.Equal((null, true), byId["n2"]);
        }
    }

    [Fact]
    public void An_id_listed_twice_gets_one_slot_and_the_copy_gets_none()
    {
        var result = new GpuSlotMap().Assign([Ti("same"), Ti("same")]);

        Assert.Equal(new int?[] { 0, null }, Slots(result));
        Assert.False(result.Adapters[1].Ambiguous);
    }

    [Fact]
    public void The_records_are_plain_json_and_come_back_the_same()
    {
        var map = new GpuSlotMap();
        var records = map.Assign([Ti(), Radeon(bus: null)]).Records;

        var json = JsonSerializer.Serialize(records);
        var back = JsonSerializer.Deserialize<GpuSlotRecord[]>(json)!;

        Assert.Equal(records, back);
        Assert.Contains("\"Slot\":0", json);
        Assert.Contains("\"PciBus\":null", json);
        // The save gives them back to a new map: the same cards have the same slots.
        Assert.Equal(new int?[] { 0, 1 }, Slots(new GpuSlotMap(back).Assign([Ti("z"), Radeon("y", null)])));
    }

    [Fact]
    public void Remembered_records_that_make_no_sense_are_ignored_not_trusted()
    {
        var map = new GpuSlotMap(
        [
            new GpuSlotRecord(-1, "Neg", GiB, null),
            new GpuSlotRecord(6, "Past the end", GiB, null),
            new GpuSlotRecord(2, "GeForce RTX 3080 Ti", 12 * GiB, 9),
            new GpuSlotRecord(2, "A second record for the same slot", GiB, 1),
        ]);

        Assert.Equal(new[] { 2 }, map.Records.Select(r => r.Slot).ToArray());
        Assert.Equal(new int?[] { 2, 0 }, Slots(map.Assign([Ti(), Rtx()])));
    }
}
