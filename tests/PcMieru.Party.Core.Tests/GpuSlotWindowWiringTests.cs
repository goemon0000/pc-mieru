using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 試験用の近道: frame に対して新しい席の地図を一度かけて、PartyComposition に渡す GPU の席を作る
/// （窓が毎サンプルやること: ApplyTo → Build / WorkOf → Tick の、最初の 2 段）。
/// </summary>
internal static class Comp
{
    public static GpuSlotFrame GpuFrame(BoardFrame frame) => new GpuSlotMap().ApplyTo(new PartyState(), frame);

    public static List<PartySlot> Build(BoardFrame frame, string lang) => PartyComposition.Build(frame, GpuFrame(frame), lang);

    public static Work WorkOf(BoardFrame frame) => PartyComposition.WorkOf(frame, GpuFrame(frame));
}

/// <summary>
/// DESIGN-C 8 step 4b（窓の側）: 席の鍵で GPU を立てる・席の無い GPU は絵だけで保存に何も作らない・番号を空けるときは装備を持ち物へ返す。
/// </summary>
public sealed class GpuSlotWindowWiringTests
{
    private const long GiB = 1L << 30;

    private static BoardGpu Gpu(string id, string name, long vram, uint? bus, double? percent) => new(id, name, percent, 1, vram, bus);

    private static GpuSlotRecord Rec(int slot, string name, long vram, uint? bus, bool retired = false) => new(slot, name, vram, bus, retired);

    private static BoardFrame Frame(params BoardGpu[] gpus) =>
        new(8, 40, null, gpus, null, new BoardDisk(null, false), new BoardNet(null, null, false)) { AllGpus = gpus };

    private static PartyState FreshState()
    {
        var state = new PartyState();
        state.Migrate();
        return state;
    }

    private static PartyState Reload(PartyState state, out bool repaired) => PartyState.FromJson(state.ToJson(), out repaired);

    // ---- keys come from the slot, not from the index ----

    [Fact]
    public void Gpus_are_keyed_by_their_slot_even_when_the_list_order_changes()
    {
        var map = new GpuSlotMap([Rec(0, "Card A", 8 * GiB, 1), Rec(1, "Card B", 12 * GiB, 2)]);
        var frame = Frame(Gpu("b", "Card B", 12 * GiB, 2, 30), Gpu("a", "Card A", 8 * GiB, 1, 70));

        var gpuSlots = map.ApplyTo(FreshState(), frame);
        var gpus = PartyComposition.Build(frame, gpuSlots, "ja").Where(s => s.CharacterId == "gpu").ToList();

        Assert.Equal(new[] { "gpu#1", "gpu#0" }, gpus.Select(s => s.Key));
        Assert.Equal(new[] { "GPU1", "GPU0" }, gpus.Select(s => s.Title));
        Assert.All(gpus, s => Assert.True(s.Measured));
        Assert.Equal(0.3, gpus[0].Load, 6);
    }

    [Fact]
    public void An_unkeyed_gpu_is_drawn_with_a_display_only_key_and_is_not_measured()
    {
        var frame = Frame(Gpu("x", "Twin", 8 * GiB, null, 90), Gpu("y", "Twin", 8 * GiB, null, 80));
        var gpuSlots = new GpuSlotFrame(
            [new GpuSlotAssignment("x", null, true), new GpuSlotAssignment("y", 0, false)],
            ["gpu#0"], false);

        var gpus = PartyComposition.Build(frame, gpuSlots, "ja").Where(s => s.CharacterId == "gpu").ToList();

        Assert.Equal(new[] { GpuSlotKeys.DisplayKeyOf(0), "gpu#0" }, gpus.Select(s => s.Key));
        Assert.True(GpuSlotKeys.IsDisplayOnly(gpus[0].Key));
        Assert.False(gpus[0].Measured);
        Assert.Equal(0, gpus[0].Load);
        Assert.True(gpus[1].Measured);
        // The work of the story counts only the GPU that may earn: 80, not the unkeyed 90.
        Assert.Equal(0.8, PartyComposition.WorkOf(frame, gpuSlots).Gpu, 6);
    }

