using System.Text;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// DESIGN-C 2.3 / 4.2 / 9: a number the owner let go stays closed until it is reset; a damaged GpuSlots field never turns into "no slots" while progress
/// is stored under gpu#i; and only a card that is settled and has a reading earns.
/// </summary>
public class GpuSlotRetireTests
{
    private const long GiB = 1L << 30;

    private static BoardGpu Gpu(string id, string name, long vram, uint? bus, double? percent = 10) => new(id, name, percent, 1, vram, bus);

    private static GpuSlotRecord Rec(int slot, string name, long vram, uint? bus) => new(slot, name, vram, bus);

    private static BoardFrame Frame(params BoardGpu[]? gpus) =>
        new(8, 10, null, gpus, null, new BoardDisk(null, false), new BoardNet(null, null, false)) { AllGpus = gpus };

    private static int?[] Slots(GpuSlotResult r) => r.Adapters.Select(static a => a.Slot).ToArray();

    private static PartyState StateWithProgress(params int[] slots)
    {
        var state = new PartyState();
        state.Migrate();
        foreach (var s in slots)
        {
            state.Experience[GpuSlotKeys.KeyOf(s)] = 100 + s;
            state.Adventure!.Equipped[GpuSlotKeys.KeyOf(s)] = ["sword", ""];
        }

        return state;
    }

    // ---- item 1: a forgotten number stays closed ----

