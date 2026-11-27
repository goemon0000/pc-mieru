using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 測れていない席（Measured=false）は、冒険の報酬も着け替えも受け取らない。**いま着けている装備はそのまま持ち、絵には出続ける。**
/// </summary>
public class UnmeasuredSlotGearTests
{
    private static readonly AdventureBook Book = TestData.Book;

    private static PartySlot Slot(string key, string id, bool measured) => new(key, id, SlotKind.Pcie, key, "", measured ? 0.2 : 0, measured);

    private static readonly WorldState World = new(new Dictionary<string, int>(), 3, 3, 1,
        new HashSet<string> { "cpu", "cpu-cooler", "memory", "gpu", "m2", "ssd", "hdd", "psu" }, 1, 1);

    private static readonly string[] Worn = ["glowing-chip", "thin-sheet", "rubber-foot"];

    /// <summary>gpu#1 が着けている物より真に良い、gpu 向けの道具を枠ごとに1つ（持ち物に入れる）。</summary>
    private static string[] BetterForGpu()
    {
        var better = new List<string>();
        for (int slot = 0; slot < Equipment.SlotCount; slot++)
        {
            int current = Equipment.BaseScore(Book.Item(Worn[slot]), "gpu");
            var best = Book.Items
                .Where(i => (int)i.SlotValue == slot && Equipment.BaseScore(i, "gpu") > current)
                .OrderByDescending(i => Equipment.BaseScore(i, "gpu")).ThenBy(i => i.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is not null) better.Add(best.Id);
        }
        return better.ToArray();
    }

    /// <summary>gpu#1 が装備を持ち、持ち物に良い物がある町。着け替えの場面の手前で止めてある。</summary>
    private static PartyState Town(string[] bag)
    {
        var state = PartyState.FromJson("""
            { "SaveVersion": 1, "Adventure": { "Mode": "Town", "Phase": "Equip", "Town": 1, "Gold": 0, "Inventory": [] }, "Chapter": 3, "Scene": 7 }
            """);
        var s = state.Adventure!;
        s.Mode = AdventureMode.Town;
        s.Phase = TownPhase.Equip;
        s.Equipped["gpu#1"] = Worn.ToArray();
        s.Inventory.AddRange(bag);
        return state;
    }

    /// <summary>1 秒ぶん回して顔ぶれを決め、町の着け替えの場面を1つ進める（着け替え・売り・買いは、この呼び出しの内側で起きる）。</summary>
    private static void OneTownStep(AdventureEngine e, bool gpu1Measured)
    {
        PartySlot[] board = [Slot("gpu#1", "gpu", gpu1Measured)];   // 他に着け替えの相手を置かない（持ち物の物が gpu#1 以外へ流れない）
        e.Tick(1.0, 0, new Work(0.1, 0.1, 0.4, 0, 0.5), board, World, [], "ja");
        e.Step(World, "ja");
    }

    [Fact]
    public void A_measured_gpu_takes_the_better_gear_from_the_bag()
    {
        var bag = BetterForGpu();
        Assert.NotEmpty(bag);   // この試験の前提: 良くなる物が本当にある
        var state = Town(bag);
        var e = new AdventureEngine(Book, state, new Random(1));

        OneTownStep(e, gpu1Measured: true);

        Assert.NotEqual(Worn, state.Adventure!.Equipped["gpu#1"]);
    }

    [Fact]
    public void An_unmeasured_gpu_keeps_its_gear_gets_nothing_new_and_is_still_a_member()
    {
        var bag = BetterForGpu();
        var state = Town(bag);
        var e = new AdventureEngine(Book, state, new Random(1));

        OneTownStep(e, gpu1Measured: false);

        var s = state.Adventure!;
        Assert.Equal(Worn, s.Equipped["gpu#1"]);              // いま着けている物はそのまま
        Assert.Contains("gpu#1", e.Members.Select(m => m.Key)); // 顔ぶれには居る（絵にも出る）
        // 持ち物の良い物は誰にも渡らず、持ち物に残る（着け替えの相手が居ない。売りもしない: gpu#1 はまだ使える）
        Assert.Equal(bag.OrderBy(x => x), s.Inventory.OrderBy(x => x));
    }

    [Fact]
    public void An_unmeasured_gpu_without_gear_gets_no_starters_and_no_record()
    {
        var state = Town(BetterForGpu());
        state.Adventure!.Equipped.Remove("gpu#1");
        var e = new AdventureEngine(Book, state, new Random(1));

        OneTownStep(e, gpu1Measured: false);

        Assert.False(state.Adventure!.Equipped.ContainsKey("gpu#1"));
    }
}
