using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 盤に居ない者（幻の gpu#3）の装備。**冒険は盤と同じ顔ぶれで動き、残った鍵は装備を失わせずに片付ける。**
/// </summary>
public class StrayGearTests
{
    private static readonly AdventureBook Book = TestData.Book;

    private static PartySlot Slot(string key, string id) => new(key, id, SlotKind.Pcie, key, "", 0.2);

    /// <summary>開発機の盤（GPU 3 枚）。</summary>
    private static readonly PartySlot[] Board =
    {
        Slot("cpu#0", "cpu"), Slot("cpu-cooler#0", "cpu-cooler"), Slot("memory", "memory"),
        Slot("gpu#0", "gpu"), Slot("gpu#1", "gpu"), Slot("gpu#2", "gpu"),
        Slot("m2", "m2"), Slot("ssd", "ssd"), Slot("hdd", "hdd"), Slot("psu", "psu"),
    };

    private static readonly WorldState World = new(new Dictionary<string, int>(), 3, 3, 1,
        new HashSet<string> { "cpu", "cpu-cooler", "memory", "gpu", "m2", "ssd", "hdd", "psu" }, 1, 1);

    /// <summary>v0.7.0 の試運転の後に残っていた保存（state.json.demo-after）の装備の形。</summary>
    private static PartyState DemoAfter() => PartyState.FromJson("""
        {
          "SaveVersion": 1,
          "Adventure": {
            "Mode": "Town", "Phase": "Depart", "Town": 1, "Place": "steam-creek", "Gold": 41, "Inventory": [],
            "Equipped": {
              "cpu#0": ["spare-screw", "thin-sheet", "rubber-foot"],
              "cpu-cooler#0": ["old-label", "thin-sheet", "rubber-foot"],
              "gpu#0": ["glowing-stone", "thin-sheet", "rubber-foot"],
              "gpu#1": ["glowing-chip", "thin-sheet", "rubber-foot"],
              "gpu#2": ["glowing-chip", "thin-sheet", "rubber-foot"],
              "memory": ["old-label", "thin-sheet", "cable-tie"],
              "psu": ["old-label", "thin-sheet", "rubber-foot"],
              "ssd": ["old-label", "thin-sheet", "rubber-foot"],
              "hdd": ["old-label", "thin-sheet", "rubber-foot"],
              "m2": ["old-label", "thin-sheet", "rubber-foot"],
              "gpu#3": ["glowing-chip", "thin-sheet", "rubber-foot"]
            }
          },
          "Experience": { "gpu#0": 1108, "gpu#1": 4688, "gpu#2": 469, "gpu#3": 60 },
          "Journey": 1421.6, "Chapter": 3, "Scene": 7
        }
        """);

    private static void Run(AdventureEngine e, IReadOnlyList<PartySlot> slots, int seconds)
    {
        var output = new List<string>();
        var w = new Work(0.1, 0.1, 0.4, 0, 0.5);
        // 旅程 0 で回す: 場面は進まない（町の着け替えで装備が動かない）。片付けだけを見る
        for (int t = 0; t < seconds; t++) e.Tick(1.0, 0, w, slots, World, output, "ja");
    }

    private static Dictionary<string, string[]> Copy(AdventureState s) =>
        s.Equipped.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

    [Fact]
    public void A_phantom_gpu_slot_is_cleared_after_the_grace_period_and_its_gear_goes_back_to_the_bag()
    {
        var state = DemoAfter();
        var before = Copy(state.Adventure!);
        var e = new AdventureEngine(Book, state, new Random(1));

        // 揺れでは外さない
        Run(e, Board, (int)AdventureEngine.StrayGraceSeconds - 60);
        Assert.True(state.Adventure!.Equipped.ContainsKey("gpu#3"));

        Run(e, Board, 120);
        var s = state.Adventure!;
        Assert.False(s.Equipped.ContainsKey("gpu#3"));
        // 冒険の顔ぶれは盤と同じ
        Assert.Equal(Board.Select(b => b.Key).OrderBy(k => k, StringComparer.Ordinal), e.Members.Select(m => m.Key));
        // 他の者の装備は一つも変わらない
        foreach (var (key, gear) in before)
            if (key != "gpu#3") Assert.Equal(gear, s.Equipped[key]);
        // 外した物は捨てずに持ち物へ
        Assert.Equal(before["gpu#3"].OrderBy(x => x), s.Inventory.OrderBy(x => x));
        // 経験値には触らない（レベルは仕事でしか動かない）
        Assert.Equal(60, state.Experience["gpu#3"]);
    }