    [Fact]
    public void A_forgotten_number_is_never_given_to_a_new_card_after_a_restart()
    {
        var state = StateWithProgress(0, 1);
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u)]);
        Assert.True(map.ForgetSlot(state, 1));

        var restarted = PartyState.FromJson(state.ToJson(), out var repaired);
        var after = new GpuSlotMap(restarted.GpuSlots).Assign([Gpu("a", "A", 8 * GiB, 1u), Gpu("n", "NEW", 4 * GiB, 9u)]);

        Assert.False(repaired);
        Assert.False(restarted.GpuSlotsRepaired);
        Assert.Equal([0, 2], Slots(after));    // the new card gets 2, never the closed 1
        Assert.Equal(101, restarted.Experience["gpu#1"]);    // and the progress under 1 is still there
    }

    [Fact]
    public void The_same_card_plugged_in_again_after_Forget_and_a_restart_is_a_new_card_too()
    {
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u)]);
        map.Forget(0);

        var again = new GpuSlotMap(map.Records).Assign([Gpu("a", "A", 8 * GiB, 1u)]);

        Assert.Equal([1], Slots(again));
    }

    [Fact]
    public void ResetSlot_removes_the_progress_the_gear_and_the_record_and_the_number_goes_to_a_new_card_which_starts_from_nothing()
    {
        var state = StateWithProgress(0, 1);
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u)]);
        map.ForgetSlot(state, 1);

        Assert.True(map.ResetSlot(state, 1));

        Assert.False(state.Experience.ContainsKey("gpu#1"));
        Assert.False(state.Adventure!.Equipped.ContainsKey("gpu#1"));
        Assert.Equal([0], state.GpuSlots.Select(static r => r.Slot).ToArray());
        Assert.True(state.Experience.ContainsKey("gpu#0"));    // the others are untouched
        Assert.True(state.Adventure.Equipped.ContainsKey("gpu#0"));

        var frame = map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u), Gpu("n", "NEW", 4 * GiB, 9u)));
        Assert.Equal([0, 1], frame.Adapters.Select(static a => a.Slot).ToArray());    // 1 is free again
        Assert.Equal(["gpu#0", "gpu#1"], frame.EarningKeys);
        Assert.False(state.Experience.ContainsKey("gpu#1"));
    }

    [Fact]
    public void ResetSlot_refuses_a_slot_that_is_not_retired_and_changes_nothing()
    {
        var state = StateWithProgress(0);
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u)]);
        map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u)));
        var records = state.GpuSlots.ToArray();

        Assert.False(map.ResetSlot(state, 0));    // live
        Assert.False(map.ResetSlot(state, 3));    // no record
        Assert.False(map.ResetSlot(state, -1));
        Assert.False(map.ResetSlot(state, 6));

        Assert.Equal(records, state.GpuSlots);
        Assert.Equal(100, state.Experience["gpu#0"]);
        Assert.Equal(["sword", ""], state.Adventure!.Equipped["gpu#0"]);
        Assert.Equal(records, map.Records);
    }

    [Fact]
    public void ForgetSlot_writes_the_retired_record_into_the_state_at_once_and_a_second_call_changes_nothing()
    {
        var state = StateWithProgress(0);
        var map = new GpuSlotMap([Rec(0, "A", 8 * GiB, 1u), Rec(1, "B", 8 * GiB, 2u)]);
        state.GpuSlots = [.. map.Records];

        Assert.True(map.ForgetSlot(state, 0));
        Assert.False(map.ForgetSlot(state, 0));
        Assert.False(map.ForgetSlot(state, 5));

        Assert.Equal(Rec(0, "A", 8 * GiB, 1u) with { Retired = true }, state.GpuSlots[0]);
        Assert.False(state.GpuSlots[1].Retired);
        Assert.Equal(100, state.Experience["gpu#0"]);
    }

    [Fact]
    public void A_record_saved_before_the_Retired_field_existed_loads_as_not_retired_and_a_live_record_is_saved_without_it()
    {
        const string old =
            """{"SaveVersion":1,"Experience":{},"GpuSlots":[{"Slot":0,"Name":"A","VramBytes":1024,"PciBus":5}]}""";

        var back = PartyState.FromJson(old, out var repaired);

        Assert.False(repaired);
        Assert.False(back.GpuSlotsRepaired);
        Assert.False(back.GpuSlots[0].Retired);
        Assert.DoesNotContain("Retired", back.ToJson());
        back.GpuSlots[0] = back.GpuSlots[0] with { Retired = true };
        Assert.Contains("Retired", back.ToJson());    // a retired one is written, and reads back as retired
        Assert.True(PartyState.FromJson(back.ToJson()).GpuSlots[0].Retired);
    }

    // ---- item 2: absent is not broken ----

    [Fact]
    public void An_absent_field_is_an_old_save_so_the_first_run_numbers_the_cards_as_before()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7,"gpu#2":5}}""", out var repaired);

        Assert.False(repaired);
        Assert.False(back.GpuSlotsRepaired);
        Assert.Empty(back.GpuSlots);
        Assert.Equal([0, 1], Slots(new GpuSlotMap(back.GpuSlots).Assign([Gpu("a", "A", 8 * GiB, 1u), Gpu("b", "B", 8 * GiB, 2u)])));
    }

    [Fact]
    public void A_present_empty_list_is_well_formed_and_is_not_reported()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"cpu#0":7},"GpuSlots":[]}""", out var repaired);

        Assert.False(repaired);
        Assert.False(back.GpuSlotsRepaired);
        Assert.Empty(back.GpuSlots);    // no gpu#i holds progress: nothing to reserve
    }

    [Fact]
    public void A_present_empty_list_with_progress_is_not_reported_but_reserves_the_number_that_holds_it()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"gpu#0":7},"GpuSlots":[]}""", out var repaired);

        Assert.False(repaired);
        Assert.False(back.GpuSlotsRepaired);
        Assert.Equal([new GpuSlotRecord(0, "", 0, null, Retired: true)], back.GpuSlots);
        Assert.Equal(7, back.Experience["gpu#0"]);
    }

    // ---- item 2: present but broken keeps the progress reserved ----

    private const string Progress =
        "\"SaveVersion\":1,\"Journey\":1,\"Chapter\":0,\"Scene\":0,\"Experience\":{\"gpu#0\":7,\"gpu#2\":5,\"gpu#4\":0},\"Adventure\":{\"Equipped\":{\"gpu#3\":[\"sword\",\"\"],\"gpu#5\":[\"\",\"\"]}}";

    [Theory]
    // null
    [InlineData("null", new int[0], new[] { 0, 2, 3 })]
    // not an array
    [InlineData("\"oops\"", new int[0], new[] { 0, 2, 3 })]
    [InlineData("{\"Slot\":1}", new int[0], new[] { 0, 2, 3 })]
    // an element that is null, and one of the wrong kind
    [InlineData("[null,{\"Slot\":1,\"Name\":\"C\",\"VramBytes\":1,\"PciBus\":4}]", new[] { 1 }, new[] { 0, 2, 3 })]
    [InlineData("[7,{\"Slot\":1,\"Name\":\"C\",\"VramBytes\":1,\"PciBus\":4}]", new[] { 1 }, new[] { 0, 2, 3 })]
    // the same slot twice: neither of the two is trusted
    [InlineData("[{\"Slot\":0,\"Name\":\"A\",\"VramBytes\":1,\"PciBus\":1},{\"Slot\":0,\"Name\":\"B\",\"VramBytes\":1,\"PciBus\":2},{\"Slot\":1,\"Name\":\"C\",\"VramBytes\":1,\"PciBus\":4}]", new[] { 1 }, new[] { 0, 2, 3 })]
    // a negative slot (and one past the last)
    [InlineData("[{\"Slot\":-1,\"Name\":\"A\",\"VramBytes\":1,\"PciBus\":1},{\"Slot\":2,\"Name\":\"B\",\"VramBytes\":1,\"PciBus\":2}]", new[] { 2 }, new[] { 0, 3 })]
    [InlineData("[{\"Slot\":6,\"Name\":\"A\",\"VramBytes\":1,\"PciBus\":1},{\"Slot\":2,\"Name\":\"B\",\"VramBytes\":1,\"PciBus\":2}]", new[] { 2 }, new[] { 0, 3 })]
    // two live records of one exact identity are not here: they are kept as Disputed, not reserved as Retired (GpuSlotDisputedTests)
    public void A_broken_field_keeps_the_valid_records_reserves_every_number_that_holds_progress_and_never_drops_the_progress(
        string slotsJson, int[] keptSlots, int[] reserved)
    {
        var json = "{" + Progress + ",\"GpuSlots\":" + slotsJson + "}";

        var back = PartyState.FromJson(json, out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Equal(keptSlots.Concat(reserved).Order().ToArray(), back.GpuSlots.Select(static r => r.Slot).ToArray());
        Assert.All(back.GpuSlots.Where(r => reserved.Contains(r.Slot)), static r => Assert.True(r.Retired));
        Assert.All(back.GpuSlots.Where(r => keptSlots.Contains(r.Slot)), static r => Assert.False(r.Retired));

        // The progress is all still there.
        Assert.Equal(7, back.Experience["gpu#0"]);
        Assert.Equal(5, back.Experience["gpu#2"]);
        Assert.Equal(["sword", ""], back.Adventure!.Equipped["gpu#3"]);

        // No card inherits it: new cards (the old ones, now unknown, and a new one) all land on a number that holds nothing.
        var map = new GpuSlotMap(back.GpuSlots);
        var r = map.Assign([Gpu("x", "X", 8 * GiB, 21u), Gpu("y", "Y", 8 * GiB, 22u)]);
        Assert.All(r.Adapters, a => Assert.DoesNotContain(a.Slot ?? -1, new[] { 0, 2, 3 }));

        // The reserved numbers and the flag survive the next save and load, with nothing more to repair.
        var again = PartyState.FromJson(back.ToJson(), out var repairedAgain);
        Assert.False(repairedAgain);
        Assert.True(again.GpuSlotsRepaired);
        Assert.Equal(back.GpuSlots, again.GpuSlots);
    }

    [Fact]
    public void A_broken_field_with_no_progress_anywhere_reserves_nothing_but_is_still_reported()
    {
        var back = PartyState.FromJson("""{"SaveVersion":1,"Experience":{"cpu#0":5},"GpuSlots":"oops"}""", out var repaired);

        Assert.True(repaired);
        Assert.True(back.GpuSlotsRepaired);
        Assert.Empty(back.GpuSlots);
    }

    [Fact]
    public void Retired_placeholders_are_not_counted_as_a_duplicate_identity_and_a_repaired_list_stays_stable()
    {
        var back = PartyState.FromJson("{" + Progress + ",\"GpuSlots\":null}", out _);

        var map = new GpuSlotMap(back.GpuSlots);
        map.ApplyTo(back, Frame(Gpu("x", "X", 8 * GiB, 21u)));

        Assert.Equal([0, 1, 2, 3], back.GpuSlots.Select(static r => r.Slot).ToArray());    // the new card takes 1; the three placeholders stay
        Assert.Equal([true, false, true, true], back.GpuSlots.Select(static r => r.Retired).ToArray());
    }

    // ---- item 2: the Import verdict ----

    [Fact]
    public void The_importer_reports_a_broken_slot_list_as_repaired_and_lists_the_field()
    {
        var json = "{" + Progress + ",\"GpuSlots\":[{\"Slot\":0,\"Name\":\"A\",\"VramBytes\":1,\"PciBus\":1},{\"Slot\":0,\"Name\":\"B\",\"VramBytes\":1,\"PciBus\":2}]}";

        var verdict = StateImport.Inspect(Encoding.UTF8.GetBytes(json));

        Assert.Equal(ImportStatus.NeedsRepair, verdict.Status);
        Assert.Contains(nameof(PartyState.GpuSlots), verdict.RepairedFields);
        var converted = PartyState.FromJson(Encoding.UTF8.GetString(verdict.Converted!));
        Assert.Equal(7, converted.Experience["gpu#0"]);
        Assert.True(converted.GpuSlots.Single(static r => r.Slot == 0).Retired);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"oops\"")]
    [InlineData("[{\"Slot\":-1,\"Name\":\"A\",\"VramBytes\":1,\"PciBus\":1}]")]
    public void The_importer_reports_every_kind_of_broken_slot_list(string slotsJson)
    {
        var verdict = StateImport.Inspect(Encoding.UTF8.GetBytes("{" + Progress + ",\"GpuSlots\":" + slotsJson + "}"));

        Assert.Equal(ImportStatus.NeedsRepair, verdict.Status);
        Assert.Contains(verdict.RepairedFields, static f => f.StartsWith(nameof(PartyState.GpuSlots), StringComparison.Ordinal));
    }

    [Fact]
    public void The_importer_still_accepts_a_source_without_the_field()
    {
        var verdict = StateImport.Inspect(Encoding.UTF8.GetBytes("{" + Progress + "}"));

        Assert.Equal(ImportStatus.Acceptable, verdict.Status);
        Assert.Empty(verdict.RepairedFields);
    }

    // ---- item 3: displaying is not earning ----

    [Fact]
    public void ApplyTo_shows_a_settled_adapter_with_no_reading_but_does_not_let_it_earn()
    {
        var state = StateWithProgress();
        var map = new GpuSlotMap(state.GpuSlots);

        var f = map.ApplyTo(state, Frame(Gpu("a", "A", 8 * GiB, 1u, percent: null), Gpu("b", "B", 8 * GiB, 2u, percent: 40), Gpu("c", "C", 8 * GiB, 3u, percent: double.NaN)));

        Assert.Equal(["gpu#0", "gpu#1", "gpu#2"], f.Adapters.Select(static a => a.Key ?? "-").ToArray());
        Assert.Equal(["gpu#1"], f.EarningKeys);
    }

    [Fact]
    public void ApplyTo_shows_an_ambiguous_adapter_with_no_key_and_it_earns_nothing()
    {
        var state = StateWithProgress();
        state.GpuSlots = [Rec(0, "TWIN", 8 * GiB, null), Rec(1, "TWIN", 8 * GiB, null)];
        var map = new GpuSlotMap(state.GpuSlots);

        var f = map.ApplyTo(state, Frame(Gpu("t1", "TWIN", 8 * GiB, null), Gpu("t2", "TWIN", 8 * GiB, null), Gpu("o", "OTHER", 8 * GiB, 7u)));

        Assert.Equal(3, f.Adapters.Count);
        Assert.All(f.Adapters.Take(2), static a => { Assert.True(a.Ambiguous); Assert.Null(a.Key); });
        Assert.Equal(["gpu#2"], f.EarningKeys);
    }
}
