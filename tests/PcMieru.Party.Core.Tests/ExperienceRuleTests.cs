using PcMieru.Party.Game;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 既存の決まり: **経験値はその子自身の仕事でしか増えない。**
/// 冒険を足しても、ここが崩れていないことを確かめる。
/// </summary>
public class ExperienceRuleTests
{
    private static PartySlot Slot(string key, string id, double load) =>
        new(key, id, SlotKind.Pcie, key, "", load);

    [Fact]
    public void Each_part_gains_exactly_what_its_own_load_earns()
    {
        var state = new PartyState();
        state.Migrate();
        var d = new Director(TestData.Story, state, TestData.Book, new Random(1), () => 1_800_000_000);
        var expected = new Dictionary<string, double>();
        var rng = new Random(2);

        for (int t = 0; t < 50_000; t++)
        {
            // CPU は働き続け、2枚目の GPU は眠ったまま、メモリは揺れる
            double cpu = 0.6 + 0.4 * rng.NextDouble(), mem = rng.NextDouble();
            var slots = new[]
            {
                Slot("cpu#0", "cpu", cpu), Slot("memory", "memory", mem),
                Slot("gpu#0", "gpu", 0.9), Slot("gpu#1", "gpu", 0.0), Slot("hdd", "hdd", 0.05),
            };
            foreach (var s in slots)
                expected[s.Key] = expected.GetValueOrDefault(s.Key) + Director.ExpRate(s.Load) * 1.0;
            d.Tick(1.0, new Work(cpu, 0.9, mem, 0.05, 0.4), slots, "ja");
        }

        foreach (var (key, exp) in expected)
            Assert.Equal(exp, state.Experience[key], 6);
        // 眠っている GPU は、点けているだけの分（0.6/秒）しか増えない
        Assert.Equal(0.6 * 50_000, state.Experience["gpu#1"], 6);
        Assert.True(state.Level("gpu#0") > state.Level("gpu#1"));
    }

    [Fact]
    public void Adventure_never_touches_experience()
    {
        // 同じ負荷・同じ種で、冒険ありと冒険なしを並べて走らせる。経験値は1点も違わない
        var with = new Sim(seed: 21, adventure: true);
        var without = new Sim(seed: 21, adventure: false);
        for (int t = 0; t < 2 * 86400; t++)
        {
            with.Tick(Profile.Gaming);
            without.Tick(Profile.Gaming);
        }
        Assert.True(with.Adv.Expeditions > 10);   // 冒険は実際に回っていた
        Assert.True(with.Adv.Wins + with.Adv.Losses > 0);
        Assert.Equal(without.State.Experience.OrderBy(k => k.Key), with.State.Experience.OrderBy(k => k.Key));
        Assert.Equal(without.State.Journey, with.State.Journey);
    }

    [Fact]
    public void Another_parts_work_does_not_feed_this_part()
    {
        // GPU だけを回しても、メモリの経験値は変わらない
        double MemoryAfter(double gpuLoad)
        {
            var state = new PartyState();
            state.Migrate();
            var d = new Director(TestData.Story, state, TestData.Book, new Random(4), () => 1_800_000_000);
            for (int t = 0; t < 20_000; t++)
                d.Tick(1.0, new Work(0.1, gpuLoad, 0.4, 0, 0.4),
                       new[] { Slot("memory", "memory", 0.4), Slot("gpu#0", "gpu", gpuLoad) }, "en");
            return state.Experience["memory"];
        }
        Assert.Equal(MemoryAfter(0.0), MemoryAfter(1.0), 6);
    }
}