    [Fact]
    public void A_keyed_gpu_that_is_not_in_the_earning_keys_is_not_measured()
    {
        var frame = Frame(Gpu("a", "Card A", 8 * GiB, 1, 50));
        var gpuSlots = new GpuSlotFrame([new GpuSlotAssignment("a", 0, false)], [], false);

        var gpu = PartyComposition.Build(frame, gpuSlots, "ja").Single(s => s.CharacterId == "gpu");

        Assert.Equal("gpu#0", gpu.Key);
        Assert.False(gpu.Measured);
        Assert.Equal(PartyComposition.Missing, gpu.Detail);
        Assert.False(PartyComposition.WorkOf(frame, gpuSlots).Has(WorkChannels.Gpu));
    }

    [Fact]
    public void Director_and_the_adventure_write_nothing_for_an_unkeyed_slot()
    {
        var state = FreshState();
        var director = new Director(TestData.Story, state, TestData.Book, new Random(1), () => 1_800_000_000);
        var frame = Frame(Gpu("x", "Twin", 8 * GiB, null, 95), Gpu("y", "Card", 8 * GiB, 1, 60));
        var gpuSlots = new GpuSlotFrame(
            [new GpuSlotAssignment("x", null, true), new GpuSlotAssignment("y", 0, false)],
            ["gpu#0"], false);
        var slots = PartyComposition.Build(frame, gpuSlots, "ja");
        var work = PartyComposition.WorkOf(frame, gpuSlots);

        // Long enough for the experience, the starter gear and the stray-gear sweep (every 30 s) to all run.
        for (var t = 0; t < 1500; t++)
        {
            director.Tick(1, work, slots, "ja");
        }

        Assert.True(state.Experience.GetValueOrDefault("gpu#0") > 0);
        Assert.DoesNotContain(state.Experience.Keys, GpuSlotKeys.IsDisplayOnly);
        Assert.DoesNotContain(state.Adventure!.Equipped.Keys, GpuSlotKeys.IsDisplayOnly);
        Assert.DoesNotContain("gpu~", state.ToJson());
        Assert.Contains("gpu#0", state.Adventure.Equipped.Keys);
    }

    // ---- reset: the gear goes back to the bag ----

    [Fact]
    public void ReturnGearOf_puts_the_gear_in_the_bag_and_removes_the_entry()
    {
        var state = FreshState();
        state.Adventure!.Equipped["gpu#2"] = ["old-label", "thin-sheet", "cable-tie"];
        state.Adventure.Equipped["gpu#0"] = ["rubber-foot", "", ""];
        var engine = new AdventureEngine(TestData.Book, state, new Random(1));

        Assert.True(engine.ReturnGearOf("gpu#2"));

        Assert.False(state.Adventure.Equipped.ContainsKey("gpu#2"));
        Assert.Equal(new[] { "cable-tie", "old-label", "thin-sheet" }, state.Adventure.Inventory.OrderBy(x => x));
        Assert.Equal(new[] { "rubber-foot", "", "" }, state.Adventure.Equipped["gpu#0"]);
        Assert.False(engine.ReturnGearOf("gpu#2"));   // nothing left to return
    }

    [Fact]
    public void ReturnGearOf_sells_what_does_not_fit_in_the_bag()
    {
        var state = FreshState();
        for (var i = 0; i < AdventureEngine.InventoryCap - 1; i++) state.Adventure!.Inventory.Add("rubber-foot");
        state.Adventure!.Equipped["gpu#2"] = ["old-label", "thin-sheet", "cable-tie"];
        var engine = new AdventureEngine(TestData.Book, state, new Random(1));
        var lootBefore = state.Adventure.Loot;
        var oneFits = new[] { "old-label", "thin-sheet", "cable-tie" }.Select(id => TestData.Book.Item(id)!.SellPrice).ToArray();

        engine.ReturnGearOf("gpu#2");

        Assert.Equal(AdventureEngine.InventoryCap, state.Adventure.Inventory.Count);
        Assert.Equal("old-label", state.Adventure.Inventory[^1]);   // the first one took the last free place
        Assert.Equal(lootBefore + oneFits[1] + oneFits[2], state.Adventure.Loot);
        Assert.False(state.Adventure.Equipped.ContainsKey("gpu#2"));
    }

