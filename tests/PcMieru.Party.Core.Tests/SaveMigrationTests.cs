using System.Text.Json.Nodes;
using PcMieru.Party.Adventure;
using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 古い保存が今の版で読めること。**育てた分と物語の進みが消えるのは、一番やってはいけない。**
/// Fixtures/state-v0.6.0.json と state-v0.3.json は、その版の PartyState で実際に書き出した物。
/// </summary>
public class SaveMigrationTests
{
    [Fact]
    public void V060_save_keeps_story_and_levels_and_gains_an_adventure()
    {
        string json = TestData.Fixture("state-v0.6.0.json");
        var before = JsonNode.Parse(json)!;
        var s = PartyState.FromJson(json);

        Assert.Equal(PartyState.CurrentVersion, s.SaveVersion);
        Assert.Equal(30512.75, s.Journey);
        Assert.Equal(13, s.Chapter);
        Assert.Equal(4, s.Scene);
        Assert.Equal(41, s.Boots);
        Assert.Equal(512_345.5, s.UptimeSeconds);
        Assert.Equal("name", s.Labels);
        Assert.Equal(-1600, s.WindowLeft);
        Assert.Equal(2, s.Log.Count);
        Assert.Equal(1_789_990_000, s.LastEventAt["gpu-very-high"]);

        foreach (var (key, node) in before["Experience"]!.AsObject())
        {
            double exp = node!.GetValue<double>();
            Assert.Equal(exp, s.Experience[key]);
            Assert.Equal(PartyState.LevelOf(exp), s.Level(key));
        }

        // 冒険は最初の町から、何も持たずに
        Assert.NotNull(s.Adventure);
        Assert.Equal(AdventureMode.Town, s.Adventure!.Mode);
        Assert.Equal(AdventureState.StartingGold, s.Adventure.Gold);
        Assert.Empty(s.Adventure.Inventory);
        Assert.Empty(s.Adventure.Equipped);
    }

    [Fact]
    public void V03_save_moves_experience_to_numbered_keys()
    {
        var s = PartyState.FromJson(TestData.Fixture("state-v0.3.json"));
        Assert.Equal(5000, s.Experience["cpu#0"]);
        Assert.Equal(4800, s.Experience["cpu-cooler#0"]);
        Assert.Equal(7000, s.Experience["gpu#0"]);
        Assert.Equal(1000, s.Experience["memory"]);
        Assert.False(s.Experience.ContainsKey("cpu"));
        Assert.False(s.Experience.ContainsKey("gpu"));
        Assert.Equal(900, s.Journey);
        Assert.Equal(PartyState.CurrentVersion, s.SaveVersion);
    }

    [Fact]
    public void Migrated_save_round_trips_and_migration_is_idempotent()
    {
        var a = PartyState.FromJson(TestData.Fixture("state-v0.6.0.json"));
        string once = a.ToJson();
        var b = PartyState.FromJson(once);
        Assert.Equal(once, b.ToJson());
        b.Migrate();
        Assert.Equal(once, b.ToJson());
    }

    [Fact]
    public void Playing_on_after_migration_does_not_reset_progress()
    {
        var s = PartyState.FromJson(TestData.Fixture("state-v0.6.0.json"));
        var expBefore = new Dictionary<string, double>(s.Experience);
        var sim = new Sim(seed: 11, state: s);
        for (int t = 0; t < 6 * 3600; t++) sim.Tick(Profile.Office);

        Assert.True(s.Journey > 30512.75);
        Assert.True(s.Chapter >= 13);
        foreach (var (key, exp) in expBefore)
            Assert.True(s.Experience[key] >= exp, $"{key} の経験値が減った");

        // 冒険は本筋の章に合った町から始まる（第十三章 → 残骸の市）
        Assert.Equal(TestData.Book.TownFor(s.Chapter), s.Adventure!.Town);
        Assert.True(s.Adventure.Expeditions > 0);
        // 一行の全員に、ありあわせが配られている
        foreach (var d in Rig.Desktop) Assert.True(s.Adventure.Equipped.ContainsKey(d.Key));
    }

    [Fact]
    public void Adventure_state_mid_expedition_survives_a_save()
    {
        var sim = new Sim(seed: 5);
        // 遠出に出ているところまで進める
        for (int t = 0; t < 200_000 && sim.Adv.Mode != AdventureMode.Out; t++) sim.Tick(Profile.Office);
        Assert.Equal(AdventureMode.Out, sim.Adv.Mode);

        var copy = PartyState.FromJson(sim.State.ToJson());
        var a = sim.Adv;
        var b = copy.Adventure!;
        Assert.Equal(a.Mode, b.Mode);
        Assert.Equal(a.Place, b.Place);
        Assert.Equal(a.Depth, b.Depth);
        Assert.Equal(a.Gold, b.Gold);
        Assert.Equal(a.Hp, b.Hp);
        Assert.Equal(a.Inventory, b.Inventory);
        Assert.Equal(a.Equipped.Keys.OrderBy(k => k), b.Equipped.Keys.OrderBy(k => k));
        foreach (var k in a.Equipped.Keys) Assert.Equal(a.Equipped[k], b.Equipped[k]);
        // 保存には列挙を名前で書く（手で読めるように）
        Assert.Contains("\"Mode\": \"Out\"", sim.State.ToJson());
    }

