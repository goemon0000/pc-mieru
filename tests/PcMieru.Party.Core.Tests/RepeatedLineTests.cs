using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>同じ文が二行続かないこと。</summary>
public class RepeatedLineTests
{
    /// <summary>
    /// 試運転の保存（make-demo.py）: 旅程は第三章の途中、章と場面は 0、記録は空。
    /// 最初の秒に「章題・場面」を出した上で、Log がまだ空なので「章の頭の一文」として同じ場面をもう一度出していた。
    /// </summary>
    [Fact]
    public void Starting_mid_chapter_with_an_empty_log_does_not_repeat_the_scene()
    {
        var story = TestData.Story;
        var ch = story.Chapters[3];
        var state = new PartyState();
        state.Migrate();
        state.Journey = ch.Require + ch.ScenePer * 1.5;   // 第三章の 2 場面目
        var sim = new Sim(5, state: state);

        var first = sim.TickWith(new Work(0.1, 0, 0.4, 0, 0.5)).ToList();
        Assert.Equal(3, state.Chapter);
        Assert.StartsWith("── ", first[0]);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.Contains("HDDくんの荷が一番重い。本人は、重いと言ったことがない。", first);
        Assert.Equal(1, state.Log.Count(l => l == "HDDくんの荷が一番重い。本人は、重いと言ったことがない。"));
    }

    [Fact]
    public void A_fresh_start_still_shows_the_opening_line()
    {
        var state = new PartyState();
        state.Migrate();
        var sim = new Sim(2, state: state);
        var first = sim.TickWith(new Work(0, 0, 0.3, 0, 0.5));
        var opening = TestData.Story.Chapters[0].Scenes[0].Variants.Select(v => v.Ja).ToHashSet();
        Assert.Equal(1, first.Count(opening.Contains));
    }

    [Theory]
    [InlineData(Profile.Gaming, 11)]
    [InlineData(Profile.Idle, 12)]
    [InlineData(Profile.Rendering, 13)]
    public void Over_two_days_no_line_is_shown_twice_in_a_row(Profile profile, int seed)
    {
        var sim = new Sim(seed);
        string last = "";
        for (int t = 0; t < 2 * 86400; t++)
            foreach (var line in sim.Tick(profile))
            {
                Assert.True(line != last, $"{t} 秒目に同じ文が続いた: {line}");
                last = line;
            }
    }
}
