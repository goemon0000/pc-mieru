using PcMieru.Party.Game;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// レベルが上がった一行。**条件を守り、物語を押し流さない。**
/// 以前は when を見ずに選んでいたので、新しく入れた機械の Lv2 で「門の向こうで…」が出た。
/// HDD の無い機械で「HDDくんが…」も出た。CPU くんとクーラーくんは同じ秒に上がるので二行ずつ流れた。
/// </summary>
public class LevelUpLineTests
{
    private static PartySlot Slot(string key, string id, double load) => new(key, id, SlotKind.Pcie, key, "", load);

    private static StoryEvent LevelUp => TestData.Story.EventById("level-up")!;

    private static WorldState World(int chapter, double hours, params string[] present) =>
        new(new Dictionary<string, int>(), chapter, 1, 1,
            new HashSet<string>(present.Concat(new[] { "cpu", "cpu-cooler", "memory", "gpu", "psu" })), 1, hours);

    [Fact]
    public void A_fresh_machine_without_an_hdd_gets_no_conditioned_or_hdd_lines_and_no_flood()
    {
        var state = new PartyState();
        state.Migrate();
        var d = new Director(TestData.Story, state, TestData.Book, new Random(3), () => 1_800_000_000);
        var slots = new[]
        {
            Slot("cpu#0", "cpu", 1.0), Slot("cpu-cooler#0", "cpu-cooler", 1.0), Slot("memory", "memory", 0.9),
            Slot("gpu#0", "gpu", 1.0), Slot("ssd", "ssd", 0.6), Slot("psu", "psu", 1.0),
        };
        var lines = new List<(int At, string Line, int Chapter)>();
        for (int t = 0; t < 2 * 3600; t++)
        {
            int here = 0;
            foreach (var l in d.Tick(1.0, new Work(1, 1, 0.9, 0.6, 0.5), slots, "ja"))
                if (l.Contains("  (Lv.")) { lines.Add((t, l, state.Chapter)); here++; }
            Assert.True(here <= 1, $"{t} 秒目にレベルの文が {here} 行");
        }

        Assert.NotEmpty(lines);
        Assert.True(lines.Count <= 2 * 3600 / Director.LevelLineGap + 1, $"レベルの文が {lines.Count} 行");
        for (int i = 1; i < lines.Count; i++)
            Assert.True(lines[i].At - lines[i - 1].At >= Director.LevelLineGap, "間隔が短い");
        // 同じ秒に上がった CPU くんとクーラーくんは一行にまとまっている
        Assert.Contains(lines, x => x.Line.Contains("・"));

        // 全力なので章は進む。その時点の章で、まだ出てはいけない文を見る
        Assert.Contains(lines, x => x.Chapter < 2);
        foreach (var (_, l, chapter) in lines)
        {
            Assert.DoesNotContain("HDDくん", l);          // 載っていない
            Assert.DoesNotContain("数えるのが難しい", l);  // hours>=500
            Assert.DoesNotContain("何十度目か", l);        // boots>=50
            Assert.DoesNotContain("帳面の何枚分", l);      // hours>=100
            if (chapter < 5) Assert.DoesNotContain("門の向こう", l);
            if (chapter < 2) Assert.DoesNotContain("谷を越えた", l);
        }
    }

    [Fact]
    public void Pick_honours_when_and_never_names_the_one_who_levelled()
    {
        var rng = new Random(1);
        var fresh = World(0, 0.5);
        var grown = World(8, 900, "hdd");
        var seenGrown = new HashSet<string>();
        for (int i = 0; i < 3000; i++)
        {
            var a = Director.PickLevelUp(LevelUp, fresh, "cpu", rng)!;
            Assert.True(Conditions.Match(a.When, fresh), a.Ja);

            var b = Director.PickLevelUp(LevelUp, grown, "hdd", rng)!;
            Assert.DoesNotContain("HDDくん", b.Ja.Replace("{name}", ""));
            var c = Director.PickLevelUp(LevelUp, grown, "psu", rng)!;
            Assert.DoesNotContain("電源くん", c.Ja.Replace("{name}", ""));
            seenGrown.Add(Director.PickLevelUp(LevelUp, grown, "gpu", rng)!.Ja);
        }
        // 条件に合う機械では、条件付きの文も出る
        Assert.Contains(seenGrown, x => x.Contains("門の向こう"));
        Assert.Contains(seenGrown, x => x.Contains("HDDくんが"));
    }

    [Fact]
    public void Level_up_lines_that_name_a_drive_require_that_drive()
    {
        // 執筆規約 冒険 §3: 居ないかもしれない者を名指しする文には has: を付ける
        var maybeAbsent = new Dictionary<string, string> { ["hdd"] = "HDDくん", ["ssd"] = "SSDくん", ["m2"] = "M.2くん" };
        foreach (var t in LevelUp.Texts)
            foreach (var (id, name) in maybeAbsent)
                if (t.Ja.Contains(name)) Assert.Equal("has:" + id, t.When);
    }

    [Fact]
    public void English_coalesced_line_reads_naturally()
    {
        var state = new PartyState();
        state.Migrate();
        var d = new Director(TestData.Story, state, TestData.Book, new Random(9), () => 1_800_000_000);
        var slots = new[] { Slot("cpu#0", "cpu", 1.0), Slot("cpu-cooler#0", "cpu-cooler", 1.0) };
        var seen = new List<string>();
        for (int t = 0; t < 3600; t++)
            foreach (var l in d.Tick(1.0, new Work(1, 0, 0.3, 0, 0.5), slots, "en"))
                if (l.Contains("  (Lv.")) seen.Add(l);
        Assert.Contains(seen, l => l.Contains(", the cooler Lv.") || l.Contains(", the CPU Lv."));
    }
}