    [Fact]
    public void An_adventure_without_a_save_version_is_kept()
    {
        // 版の数だけが消えた保存（v0.6.0 は SaveVersion を知らない）。冒険を作り直さない
        var sim = new Sim(seed: 5);
        for (int t = 0; t < 2 * 86400; t++) sim.Tick(Profile.Gaming);
        var node = JsonNode.Parse(sim.State.ToJson())!.AsObject();
        Assert.True(node.Remove("SaveVersion"));
        var a = sim.Adv;
        Assert.True(a.Expeditions > 0);

        var s = PartyState.FromJson(node.ToJsonString());
        var b = s.Adventure!;
        Assert.Equal(PartyState.CurrentVersion, s.SaveVersion);
        Assert.Equal(a.Town, b.Town);
        Assert.Equal(a.Gold, b.Gold);
        Assert.Equal(a.Expeditions, b.Expeditions);
        Assert.Equal(a.Inventory, b.Inventory);
        foreach (var k in a.Equipped.Keys) Assert.Equal(a.Equipped[k], b.Equipped[k]);
        // 冒険の無い古い保存には、これまでどおり最初の町から
        Assert.Empty(PartyState.FromJson(TestData.Fixture("state-v0.6.0.json")).Adventure!.Equipped);
    }

    [Fact]
    public void Broken_adventure_values_are_repaired_on_load()
    {
        var node = JsonNode.Parse(TestData.Fixture("state-v0.6.0.json"))!.AsObject();
        node["SaveVersion"] = 1;
        node["Adventure"] = JsonNode.Parse("""
            { "Mode": "Out", "Town": 99, "Hp": 7, "Fatigue": -3, "Gold": -50, "Loot": -1,
              "Inventory": ["", "copper-spatula", "no-such-item"],
              "Equipped": { "cpu#0": ["copper-spatula"], "gpu#0": null },
              "Place": "no-such-place", "Streak": 40 }
            """);
        var s = PartyState.FromJson(node.ToJsonString());
        var sim = new Sim(seed: 2, state: s);   // 冒険の係を作るときに整える
        var a = s.Adventure!;
        Assert.InRange(a.Town, 0, TestData.Book.Towns.Count - 1);
        Assert.InRange(a.Hp, 0.05, 1);
        Assert.Equal(0, a.Fatigue);
        Assert.Equal(0, a.Gold);
        Assert.Equal(0, a.Loot);
        Assert.Equal(5, a.Streak);
        Assert.DoesNotContain("", a.Inventory);
        Assert.Equal(3, a.Equipped["cpu#0"].Length);

        // 行き先が本文に無くても、止まらずに町へ帰ってくる
        for (int t = 0; t < 4 * 3600 && a.Returns == 0; t++) sim.Tick(Profile.Idle);
        Assert.True(a.Returns > 0);
    }

    [Fact]
    public void Garbage_file_does_not_crash_loading()
    {
        Assert.ThrowsAny<Exception>(() => PartyState.FromJson("{ not json"));
        var s = PartyState.FromJson("{}");
        Assert.Equal(PartyState.CurrentVersion, s.SaveVersion);
        Assert.NotNull(s.Adventure);
        var n = PartyState.FromJson("""{ "Adventure": null, "Log": null, "Experience": null }""");
        Assert.NotNull(n.Adventure);
        Assert.NotNull(n.Log);
        Assert.NotNull(n.Experience);
    }

    [Fact]
    public void Story_reset_keeps_gear_but_full_reset_clears_it()
    {
        var sim = new Sim(seed: 9);
        for (int t = 0; t < 2 * 86400; t++) sim.Tick(Profile.Gaming);
        var a = sim.Adv;
        long gold = a.Gold;
        var gear = a.Equipped.ToDictionary(k => k.Key, k => (string[])k.Value.Clone());

        sim.State.ResetStory();
        Assert.Equal(0, sim.State.Chapter);
        Assert.Equal(AdventureMode.Town, a.Mode);
        Assert.Equal(0, a.Town);
        Assert.Equal(gold, a.Gold);
        foreach (var (k, v) in gear) Assert.Equal(v, a.Equipped[k]);

        sim.State.ResetAll();
        Assert.Empty(sim.State.Adventure!.Equipped);
        Assert.Equal(AdventureState.StartingGold, sim.State.Adventure.Gold);
    }
}
