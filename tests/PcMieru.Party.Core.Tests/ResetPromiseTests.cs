using PcMieru.Party.Adventure;
using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 設定 > データの「リセット」3 つの確認文（Strings.ResetStoryAsk / ResetLevelsAsk / ResetAllAsk）が言っている「残るもの・消えるもの」と、
/// PartyState の実際の動きが一致していることを固定する。文を変えたら、ここも見直す。
/// </summary>
public class ResetPromiseTests
{
    private static PartyState Played()
    {
        var s = new PartyState
        {
            Journey = 1234, Chapter = 3, Scene = 4, Boots = 7, UptimeSeconds = 9999, Lang = "en", Zoom = 3.0,
            Experience = { ["cpu#0"] = 5000, ["gpu#0"] = 7000 },
            Log = { "a line" },
            Adventure = new AdventureState
            {
                Town = 2, Gold = 777, Expeditions = 5, Wins = 4,
                Inventory = { "potion" },
                Equipped = { ["gpu#0"] = ["sword", "", "shield"], ["cpu#0"] = ["", "armor", ""] },
            },
        };
        return s;
    }

    [Fact]
    public void Reset_story_keeps_levels_items_gear_and_gold()
    {
        var s = Played();
        s.ResetStory();
        Assert.Equal(0, s.Chapter);
        Assert.Empty(s.Log);
        Assert.Equal(0, s.Adventure!.Town);
        Assert.Equal(2, s.Experience.Count);
        Assert.Equal(777, s.Adventure.Gold);
        Assert.Equal(new[] { "potion" }, s.Adventure.Inventory);
        Assert.Equal(new[] { "sword", "", "shield" }, s.Adventure.Equipped["gpu#0"]);
        Assert.Equal(new[] { "", "armor", "" }, s.Adventure.Equipped["cpu#0"]);
    }

    [Fact]
    public void Reset_levels_keeps_story_items_gear_and_gold()
    {
        var s = Played();
        s.ResetLevels();
        Assert.Empty(s.Experience);
        Assert.Equal(3, s.Chapter);
        Assert.Equal(2, s.Adventure!.Town);
        Assert.Equal(777, s.Adventure.Gold);
        Assert.Equal(new[] { "potion" }, s.Adventure.Inventory);
        Assert.Equal(new[] { "sword", "", "shield" }, s.Adventure.Equipped["gpu#0"]);
    }

    [Fact]
    public void Reset_all_deletes_story_levels_items_gear_gold_and_the_record_but_keeps_settings()
    {
        var s = Played();
        s.ResetAll();
        Assert.Equal(0, s.Chapter);
        Assert.Empty(s.Experience);
        Assert.Empty(s.Adventure!.Inventory);
        Assert.Empty(s.Adventure.Equipped);
        Assert.Equal(AdventureState.StartingGold, s.Adventure.Gold);
        Assert.Equal(0, s.Adventure.Expeditions);
        Assert.Equal(0, s.Boots);
        Assert.Equal(0, s.UptimeSeconds);
        Assert.Equal("en", s.Lang);
        Assert.Equal(3.0, s.Zoom);
    }
}