    [Fact]
    public void After_a_reset_a_new_card_on_that_number_starts_from_nothing()
    {
        var state = FreshState();
        state.Experience["gpu#1"] = 5000;
        state.Adventure!.Equipped["gpu#1"] = ["old-label", "", ""];
        var map = new GpuSlotMap([Rec(0, "Card A", 8 * GiB, 1), Rec(1, "Card B", 12 * GiB, 2)]);
        var engine = new AdventureEngine(TestData.Book, state, new Random(1));

        Assert.False(map.ResetSlotKeepingGear(state, engine, 1));   // still in use: refused, gear untouched
        Assert.Contains("gpu#1", state.Adventure.Equipped.Keys);

        Assert.True(map.ForgetSlot(state, 1));
        Assert.True(map.ResetSlotKeepingGear(state, engine, 1));

        Assert.False(state.Experience.ContainsKey("gpu#1"));
        Assert.False(state.Adventure.Equipped.ContainsKey("gpu#1"));
        Assert.Contains("old-label", state.Adventure.Inventory);
        Assert.DoesNotContain(state.GpuSlots, r => r.Slot == 1);

        var frame = Frame(Gpu("a", "Card A", 8 * GiB, 1, 20), Gpu("c", "Card C", 16 * GiB, 3, 20));
        var result = map.ApplyTo(state, frame);

        Assert.Equal(new[] { "gpu#0", "gpu#1" }, result.Adapters.Select(static a => a.Key));
        Assert.Equal(0, state.Experience.GetValueOrDefault("gpu#1"));
        Assert.False(state.Adventure.Equipped.ContainsKey("gpu#1"));
        Assert.Equal(1, state.Level("gpu#1"));
    }

    [Fact]
    public void Forget_then_reset_survives_a_save_and_a_load()
    {
        var state = FreshState();
        state.Experience["gpu#0"] = 300;
        state.Experience["gpu#1"] = 700;
        state.Adventure!.Equipped["gpu#1"] = ["thin-sheet", "", ""];
        state.GpuSlots = [Rec(0, "Card A", 8 * GiB, 1), Rec(1, "Card B", 12 * GiB, 2)];
        var map = new GpuSlotMap(state.GpuSlots);

        Assert.True(map.ForgetSlot(state, 1));
        var afterForget = Reload(state, out var repaired1);
        Assert.False(repaired1);
        Assert.True(afterForget.GpuSlots.Single(r => r.Slot == 1).Retired);
        Assert.Equal(700, afterForget.Experience["gpu#1"]);

        var reloadedMap = new GpuSlotMap(afterForget.GpuSlots);
        var engine = new AdventureEngine(TestData.Book, afterForget, new Random(1));
        Assert.True(reloadedMap.ResetSlotKeepingGear(afterForget, engine, 1));
        var afterReset = Reload(afterForget, out var repaired2);

        Assert.False(repaired2);
        Assert.DoesNotContain(afterReset.GpuSlots, r => r.Slot == 1);
        Assert.False(afterReset.Experience.ContainsKey("gpu#1"));
        Assert.False(afterReset.Adventure!.Equipped.ContainsKey("gpu#1"));
        Assert.Contains("thin-sheet", afterReset.Adventure.Inventory);
        Assert.Equal(300, afterReset.Experience["gpu#0"]);
        Assert.Equal(new[] { 0 }, afterReset.GpuSlots.Select(static r => r.Slot));
    }
}
