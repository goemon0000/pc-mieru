using PcMieru.Party.Adventure;

namespace PcMieru.Party.Core.Tests;

public class StanceTests
{
    private const int Samples = 4000;

    /// <summary>同じ材料で何度も決めさせ、構えの割合を出す [慎重, 五分, 大胆]。</summary>
    private static double[] Share(StanceInput input, int seed = 1)
    {
        var rng = new Random(seed);
        var n = new int[3];
        for (int i = 0; i < Samples; i++) n[(int)StanceRules.Decide(input, rng).Stance]++;
        return n.Select(x => x / (double)Samples).ToArray();
    }

    private static StanceInput Healthy(double load = 0.3, double hot = 0) =>
        new(Hp: 1, Fatigue: 0, GearRatio: 1, LootFill: 0, Streak: 0, MachineLoad: load, MachineHot: hot);

    [Fact]
    public void Healthy_party_on_a_calm_machine_is_bold()
    {
        var s = Share(Healthy(load: 0.02));
        Assert.True(s[(int)Stance.Bold] > 0.95, $"bold {s[2]:P0}");
    }

    [Fact]
    public void Hurt_and_tired_party_is_careful()
    {
        var s = Share(new StanceInput(0.2, 0.7, 1, 0.3, 0, 0.3, 0));
        Assert.True(s[(int)Stance.Careful] > 0.95, $"careful {s[0]:P0}");
    }

    [Fact]
    public void Sustained_heat_makes_the_same_party_more_careful()
    {
        var calm = Share(Healthy(load: 0.05));
        var busy = Share(Healthy(load: 0.95, hot: 1));
        Assert.True(busy[(int)Stance.Bold] < calm[(int)Stance.Bold] - 0.5,
            $"bold calm {calm[2]:P0} / busy {busy[2]:P0}");
        Assert.True(busy[(int)Stance.Careful] + busy[(int)Stance.Balanced] > 0.9);
    }

    [Fact]
    public void A_short_spike_is_not_heat()
    {
        // 5分平均が低く、高負荷の割合もほぼ無いなら、熱とはみなさない
        Assert.Equal(0, StanceRules.Heat(0.3, 0.0));
        Assert.True(StanceRules.Heat(0.95, 0.9) >= 0.99);
    }

    [Fact]
    public void Boldness_grows_with_hp()
    {
        double prev = -1;
        for (double hp = 0; hp <= 1.0001; hp += 0.1)
        {
            var bold = Share(new StanceInput(hp, 0.3, 1, 0.2, 0, 0.3, 0))[(int)Stance.Bold];
            Assert.True(bold >= prev - 0.02, $"hp {hp:0.0}: {bold:P0} < {prev:P0}");
            prev = bold;
        }
    }

    [Fact]
    public void Caution_grows_with_machine_load()
    {
        double prev = -1;
        for (double load = 0; load <= 1.0001; load += 0.1)
        {
            var careful = Share(new StanceInput(0.7, 0.3, 1, 0.2, 0, load, load > 0.85 ? 1 : 0))[(int)Stance.Careful];
            Assert.True(careful >= prev - 0.02, $"load {load:0.0}: {careful:P0} < {prev:P0}");
            prev = careful;
        }
    }

    [Fact]
    public void Heavy_loot_and_losing_streaks_push_toward_caution()
    {
        var light = Share(new StanceInput(0.8, 0.2, 1, 0.0, 0, 0.3, 0));
        var heavy = Share(new StanceInput(0.8, 0.2, 1, 0.95, 0, 0.3, 0));
        var losing = Share(new StanceInput(0.8, 0.2, 1, 0.0, -3, 0.3, 0));
        Assert.True(heavy[(int)Stance.Careful] > light[(int)Stance.Careful]);
        Assert.True(losing[(int)Stance.Bold] < light[(int)Stance.Bold]);
    }

    [Fact]
    public void The_reason_names_the_factor_that_mattered_most()
    {
        Assert.Equal(Reason.Hurt, StanceRules.Decide(new StanceInput(0.1, 0.3, 1, 0, 0, 0.3, 0), 0).WhySafe);
        Assert.Equal(Reason.Hot, StanceRules.Decide(new StanceInput(0.7, 0.3, 1, 0, 0, 1.0, 1.0), 0).WhySafe);
        Assert.Equal(Reason.Heavy, StanceRules.Decide(new StanceInput(0.7, 0.3, 1, 1.0, 0, 0.3, 0), 0).WhySafe);
        Assert.Equal(Reason.GearStrong, StanceRules.Decide(new StanceInput(0.6, 0.3, 2.0, 0, 0, 0.3, 0), 0).WhyBold);
        Assert.Equal(Reason.Calm, StanceRules.Decide(new StanceInput(0.6, 0.3, 1, 0, 0, 0.01, 0), 0).WhyBold);
        // 何も効いていなければ、理由は「特に無い」
        Assert.Equal(Reason.None, StanceRules.Decide(new StanceInput(0.6, 0.3, 1, 0, 0, 0.3, 0), 0).WhyBold);
    }

    [Fact]
    public void Thresholds_are_as_documented()
    {
        var mid = new StanceInput(0.6, 0.3, 1, 0, 0, 0.3, 0);   // 合計 0
        Assert.Equal(Stance.Balanced, StanceRules.Decide(mid, 0).Stance);
        Assert.Equal(Stance.Bold, StanceRules.Decide(mid, 0.5).Stance);
        Assert.Equal(Stance.Careful, StanceRules.Decide(mid, -0.5).Stance);
    }

    [Fact]
    public void In_play_a_rendering_machine_decides_more_carefully_than_an_idle_one()
    {
        var idle = new Sim(seed: 3);
        var render = new Sim(seed: 3);
        for (int t = 0; t < 3 * 86400; t++)
        {
            idle.Tick(Profile.Idle);
            render.Tick(Profile.Rendering);
        }
        double Careful(Sim s)
        {
            var c = s.Director.Adventure!.StanceCounts;
            return c[0] / (double)Math.Max(1, c.Sum());
        }
        double Bold(Sim s)
        {
            var c = s.Director.Adventure!.StanceCounts;
            return c[2] / (double)Math.Max(1, c.Sum());
        }
        Assert.True(idle.Director.Adventure!.StanceCounts.Sum() > 100);
        Assert.True(render.Director.Adventure!.StanceCounts.Sum() > 100);
        Assert.True(Careful(render) > Careful(idle), $"careful idle {Careful(idle):P0} / render {Careful(render):P0}");
        Assert.True(Bold(render) < Bold(idle), $"bold idle {Bold(idle):P0} / render {Bold(render):P0}");
    }
}