    [Fact]
    public void Nothing_is_cleared_when_the_whole_kind_is_missing_or_the_key_has_no_number()
    {
        var state = DemoAfter();
        var before = Copy(state.Adventure!);
        var e = new AdventureEngine(Book, state, new Random(1));
        // 計測が止まって GPU が一枚も見えない・HDD が見えない機械
        var noGpus = Board.Where(b => b.CharacterId is not ("gpu" or "hdd")).ToArray();
        Run(e, noGpus, 3 * (int)AdventureEngine.StrayGraceSeconds);

        var s = state.Adventure!;
        foreach (var (key, gear) in before) Assert.Equal(gear, s.Equipped[key]);
        Assert.Empty(s.Inventory);
    }

    [Fact]
    public void A_slot_that_comes_back_before_the_grace_period_keeps_its_gear()
    {
        var state = DemoAfter();
        var e = new AdventureEngine(Book, state, new Random(1));
        var withPhantom = Board.Append(Slot("gpu#3", "gpu")).ToArray();
        for (int round = 0; round < 6; round++)
        {
            Run(e, Board, (int)AdventureEngine.StrayGraceSeconds / 2);
            Run(e, withPhantom, 30);
        }
        Assert.Equal(new[] { "glowing-chip", "thin-sheet", "rubber-foot" }, state.Adventure!.Equipped["gpu#3"]);
    }

    private static GpuSlotRecord Record(int slot, bool retired = false) => new(slot, $"Test GPU {slot}", 8L << 30, (uint)(10 + slot), retired);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_absent_gpu_with_a_slot_record_keeps_its_gear_while_another_gpu_is_present(bool retired)
    {
        var state = DemoAfter();
        // gpu#0 は盤に居る。gpu#1 は記録だけ（抜けた・引退）。gpu#2・gpu#3 は記録が無い（幻）
        state.GpuSlots = [Record(0), Record(1, retired)];
        var before = Copy(state.Adventure!);
        var e = new AdventureEngine(Book, state, new Random(1));
        var onlyGpu0 = Board.Where(b => b.Key is not ("gpu#1" or "gpu#2")).ToArray();

        Run(e, onlyGpu0, 3 * (int)AdventureEngine.StrayGraceSeconds);

        var s = state.Adventure!;
        Assert.Equal(before["gpu#1"], s.Equipped["gpu#1"]);   // 記録のある席は外さない
        Assert.Equal(before["gpu#0"], s.Equipped["gpu#0"]);
        Assert.False(s.Equipped.ContainsKey("gpu#2"));        // 記録の無い幻は従来どおり片付く
        Assert.False(s.Equipped.ContainsKey("gpu#3"));
        Assert.Equal(before["gpu#2"].Concat(before["gpu#3"]).OrderBy(x => x), s.Inventory.OrderBy(x => x));
    }

    [Fact]
    public void A_numbered_cpu_key_is_still_swept_even_when_gpu_slot_records_exist()
    {
        var state = DemoAfter();
        state.GpuSlots = [Record(0), Record(1), Record(2), Record(3)];
        state.Adventure!.Equipped["cpu#1"] = ["spare-screw", "thin-sheet", "rubber-foot"];
        var e = new AdventureEngine(Book, state, new Random(1));

        Run(e, Board, (int)AdventureEngine.StrayGraceSeconds + 60);

        var s = state.Adventure!;
        Assert.False(s.Equipped.ContainsKey("cpu#1"));        // cpu#0 は居て cpu#1 だけ居ない
        Assert.True(s.Equipped.ContainsKey("gpu#3"));         // 席の記録がある gpu#3 は残る
        Assert.Contains("spare-screw", s.Inventory);
    }

    [Fact]
    public void A_full_bag_turns_the_cleared_gear_into_loot_instead_of_dropping_it()
    {
        var state = DemoAfter();
        var s = state.Adventure!;
        for (int i = 0; i < AdventureEngine.InventoryCap; i++) s.Inventory.Add("old-label");
        int loot = s.Loot;
        var e = new AdventureEngine(Book, state, new Random(1));
        Run(e, Board, (int)AdventureEngine.StrayGraceSeconds + 60);

        Assert.False(s.Equipped.ContainsKey("gpu#3"));
        Assert.Equal(AdventureEngine.InventoryCap, s.Inventory.Count);
        int expected = new[] { "glowing-chip", "thin-sheet", "rubber-foot" }.Sum(id => Book.Item(id)!.SellPrice);
        Assert.Equal(loot + expected, s.Loot);
    }
}
