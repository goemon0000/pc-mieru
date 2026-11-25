using PcMieru.Party.Adventure;
using PcMieru.Party.Hardware;
using PcMieru.Party.Game;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 欠けたチャンネルは旅程の式に入れない・測れていない席には装備を配らない（DESIGN-C 2.3、codex 8・9）。
/// </summary>
public sealed class FeedRulesTests
{
    /// <summary>旧い式（全部測れている前提）。全部そろったフレームでは、これと 1 ビットも違ってはいけない。</summary>
    private static double OldJourneyRate(Work w) => 0.15 + (w.Cpu + w.Gpu + w.Memory * 0.3 + w.Disk * 0.5) * 0.62;

    [Fact]
    public void A_complete_frame_gives_exactly_the_old_number()
    {
        var rng = new Random(20261125);
        for (int i = 0; i < 2000; i++)
        {
            var w = new Work(rng.NextDouble(), rng.NextDouble(), rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
            Assert.Equal(OldJourneyRate(w), Director.JourneyRate(w));   // 誤差を許さない（Equal は == と同じ）
        }

        Assert.Equal(OldJourneyRate(new Work(0, 0, 0, 0, 0)), Director.JourneyRate(new Work(0, 0, 0, 0, 0)));
        Assert.Equal(OldJourneyRate(new Work(1, 1, 1, 1, 1)), Director.JourneyRate(new Work(1, 1, 1, 1, 1)));
    }

    [Fact]
    public void A_missing_gpu_does_not_change_what_the_other_channels_give()
    {
        double cpu = 0.4, mem = 0.5, disk = 0.2;
        var channels = WorkChannels.All & ~WorkChannels.Gpu;

        double expected = 0.15 + (cpu + mem * 0.3 + disk * 0.5) * 0.62;   // GPU が無い機械の式
        // 欠けた GPU に何が入っていても（0 でも 0.9 でも）読まない
        Assert.Equal(expected, Director.JourneyRate(new Work(cpu, 0, mem, disk, 0.3, channels)));
        Assert.Equal(expected, Director.JourneyRate(new Work(cpu, 0.9, mem, disk, 0.3, channels)));
        // 0 として測れている GPU とは違う（欠けは 0 と読まない）が、他のチャンネルの寄与は同じ
        Assert.Equal(expected, Director.JourneyRate(new Work(cpu, 0.0, mem, disk, 0.3)));
    }

    [Fact]
    public void Missing_channels_leave_only_the_base_rate_and_a_missing_cpu_is_not_idle()
    {
        Assert.Equal(0.15, Director.JourneyRate(new Work(0.7, 0.7, 0.7, 0.7, 0.7, WorkChannels.None)));
        Assert.False(new Work(0, 0, 0, 0, 0, WorkChannels.Gpu).Has(WorkChannels.Cpu));
        Assert.True(new Work(0, 0, 0, 0, 0).Has(WorkChannels.All));
    }

    [Fact]
    public void WorkOf_marks_the_channels_the_frame_does_not_have()
    {
        var frame = PartyComposition.WorkOf(new BoardFrame(16, null, null, null, null, new BoardDisk(null, false), new BoardNet(0, 0, false)));

        Assert.False(frame.Has(WorkChannels.Cpu));
        Assert.False(frame.Has(WorkChannels.Gpu));
        Assert.False(frame.Has(WorkChannels.Memory));
        Assert.Equal(0.15, Director.JourneyRate(frame));
    }

    private static readonly WorldState World = new(new Dictionary<string, int>(), 3, 3, 1,
        new HashSet<string> { "cpu", "cpu-cooler", "memory", "gpu", "m2", "ssd", "hdd", "psu" }, 1, 1);

    private static PartySlot Slot(string key, string id, bool measured) =>
        new(key, id, SlotKind.Pcie, key, "", 0.2, measured);

    [Fact]
    public void An_unmeasured_slot_gets_no_starter_gear_until_the_first_tick_where_it_is_measured()
    {
        var state = new PartyState();
        state.Migrate();
        var e = new AdventureEngine(TestData.Book, state, new Random(1));
        var w = new Work(0.1, 0.1, 0.4, 0, 0.5);
        var output = new List<string>();

        // 測れている席だけに配る
        var unmeasured = new[] { Slot("cpu#0", "cpu", true), Slot("gpu#0", "gpu", false) };
        for (int t = 0; t < 5; t++) e.Tick(1.0, 0, w, unmeasured, World, output, "ja");
        Assert.True(state.Adventure!.Equipped.ContainsKey("cpu#0"));
        Assert.False(state.Adventure.Equipped.ContainsKey("gpu#0"));
        Assert.Contains(e.Members, m => m.Key == "gpu#0");   // 顔ぶれには居る（盤と同じ）

        // 最初に測れた秒に配る
        var measured = new[] { Slot("cpu#0", "cpu", true), Slot("gpu#0", "gpu", true) };
        e.Tick(1.0, 0, w, measured, World, output, "ja");
        Assert.True(state.Adventure.Equipped.ContainsKey("gpu#0"));
        Assert.NotEmpty(state.Adventure.Equipped["gpu#0"]);
    }
}
