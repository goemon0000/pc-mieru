using System.Text;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// DESIGN-C 9 (a card's progress is never handed to another card by itself; an ambiguous card earns nothing): the codex review of 1b6906c..53ed7e0.
/// Progress that no record claims is reserved whenever the list is set up, a record with no Slot is not slot 0, two records of one identity are
/// DISPUTED (blocked, not dropped), and the normal save keeps its old shape.
/// </summary>
public class GpuSlotReserveTests
{
    private const long GiB = 1L << 30;

    private static BoardGpu Gpu(string id, string name, long vram, uint? bus, double? percent = 10) => new(id, name, percent, 1, vram, bus);

    private static GpuSlotRecord Rec(int slot, string name, long vram, uint? bus) => new(slot, name, vram, bus);

    private static GpuSlotRecord Closed(int slot) => new(slot, "", 0, null, Retired: true);

    private static BoardFrame Frame(params BoardGpu[]? gpus) =>
        new(8, 10, null, gpus, null, new BoardDisk(null, false), new BoardNet(null, null, false)) { AllGpus = gpus };

    private static int?[] Slots(GpuSlotResult r) => r.Adapters.Select(static a => a.Slot).ToArray();

    private static IEnumerable<T[]> Permutations<T>(T[] items) =>
        items.Length <= 1
            ? [items]
            : items.SelectMany((item, i) => Permutations(items.Where((_, j) => j != i).ToArray()).Select(rest => new[] { item }.Concat(rest).ToArray()));

    // ---- item 1: progress no record claims is reserved ----

    [Fact]
    public void A_present_empty_list_with_gpu1_progress_never_gives_a_card_the_number_1()
    {
        var state = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#1":7},"GpuSlots":[]}""", out var repaired);
        Assert.False(repaired);
        Assert.Equal([Closed(1)], state.GpuSlots);

        var cards = new[] { Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u), Gpu("c", "C", 8 * GiB, 3u) };
        var frame = new GpuSlotMap(state.GpuSlots).ApplyTo(state, Frame(cards));

        Assert.Equal([0, 2, 3], frame.Adapters.Select(static a => a.Slot ?? -1).ToArray());
        Assert.DoesNotContain("gpu#1", frame.EarningKeys);
        Assert.Equal(7, state.Experience["gpu#1"]);

