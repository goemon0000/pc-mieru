using PcMieru.Party.Adventure;
using Xunit.Abstractions;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 30日ぶんを1秒刻みで回す（1本あたり 259 万回）。
/// **常駐物は何週間も点けっぱなしにされる。** 数分の試験では、止まる・膨らむ・抜けられない、は見つからない。
/// </summary>
public class LongSimulationTests
{
    private const int Days = 30;
    private const int Day = 86400;
    private readonly ITestOutputHelper _out;

    public LongSimulationTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(Profile.Idle, 101)]
    [InlineData(Profile.Office, 202)]
    [InlineData(Profile.Gaming, 303)]
    [InlineData(Profile.Rendering, 404)]
    public void Thirty_days_without_getting_stuck(Profile profile, int seed)
    {
        var sim = new Sim(seed);
        var a = sim.Adv;
        var engine = sim.Director.Adventure!;

        long lastReturnAt = 0, longestAway = 0;
        int lastReturns = 0, lastChapter = sim.State.Chapter;
        var returnsByDay = new int[Days];   // 累計
        var allocPerDay = new long[Days];
        var maxTown = 0;
        long goldMax = 0;
        int invMax = 0, lootMax = 0;
        string? badLine = null;

        for (int day = 0; day < Days; day++)
        {
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            for (int s = 0; s < Day; s++)
            {
                var lines = sim.Tick(profile);
                long now = sim.Elapsed;

                // 埋まっていない差し込みや空の文が流れていないか
                for (int i = 0; i < lines.Count; i++)
                    if (badLine is null && (lines[i].Length == 0 || lines[i].IndexOf('{') >= 0)) badLine = lines[i];

                if (a.Returns != lastReturns)
                {
                    longestAway = Math.Max(longestAway, now - lastReturnAt);
                    lastReturnAt = now;
                    lastReturns = a.Returns;
                }
                if (sim.State.Chapter != lastChapter)
                {
                    Assert.True(sim.State.Chapter > lastChapter, "章が戻った");
                    lastChapter = sim.State.Chapter;
                }

                if ((s & 1023) == 0)
                {
                    goldMax = Math.Max(goldMax, a.Gold);
                    invMax = Math.Max(invMax, a.Inventory.Count);
                    lootMax = Math.Max(lootMax, a.Loot);
                    maxTown = Math.Max(maxTown, a.Town);
                    Assert.InRange(a.Gold, 0, AdventureEngine.GoldCap);
                    Assert.InRange(a.Inventory.Count, 0, AdventureEngine.InventoryCap);
                    Assert.InRange(a.Hp, 0.0, 1.0);
                    Assert.InRange(a.Fatigue, 0.0, 1.0);
                    Assert.InRange(a.Depth, 0, 32);
                    Assert.Equal(Rig.Desktop.Length, a.Equipped.Count);
                    Assert.True(sim.State.Log.Count <= 40);
                    Assert.True(sim.State.RecentVignettes.Count <= 120);
                    Assert.True(sim.State.LastEventAt.Count <= 16);
                }
            }
            allocPerDay[day] = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            returnsByDay[day] = a.Returns;
        }

        var returnsPerDay = returnsByDay.Select((v, i) => i == 0 ? v : v - returnsByDay[i - 1]).ToArray();
        _out.WriteLine($"{profile}: 章 {sim.State.Chapter} / 町 {a.Town} / 遠出 {a.Expeditions} / 帰還 {a.Returns}"
                       + $" / 勝ち {a.Wins} 負け {a.Losses} / 金 {a.Gold}（最大 {goldMax}）/ 持ち物最大 {invMax} / 荷最大 {lootMax}");
        _out.WriteLine($"  一番長い留守 {longestAway / 60} 分 / 日ごとの帰還 {string.Join(",", returnsPerDay)}");
        _out.WriteLine($"  構え 慎重/五分/大胆 = {string.Join("/", engine.StanceCounts)}");
        _out.WriteLine($"  1秒あたりの割り当て: 初日 {allocPerDay[0] / (double)Day:0.0} B / 最終日 {allocPerDay[^1] / (double)Day:0.0} B");
        _out.WriteLine("  装備: " + string.Join(" | ", a.Equipped.Select(kv => kv.Key + "=" + string.Join(",", kv.Value))));

        Assert.Null(badLine);

        // --- 止まらない: 毎日、遠出から帰ってきている
        Assert.All(returnsPerDay, r => Assert.True(r > 0, "帰ってこない日があった"));
        // --- 必ず帰る: 留守は長くても数時間（1場面 ≤ 約2分 × 40場面）
        Assert.True(longestAway <= 4 * 3600, $"留守が長すぎる: {longestAway / 60} 分");
        // 出かけた分は帰っている（今まさに出ている1回を除く）
        Assert.InRange(a.Expeditions - a.Returns, 0, 1);

        // --- 物語が進む: 本筋は最後の章まで、町も最後の町まで（放置でも 90 時間で読み切る量）
        Assert.Equal(TestData.Story.Chapters.Count - 1, sim.State.Chapter);
        Assert.Equal(TestData.Book.Towns.Count - 1, maxTown);
        // 本筋を読み切った後も、冒険の文は最終日まで流れ続ける
        Assert.True(returnsPerDay[^1] > 0);

        // --- 量が膨らまない
        Assert.True(goldMax <= AdventureEngine.GoldCap);
        Assert.True(invMax <= AdventureEngine.InventoryCap);
        Assert.True(lootMax < 10_000);

        // --- 毎秒の仕事は O(1): 最終日の割り当てが初日より増えていない（溜め込んで重くなっていない）
        double first = allocPerDay[0] / (double)Day, last = allocPerDay[^1] / (double)Day;
        Assert.True(last <= first * 1.5 + 32, $"割り当てが増えた: {first:0.0} → {last:0.0} B/秒");
        Assert.True(last < 512, $"毎秒の割り当てが多い: {last:0.0} B/秒");
    }

    [Fact]
    public void Same_seed_same_story()
    {
        var a = new Sim(seed: 77);
        var b = new Sim(seed: 77);
        for (int t = 0; t < 86400; t++)
        {
            a.Tick(Profile.Office);
            b.Tick(Profile.Office);
        }
        Assert.Equal(a.State.Log, b.State.Log);
        Assert.Equal(a.State.ToJson(), b.State.ToJson());
    }

    [Fact]
    public void English_lines_are_filled_and_capitalized()
    {
        var sim = new Sim(seed: 8) { Lang = "en" };
        var seen = new List<string>();
        for (int t = 0; t < 2 * 86400; t++)
            foreach (var l in sim.Tick(Profile.Gaming)) seen.Add(l);
        Assert.NotEmpty(seen);
        foreach (var l in seen)
        {
            Assert.DoesNotContain("{", l);
            if (char.IsLetter(l[0])) Assert.True(char.IsUpper(l[0]), $"小文字で始まる: {l}");
        }
    }

    [Fact]
    public void Steps_wait_for_work_and_never_come_faster_than_the_gap()
    {
        // 冒険の場面は、働いた分（旅程）と最短の間隔の両方がそろったときだけ進む
        var sim = new Sim(seed: 12);
        var a = sim.Adv;
        long lastChange = 0, minGap = long.MaxValue;
        string sig = Sig(a);
        for (int t = 0; t < 86400; t++)
        {
            sim.Tick(Profile.Rendering);
            string now = Sig(a);
            if (now != sig)
            {
                if (lastChange > 0) minGap = Math.Min(minGap, sim.Elapsed - lastChange);
                lastChange = sim.Elapsed;
                sig = now;
            }
        }
        Assert.True(minGap >= AdventureEngine.MinStepGap, $"間隔が短い: {minGap} 秒");

        static string Sig(AdventureState a) => $"{a.Mode}/{a.Phase}/{a.Depth}/{a.BackSteps}/{a.Pending}/{a.Rests}/{a.Bought}/{a.Expeditions}";
    }
}
