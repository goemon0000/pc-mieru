using PcMieru.Party.Adventure;

namespace PcMieru.Party.Core.Tests;

/// <summary>冒険の文が、その場の事実と食い違わないこと。</summary>
public class AdventureTextTests
{
    private static string[] Filled(string category, string town) =>
        TestData.Book.Lines(category).Select(l => l.Ja.Replace("{town}", town)).ToArray();

    [Fact]
    public void The_very_first_arrival_has_its_own_line_and_later_ones_do_not()
    {
        var sim = new Sim(seed: 17);
        var a = sim.Adv;
        string? first = null, second = null;
        int townAtReturn = -1;
        for (int t = 0; t < 4 * 3600 && second is null; t++)
        {
            bool arriving = a.Mode == AdventureMode.Town && a.Phase == TownPhase.Arrive;
            int returns = a.Returns;
            var lines = sim.Tick(Profile.Office);
            if (first is null && arriving && a.Phase != TownPhase.Arrive) first = lines.Single();
            else if (a.Returns > returns)
            {
                townAtReturn = a.Town;
                second = lines.SingleOrDefault() ?? "";
            }
        }
        string town = TestData.Book.Towns[0].Name.Ja;
        Assert.Contains(first, Filled("town-first", town));
        // 前の町が無いのに「前の町には戻らず」「これまでの町より」とは書かない
        Assert.DoesNotContain(first, Filled("town-new", town));

        Assert.NotNull(second);
        string again = TestData.Book.Towns[townAtReturn].Name.Ja;
        Assert.DoesNotContain(second, Filled("town-first", again));
        Assert.Contains(second, Filled("town-arrive", again).Concat(Filled("town-new", again)));
    }

    [Fact]
    public void Fresh_is_only_given_as_the_reason_at_near_full_hp()
    {
        // 体力の他に効いている物が無い場面
        StanceResult At(double hp) => StanceRules.Decide(new StanceInput(hp, 0.3, 1, 0, 0, 0.3, 0), 0);
        Assert.Equal(Reason.Fresh, At(1.0).WhyBold);
        Assert.Equal(Reason.Fresh, At(StanceRules.FreshAt).WhyBold);
        Assert.NotEqual(Reason.Fresh, At(0.9).WhyBold);
        Assert.NotEqual(Reason.Fresh, At(0.7).WhyBold);
        // 点には体力の分がそのまま入る（理由に書かないだけ）
        Assert.Equal((0.9 - 0.6) * 2.0, At(0.9).Score, 6);
        // 体力の他に効いている物があれば、そちらが理由になる
        var rested = StanceRules.Decide(new StanceInput(0.9, 0.0, 1, 0, 0, 0.3, 0), 0);
        Assert.Equal(Reason.Rested, rested.WhyBold);
    }

    [Fact]
    public void In_play_fresh_is_never_said_of_a_wounded_party()
    {
        // 遠出の場面で「傷が無い」の文が出たとき、体力はほぼ満ちている
        var fresh = TestData.Book.Lines("why-fresh").Select(l => l.Ja).ToArray();
        var sim = new Sim(seed: 23);
        int seen = 0;
        for (int t = 0; t < 3 * 86400; t++)
        {
            double hp = sim.Adv.Hp;
            foreach (var l in sim.Tick(Profile.Gaming))
                if (fresh.Any(f => l.StartsWith(f, StringComparison.Ordinal)))
                {
                    seen++;
                    Assert.True(hp >= StanceRules.FreshAt, $"体力 {hp:0.00} で「{l}」");
                }
        }
        Assert.True(seen > 0, "傷が無いの文が一度も出なかった（試験が何も見ていない）");
    }
}