        // and after a restart, and with the cards in any other order
        var again = PartyState.FromJson(state.ToJson());
        foreach (var order in Permutations(cards))
        {
            var r = new GpuSlotMap(again.GpuSlots).Assign(order);
            Assert.DoesNotContain(1, r.Adapters.Select(static a => a.Slot ?? -1));
        }
    }

    [Fact]
    public void An_old_save_numbered_with_fewer_cards_than_progress_reserves_the_numbers_left_over()
    {
        var state = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7,"gpu#1":5},"Adventure":{"Equipped":{"gpu#3":["sword",""]}}}""", out var repaired);
        Assert.False(repaired);
        Assert.Empty(state.GpuSlots);    // absent field: not numbered yet
        var map = new GpuSlotMap(state.GpuSlots);

        var first = map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u)));

        Assert.Equal([0], first.Adapters.Select(static a => a.Slot ?? -1).ToArray());    // the first-run numbering is unchanged: the card takes gpu#0
        Assert.Equal(["gpu#0"], first.EarningKeys);
        Assert.True(first.SlotsChanged);
        Assert.Equal([Rec(0, "A", 8 * GiB, 1u), Closed(1), Closed(3)], state.GpuSlots);    // 1 and 3 hold progress and no card: closed

        // the same map, a second card plugged in during the run, and a restart with a third: none of them gets 1 or 3
        var second = map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)));
        Assert.Equal([0, 2], second.Adapters.Select(static a => a.Slot ?? -1).ToArray());

        var restarted = PartyState.FromJson(state.ToJson(), out var repairedAgain);
        Assert.False(repairedAgain);
        var third = new GpuSlotMap(restarted.GpuSlots).ApplyTo(restarted, Frame(
            Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u), Gpu("c", "C", 8 * GiB, 3u), Gpu("d", "D", 8 * GiB, 4u)));
        Assert.Equal([0, 2, 4, 5], third.Adapters.Select(static a => a.Slot ?? -1).ToArray());
        Assert.Equal(5, restarted.Experience["gpu#1"]);
        Assert.Equal(["sword", ""], restarted.Adventure!.Equipped["gpu#3"]);
    }

    [Fact]
    public void Numbering_an_old_save_whose_progress_all_has_a_card_reserves_nothing()
    {
        var state = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7,"gpu#1":5,"gpu#2":0}}""");
        var map = new GpuSlotMap(state.GpuSlots);

        map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)));

        Assert.Equal([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u)], state.GpuSlots);    // gpu#2 holds no experience: free
    }

    [Fact]
    public void A_frame_with_no_adapter_numbers_nothing_so_it_reserves_nothing_and_the_next_one_still_numbers_in_the_old_order()
    {
        var state = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7,"gpu#1":5}}""");
        var map = new GpuSlotMap(state.GpuSlots);

        var none = map.ApplyTo(state, Frame());
        Assert.False(none.SlotsChanged);
        Assert.Empty(state.GpuSlots);

        var later = map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)));
        Assert.Equal(["gpu#0", "gpu#1"], later.EarningKeys);
    }

    [Fact]
    public void An_old_save_that_is_saved_again_before_any_numbering_is_still_an_old_save_and_numbers_in_the_old_order()
    {
        var state = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7,"gpu#1":5}}""");

        var json = state.ToJson();    // an empty list is not written: "absent" and "empty" mean the same, never numbered
        var back = PartyState.FromJson(json, out var repaired);

        Assert.DoesNotContain("GpuSlots\"", json);
        Assert.False(repaired);
        Assert.Empty(back.GpuSlots);
        var r = new GpuSlotMap(back.GpuSlots).ApplyTo(back, Frame(Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)));
        Assert.Equal(["gpu#0", "gpu#1"], r.EarningKeys);
    }

    // ---- item 2: a record without an explicit Slot is not slot 0 ----

    private const string Progress0 = "\"SaveVersion\":1,\"Journey\":1,\"Chapter\":0,\"Scene\":0,\"Experience\":{\"gpu#0\":7}";

    [Fact]
    public void An_element_with_only_Name_memory_and_bus_is_repaired_and_no_card_earns_under_gpu0_through_it()
    {
        var back = PartyState.FromJson("{" + Progress0 + ",\"GpuSlots\":[{\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}]}", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Equal([Closed(0)], back.GpuSlots);    // not a live record of slot 0: the number is only reserved
        Assert.Equal(7, back.Experience["gpu#0"]);

        var r = new GpuSlotMap(back.GpuSlots).ApplyTo(back, Frame(Gpu("a", "A", 1024, 1u)));
        Assert.Equal(["gpu#1"], r.EarningKeys);    // the card named in that element is a new card, not gpu#0
    }

    [Theory]
    [InlineData("{\"slot\":0,\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}")]     // spelled differently: an unknown field, as for the serializer
    [InlineData("{\"Slot\":null,\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}")]
    [InlineData("{\"Slot\":\"0\",\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}")]
    [InlineData("{\"Slot\":0.5,\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}")]
    [InlineData("{\"Name\":\"A\",\"Slot\":0,\"Slot\":\"x\"}")]                       // the last Slot wins, and it is not a number
    public void Every_kind_of_missing_Slot_is_a_broken_list_and_keeps_slot_0_closed(string element)
    {
        var back = PartyState.FromJson("{" + Progress0 + ",\"GpuSlots\":[" + element + "]}", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.DoesNotContain(back.GpuSlots, static r => r.Slot == 0 && !r.Retired);
        Assert.Equal([Closed(0)], back.GpuSlots);
        Assert.Equal(7, back.Experience["gpu#0"]);
    }

    [Fact]
    public void A_record_with_a_Slot_next_to_one_without_keeps_the_good_one_and_closes_slot_0()
    {
        var back = PartyState.FromJson(
            "{" + Progress0 + ",\"GpuSlots\":[{\"Slot\":3,\"Name\":\"C\",\"VramBytes\":1,\"PciBus\":4},{\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}]}", out var repaired);

        Assert.True(repaired);
        Assert.Equal([Closed(0), Rec(3, "C", 1, 4u)], back.GpuSlots);
    }

    [Fact]
    public void A_missing_Slot_with_no_progress_is_still_reported_and_leaves_nothing_at_slot_0()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"cpu#0":1},"GpuSlots":[{"Name":"A","VramBytes":1024,"PciBus":1}]}""", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Empty(back.GpuSlots);
    }

    [Fact]
    public void A_record_that_does_say_Slot_0_is_not_a_broken_list()
    {
        var back = PartyState.FromJson("{" + Progress0 + ",\"GpuSlots\":[{\"Slot\":0,\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}]}", out var repaired);

        Assert.False(repaired);
        Assert.False(back.GpuSlotsRepaired);
        Assert.Equal([Rec(0, "A", 1024, 1u)], back.GpuSlots);
    }

    [Fact]
    public void The_importer_reports_an_element_with_no_Slot_as_needing_repair()
    {
        var verdict = StateImport.Inspect(Encoding.UTF8.GetBytes("{" + Progress0 + ",\"GpuSlots\":[{\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}]}"));

        Assert.Equal(ImportStatus.NeedsRepair, verdict.Status);
        Assert.Contains(verdict.RepairedFields, static f => f.StartsWith(nameof(PartyState.GpuSlots), StringComparison.Ordinal));
        Assert.Equal([Closed(0)], PartyState.FromJson(Encoding.UTF8.GetString(verdict.Converted!)).GpuSlots);
    }

    // ---- item 3: two records of one identity are disputed, not dropped ----

    private const string TwinsProgress =
        "\"SaveVersion\":1,\"Journey\":1,\"Chapter\":0,\"Scene\":0,\"Experience\":{\"gpu#0\":7,\"gpu#1\":6,\"gpu#2\":5,\"gpu#4\":4}";

    private const string TwinsList =
        "[{\"Slot\":0,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5},{\"Slot\":1,\"Name\":\"C\",\"VramBytes\":2048,\"PciBus\":4},{\"Slot\":2,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5}]";

    private static PartyState TwinsState(out bool repaired) =>
        PartyState.FromJson("{" + TwinsProgress + ",\"GpuSlots\":" + TwinsList + "}", out repaired);

    [Fact]
    public void The_twin_duplicate_is_repaired_into_two_disputed_records_and_both_cards_are_ambiguous()
    {
        var state = TwinsState(out var repaired);

        Assert.True(repaired);
        Assert.True(state.GpuSlotsRepaired);
        Assert.Equal(
            [new GpuSlotRecord(0, "T", 1024, 5u, Disputed: true), Rec(1, "C", 2048, 4u), new GpuSlotRecord(2, "T", 1024, 5u, Disputed: true), Closed(4)],
            state.GpuSlots);

        var frame = new GpuSlotMap(state.GpuSlots).ApplyTo(state, Frame(
            Gpu("t1", "T", 1024, 5u), Gpu("t2", "T", 1024, 5u), Gpu("c", "C", 2048, 4u)));

        Assert.Equal([null, null, 1], frame.Adapters.Select(static a => a.Slot).ToArray());
        Assert.Equal([true, true, false], frame.Adapters.Select(static a => a.Ambiguous).ToArray());
        Assert.Equal(["gpu#1"], frame.EarningKeys);    // the twins earn nothing
        Assert.Equal(7, state.Experience["gpu#0"]);
        Assert.Equal(5, state.Experience["gpu#2"]);
    }

    [Fact]
    public void After_a_reload_the_disputed_twins_are_still_ambiguous_and_the_list_is_not_repaired_again()
    {
        var state = TwinsState(out _);
        var json = state.ToJson();

        var back = PartyState.FromJson(json, out var repairedAgain);

        Assert.False(repairedAgain);
        Assert.True(back.GpuSlotsRepaired);    // the sign of the first repair is saved
        Assert.Equal(state.GpuSlots, back.GpuSlots);
        Assert.Equal(2, back.GpuSlots.Count(static r => r.Disputed));

        var frame = new GpuSlotMap(back.GpuSlots).ApplyTo(back, Frame(Gpu("t1", "T", 1024, 5u), Gpu("t2", "T", 1024, 5u)));
        Assert.Equal([null, null], frame.Adapters.Select(static a => a.Slot).ToArray());
        Assert.All(frame.Adapters, static a => Assert.True(a.Ambiguous));
        Assert.Empty(frame.EarningKeys);
        Assert.False(frame.SlotsChanged);

        // a second round trip changes nothing
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public void The_disputed_twins_stay_ambiguous_whatever_the_order_of_the_adapters_and_their_numbers_are_never_handed_out()
    {
        var state = TwinsState(out _);
        var adapters = new[] { Gpu("t1", "T", 1024, 5u), Gpu("t2", "T", 1024, 5u), Gpu("c", "C", 2048, 4u), Gpu("n", "NEW", 4 * GiB, 9u), Gpu("m", "MORE", 4 * GiB, 10u) };

        foreach (var order in Permutations(adapters))
        {
            var r = new GpuSlotMap(state.GpuSlots).Assign(order);
            var byId = order.Select((g, i) => (g.Id, r.Adapters[i])).ToDictionary(static x => x.Id, static x => x.Item2);

            Assert.True(byId["t1"].Ambiguous);
            Assert.True(byId["t2"].Ambiguous);
            Assert.Null(byId["t1"].Slot);
            Assert.Null(byId["t2"].Slot);
            Assert.Equal(1, byId["c"].Slot);
            // 0, 1, 2 and 4 are taken (disputed, live, disputed, closed); the two new cards share 3 and 5, in the order given
            var fresh = order.Where(static g => g.Id is "n" or "m").Select(g => byId[g.Id].Slot).ToArray();
            Assert.Equal([3, 5], fresh);
        }
    }

    [Fact]
    public void One_card_that_fits_a_disputed_record_is_ambiguous_too_and_never_a_new_card()
    {
        var map = new GpuSlotMap([new GpuSlotRecord(0, "T", 1024, 5u, Disputed: true), new GpuSlotRecord(2, "T", 1024, 5u, Disputed: true)]);

        var r = map.Assign(
        [
            Gpu("t", "T", 1024, 5u), Gpu("b", "T", 0, null),    // the exact fit, and a card that could be the model with an unknown bus
            Gpu("n1", "N1", GiB, 1u), Gpu("n2", "N2", GiB, 2u), Gpu("n3", "N3", GiB, 3u), Gpu("n4", "N4", GiB, 4u),
        ]);

        Assert.Equal([null, null, 1, 3, 4, 5], Slots(r));
        Assert.True(r.Adapters[0].Ambiguous);
        Assert.True(r.Adapters[1].Ambiguous);
        Assert.False(r.Adapters[2].Ambiguous);
        Assert.Equal([true, false, true], r.Records.Where(static x => x.Slot is 0 or 1 or 2).Select(static x => x.Disputed).ToArray());
    }

    [Fact]
    public void A_card_that_shares_nothing_with_the_disputed_identity_is_a_new_card()
    {
        var map = new GpuSlotMap([new GpuSlotRecord(0, "T", 1024, 5u, Disputed: true), new GpuSlotRecord(2, "T", 1024, 5u, Disputed: true)]);

        var r = map.Assign([Gpu("o", "OTHER", 1024, 5u), Gpu("big", "T", 4096, 5u)]);

        Assert.Equal([1, 3], Slots(r));    // another name; and the same name with a different memory is another model
    }

    [Fact]
    public void ForgetSlot_turns_a_disputed_record_into_a_retired_one_and_ResetSlot_then_frees_it()
    {
        var state = TwinsState(out _);
        var map = new GpuSlotMap(state.GpuSlots);
        var twins = new[] { Gpu("t1", "T", 1024, 5u), Gpu("t2", "T", 1024, 5u) };

        Assert.False(map.ResetSlot(state, 0));    // disputed is not retired: nothing is removed by a reset alone
        Assert.Equal(7, state.Experience["gpu#0"]);

        Assert.True(map.ForgetSlot(state, 0));
        Assert.Equal(new GpuSlotRecord(0, "T", 1024, 5u, Retired: true), state.GpuSlots.Single(static r => r.Slot == 0));
        // the other twin's record is still disputed: the twins stay ambiguous
        Assert.All(map.Assign(twins).Adapters, static a => Assert.True(a.Ambiguous));

        Assert.True(map.ForgetSlot(state, 2));
        // both let go: the twins are new cards now, and take numbers nobody holds (0 and 2 are closed until reset, 1 and 4 hold progress)
        Assert.Equal([3, 5], Slots(map.Assign(twins)));

        Assert.True(map.ResetSlot(state, 0));
        Assert.True(map.ResetSlot(state, 2));
        Assert.False(state.Experience.ContainsKey("gpu#0"));
        Assert.False(state.Experience.ContainsKey("gpu#2"));
        Assert.DoesNotContain(state.GpuSlots, static r => r.Slot is 0 or 2);
    }

    [Fact]
    public void A_record_that_is_retired_and_disputed_counts_as_retired()
    {
        var map = new GpuSlotMap([new GpuSlotRecord(0, "T", 1024, 5u, Retired: true, Disputed: true)]);

        Assert.Equal(new GpuSlotRecord(0, "T", 1024, 5u, Retired: true), map.Records.Single());
        Assert.Equal([1], Slots(map.Assign([Gpu("t", "T", 1024, 5u)])));    // retired: the matching card is new
    }

    [Fact]
    public void A_live_record_is_saved_without_Disputed_and_a_disputed_one_with_it()
    {
        var state = new PartyState();
        state.Migrate();
        state.GpuSlots = [Rec(0, "A", GiB, 1u), new GpuSlotRecord(1, "T", GiB, 2u, Disputed: true)];

        var json = state.ToJson();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "Disputed"));
        Assert.DoesNotContain("Retired", json);
        Assert.Equal(state.GpuSlots, PartyState.FromJson(json).GpuSlots);
    }

    [Fact]
    public void The_twin_duplicate_is_reported_by_the_importer_and_stays_disputed_in_the_converted_state()
    {
        var verdict = StateImport.Inspect(Encoding.UTF8.GetBytes("{" + TwinsProgress + ",\"GpuSlots\":" + TwinsList + "}"));

        Assert.Equal(ImportStatus.NeedsRepair, verdict.Status);
        Assert.Contains(verdict.RepairedFields, static f => f.StartsWith(nameof(PartyState.GpuSlots), StringComparison.Ordinal));
        var converted = PartyState.FromJson(Encoding.UTF8.GetString(verdict.Converted!));
        Assert.Equal([0, 2], converted.GpuSlots.Where(static r => r.Disputed).Select(static r => r.Slot).ToArray());
    }

    // ---- item 4: the normal save keeps its old shape ----

    [Fact]
    public void A_normal_save_has_no_GpuSlotsRepaired_text_and_an_old_save_without_it_loads_as_false()
    {
        var state = new PartyState();
        state.Migrate();
        state.GpuSlots = [Rec(0, "A", GiB, 1u)];

        Assert.DoesNotContain("GpuSlotsRepaired", state.ToJson());
        Assert.False(PartyState.FromJson(state.ToJson()).GpuSlotsRepaired);
        Assert.False(PartyState.FromJson("""{"SaveVersion":1,"Experience":{}}""").GpuSlotsRepaired);
    }

    [Fact]
    public void A_repaired_save_writes_the_sign_and_reads_it_back_even_when_another_field_is_read_one_by_one()
    {
        var state = new PartyState { GpuSlotsRepaired = true };
        state.Migrate();
        var json = state.ToJson();
        Assert.Contains("\"GpuSlotsRepaired\": true", json);
        Assert.True(PartyState.FromJson(json).GpuSlotsRepaired);

        // a save whose Adventure cannot be read as a whole goes through the field-by-field reader: the sign must survive that too
        var lenient = PartyState.FromJson("""{"SaveVersion":1,"GpuSlotsRepaired":true,"Adventure":"oops","Experience":{}}""", out var repaired);
        Assert.True(repaired);
        Assert.True(lenient.GpuSlotsRepaired);
    }

    [Fact]
    public void The_importer_converts_a_normal_state_without_the_sign()
    {
        var verdict = StateImport.Inspect(Encoding.UTF8.GetBytes("""{"SaveVersion":1,"Journey":1,"Chapter":0,"Scene":0,"Experience":{"cpu#0":10}}"""));

        Assert.Equal(ImportStatus.Acceptable, verdict.Status);
        Assert.DoesNotContain("GpuSlotsRepaired", Encoding.UTF8.GetString(verdict.Converted!));
    }

    // ---- codex review of 7155917..d6a6d40, item 1: an exact match with unknown memory must not beat a disputed record ----

    // slot 0: a live record with unknown memory; slot 1: a disputed record of the same model and bus with the memory known; slot 2: another card of the same
    // model on a different known bus, with its own exact record
    private static GpuSlotRecord[] UnknownMemoryNextToDisputed() =>
        [Rec(0, "N", 0, 5u), new GpuSlotRecord(1, "N", GiB, 5u, Disputed: true), Rec(2, "N", GiB, 9u)];

    private static PartyState SavedAndLoaded(GpuSlotRecord[] records, out bool repaired)
    {
        var state = new PartyState();
        state.Migrate();
        state.GpuSlots = [.. records];
        return PartyState.FromJson(state.ToJson(), out repaired);
    }

    [Fact]
    public void A_card_with_unknown_memory_that_fits_a_live_record_exactly_and_a_disputed_one_too_is_ambiguous_and_the_live_slot_is_taken_by_no_one()
    {
        var back = SavedAndLoaded(UnknownMemoryNextToDisputed(), out var repaired);
        Assert.False(repaired);
        Assert.Equal(UnknownMemoryNextToDisputed(), back.GpuSlots);

        foreach (var records in new GpuSlotRecord[][] { UnknownMemoryNextToDisputed(), [.. back.GpuSlots] })
        {
            var r = new GpuSlotMap(records).Assign([Gpu("x", "N", 0, 5u), Gpu("q", "Q", GiB, 1u)]);

            Assert.Null(r.Adapters[0].Slot);
            Assert.True(r.Adapters[0].Ambiguous);
            Assert.Equal(3, r.Adapters[1].Slot);                                    // a new card: not slot 0, which is blocked, and not 1 or 2
            Assert.Equal(Rec(0, "N", 0, 5u), r.Records.Single(static x => x.Slot == 0));
            Assert.True(r.Records.Single(static x => x.Slot == 1).Disputed);
        }

        // the same through the frame: it earns nothing, and the record is not rewritten to the card
        var state = SavedAndLoaded(UnknownMemoryNextToDisputed(), out _);
        var frame = new GpuSlotMap(state.GpuSlots).ApplyTo(state, Frame(Gpu("x", "N", 0, 5u)));
        Assert.Empty(frame.EarningKeys);
        Assert.Equal(Rec(0, "N", 0, 5u), state.GpuSlots.Single(static x => x.Slot == 0));
    }

    [Fact]
    public void A_third_card_of_the_same_model_on_another_known_bus_still_gets_its_own_slot_whatever_the_order()
    {
        var back = SavedAndLoaded(UnknownMemoryNextToDisputed(), out _);
        var adapters = new[] { Gpu("x", "N", 0, 5u), Gpu("y", "N", GiB, 9u), Gpu("q", "Q", GiB, 1u) };

        foreach (var records in new GpuSlotRecord[][] { UnknownMemoryNextToDisputed(), [.. back.GpuSlots] })
        {
            foreach (var order in Permutations(adapters))
            {
                var r = new GpuSlotMap(records).Assign(order);
                var byId = order.Select((g, i) => (g.Id, r.Adapters[i])).ToDictionary(static x => x.Id, static x => x.Item2);

                Assert.Null(byId["x"].Slot);
                Assert.True(byId["x"].Ambiguous);
                Assert.Equal(2, byId["y"].Slot);
                Assert.False(byId["y"].Ambiguous);
                Assert.Equal(3, byId["q"].Slot);
            }
        }
    }

    [Fact]
    public void A_card_on_a_known_bus_that_differs_from_the_disputed_one_does_not_fit_it_and_goes_through_the_tiers_as_usual()
    {
        var map = new GpuSlotMap([new GpuSlotRecord(0, "N", GiB, 5u, Disputed: true), Rec(1, "N", GiB, 9u)]);

        var r = map.Assign([Gpu("y", "N", GiB, 9u), Gpu("z", "N", GiB, 7u)]);

        Assert.Equal(1, r.Adapters[0].Slot);    // the exact fit is its own, not blocked by the disputed record of another bus
        Assert.False(r.Adapters[0].Ambiguous);
        Assert.Null(r.Adapters[1].Slot);        // nothing exact; its only candidate is the disputed record, which is never taken
        Assert.True(r.Adapters[1].Ambiguous);
    }

    // ---- item 2: a discarded row still says whose twin it may be ----

    private const string NoProgress = "\"SaveVersion\":1,\"Experience\":{\"cpu#0\":1}";

    [Theory]
    [InlineData("{\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5}")]                        // no Slot: read as slot 0, then discarded
    [InlineData("{\"Slot\":\"x\",\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5}")]         // not a number: the reader drops it, the text still says who it was
    public void A_valid_record_with_a_twin_row_that_has_no_Slot_is_disputed_and_a_matching_card_is_ambiguous(string twin)
    {
        var state = PartyState.FromJson("{" + NoProgress + ",\"GpuSlots\":[{\"Slot\":1,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5}," + twin + "]}", out var repaired);

        Assert.True(repaired);
        Assert.True(state.GpuSlotsRepaired);
        Assert.Equal([new GpuSlotRecord(1, "T", 1024, 5u, Disputed: true)], state.GpuSlots);

        var frame = new GpuSlotMap(state.GpuSlots).ApplyTo(state, Frame(Gpu("t", "T", 1024, 5u)));
        Assert.Equal([null], frame.Adapters.Select(static a => a.Slot).ToArray());
        Assert.True(frame.Adapters[0].Ambiguous);
        Assert.Empty(frame.EarningKeys);

        // the repaired list is not repaired again
        var again = PartyState.FromJson(state.ToJson(), out var repairedAgain);
        Assert.False(repairedAgain);
        Assert.Equal(state.GpuSlots, again.GpuSlots);
    }

    [Fact]
    public void A_valid_record_next_to_two_rows_that_share_another_slot_is_disputed_when_one_of_them_has_its_identity()
    {
        var state = PartyState.FromJson(
            "{" + NoProgress + ",\"GpuSlots\":[{\"Slot\":0,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5},"
            + "{\"Slot\":3,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5},{\"Slot\":3,\"Name\":\"U\",\"VramBytes\":2048,\"PciBus\":6}]}", out var repaired);

        Assert.True(repaired);
        Assert.Equal([new GpuSlotRecord(0, "T", 1024, 5u, Disputed: true)], state.GpuSlots);    // slot 3 is trusted for neither row

        var frame = new GpuSlotMap(state.GpuSlots).ApplyTo(state, Frame(Gpu("t", "T", 1024, 5u)));
        Assert.True(frame.Adapters[0].Ambiguous);
        Assert.Empty(frame.EarningKeys);
    }

    [Fact]
    public void A_discarded_row_with_another_identity_disputes_nothing_and_neither_does_a_row_that_is_null_or_out_of_range()
    {
        const string Valid = "{\"Slot\":0,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5}";
        var keep = new[] { Rec(0, "T", 1024, 5u) };

        var noSlot = PartyState.FromJson("{" + NoProgress + ",\"GpuSlots\":[" + Valid + ",{\"Name\":\"U\",\"VramBytes\":1024,\"PciBus\":5}]}", out var r1);
        var twice = PartyState.FromJson(
            "{" + NoProgress + ",\"GpuSlots\":[" + Valid + ",{\"Slot\":2,\"Name\":\"U\",\"VramBytes\":1,\"PciBus\":5},{\"Slot\":2,\"Name\":\"T\",\"VramBytes\":2048,\"PciBus\":5}]}", out var r2);
        var outOfRange = PartyState.FromJson(
            "{" + NoProgress + ",\"GpuSlots\":[" + Valid + ",{\"Slot\":9,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5},null]}", out var r3);

        Assert.True(r1);
        Assert.True(r2);
        Assert.True(r3);
        Assert.Equal(keep, noSlot.GpuSlots);
        Assert.Equal(keep, twice.GpuSlots);       // same name and bus, but another memory: another card
        Assert.Equal(keep, outOfRange.GpuSlots);  // no usable identity
    }

    [Fact]
    public void A_retired_row_that_is_discarded_does_not_dispute_a_live_record()
    {
        var state = PartyState.FromJson(
            "{" + NoProgress + ",\"GpuSlots\":[{\"Slot\":0,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5},{\"Retired\":true,\"Name\":\"T\",\"VramBytes\":1024,\"PciBus\":5}]}", out var repaired);

        Assert.True(repaired);
        Assert.Equal([Rec(0, "T", 1024, 5u)], state.GpuSlots);
    }

    // ---- item 3: one row with no Slot does not discard a genuine slot 0 ----

    [Fact]
    public void A_row_with_no_Slot_next_to_a_genuine_slot_0_discards_only_that_row_and_the_save_is_still_reported_repaired()
    {
        var back = PartyState.FromJson(
            "{" + Progress0 + ",\"GpuSlots\":[{\"Name\":\"B\",\"VramBytes\":2048,\"PciBus\":2},{\"Slot\":0,\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}]}", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Equal([Rec(0, "A", 1024, 1u)], back.GpuSlots);    // the genuine slot 0 stays, and holds its progress

        var r = new GpuSlotMap(back.GpuSlots).ApplyTo(back, Frame(Gpu("a", "A", 1024, 1u)));
        Assert.Equal(["gpu#0"], r.EarningKeys);
        Assert.Equal(7, back.Experience["gpu#0"]);
    }

    [Fact]
    public void A_row_with_no_Slot_in_the_middle_of_several_discards_only_that_row()
    {
        var back = PartyState.FromJson(
            "{" + NoProgress + ",\"GpuSlots\":[{\"Slot\":2,\"Name\":\"C\",\"VramBytes\":3,\"PciBus\":3},{\"Slot\":0,\"Name\":\"A\",\"VramBytes\":1,\"PciBus\":1},"
            + "{\"Name\":\"B\",\"VramBytes\":2,\"PciBus\":2},{\"Slot\":1,\"Name\":\"D\",\"VramBytes\":4,\"PciBus\":4}]}", out var repaired);

        Assert.True(repaired);
        Assert.Equal([Rec(0, "A", 1, 1u), Rec(1, "D", 4, 4u), Rec(2, "C", 3, 3u)], back.GpuSlots);
    }

    [Fact]
    public void When_the_reader_dropped_a_row_the_rows_no_longer_line_up_and_slot_0_stays_doubtful()
    {
        // "Slot":"x" makes the reader drop that row: the list has fewer rows than the text, so which row is which is unknown
        var back = PartyState.FromJson(
            "{" + Progress0 + ",\"GpuSlots\":[{\"Slot\":\"x\",\"Name\":\"B\",\"VramBytes\":2048,\"PciBus\":2},{\"Slot\":0,\"Name\":\"A\",\"VramBytes\":1024,\"PciBus\":1}]}", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Equal([Closed(0)], back.GpuSlots);
        Assert.Equal(7, back.Experience["gpu#0"]);
    }
}
