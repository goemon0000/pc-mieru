using System.Text.Json.Nodes;
using PcMieru.Party.Adventure;
using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 壊れた保存・新しい版の保存を読んでも、**物語の進みとレベルが消えない**こと。
/// 以前は冒険の一欄が読めないだけで空の進みが返り、60秒後の保存で state.json が上書きされていた。
/// </summary>
public sealed class SaveRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partslife-test-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "state.json");

    public SaveRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static JsonObject V060() => JsonNode.Parse(TestData.Fixture("state-v0.6.0.json"))!.AsObject();

    /// <summary>v0.6.0 の保存に、冒険の欄を足した物。town と expeditions は読めているかの目印。</summary>
    private static string WithAdventure(string probe, bool versioned = true)
    {
        var node = V060();
        if (versioned) node["SaveVersion"] = 1;
        node["Adventure"] = JsonNode.Parse("{ \"Town\": 3, \"Expeditions\": 7, " + probe + " }");
        return node.ToJsonString();
    }

    private static void AssertStoryAndLevelsKept(PartyState s)
    {
        var before = V060();
        Assert.Equal(30512.75, s.Journey);
        Assert.Equal(13, s.Chapter);
        Assert.Equal(4, s.Scene);
        Assert.Equal(41, s.Boots);
        Assert.Equal(2, s.Log.Count);
        foreach (var (key, node) in before["Experience"]!.AsObject())
            Assert.Equal(node!.GetValue<double>(), s.Experience[key]);
    }

    public static TheoryData<string> Probes => new()
    {
        "\"Mode\": \"Ship\"",
        "\"Phase\": \"Forge\"",
        "\"Stance\": \"Reckless\"",
        "\"Gold\": 1.5",
        "\"Hp\": null",
        "\"Equipped\": { \"cpu#0\": \"x\", \"gpu#0\": [\"glowing-chip\", \"\", \"\"] }",
    };

    [Theory]
    [MemberData(nameof(Probes))]
    public void One_unreadable_adventure_field_keeps_story_levels_and_the_rest_of_the_adventure(string probe)
    {
        foreach (bool versioned in new[] { true, false })
        {
            var s = PartyState.FromJson(WithAdventure(probe, versioned));
            AssertStoryAndLevelsKept(s);
            var a = s.Adventure!;
            Assert.Equal(3, a.Town);
            Assert.Equal(7, a.Expeditions);
            Assert.True(Enum.IsDefined(a.Mode));
            Assert.True(Enum.IsDefined(a.Phase));
            Assert.True(Enum.IsDefined(a.Stance));

            // 冒険の係を作って回しても止まらない（読み直した値が動ける範囲にある）
            var sim = new Sim(seed: 4, state: s);
            for (int t = 0; t < 3600; t++) sim.Tick(Profile.Office);
            AssertStoryAndLevelsKeptOrGrown(s);
        }
    }

    private static void AssertStoryAndLevelsKeptOrGrown(PartyState s)
    {
        foreach (var (key, node) in V060()["Experience"]!.AsObject())
            Assert.True(s.Experience[key] >= node!.GetValue<double>());
        Assert.True(s.Chapter >= 13);
    }

    [Fact]
    public void Unknown_enum_names_map_to_safe_defaults()
    {
        var a = PartyState.FromJson(WithAdventure(
            "\"Mode\": \"Ship\", \"Phase\": \"Forge\", \"Stance\": \"Reckless\", \"Pending\": 99")).Adventure!;
        Assert.Equal(AdventureMode.Town, a.Mode);
        Assert.Equal(TownPhase.Arrive, a.Phase);
        Assert.Equal(Stance.Balanced, a.Stance);
        Assert.Equal(Pending.None, a.Pending);
        // 名前でも数でも、知っている値はそのまま読む（構えは以前、数で書いていた）
        var b = PartyState.FromJson(WithAdventure("\"Mode\": \"out\", \"Stance\": 2")).Adventure!;
        Assert.Equal(AdventureMode.Out, b.Mode);
        Assert.Equal(Stance.Bold, b.Stance);
    }

    [Fact]
    public void Repairs_are_local_to_the_broken_field()
    {
        var s = PartyState.FromJson(WithAdventure("\"Gold\": 1.5, \"Hp\": null"), out bool repaired);
        Assert.True(repaired);
        Assert.Equal(1, s.Adventure!.Gold);   // 小数は切り捨てて使う
        Assert.Equal(1, s.Adventure.Hp);      // null は既定値

        var e = PartyState.FromJson(WithAdventure("\"Equipped\": { \"cpu#0\": \"x\", \"gpu#0\": [\"glowing-chip\", 5, \"\"] }"));
        Assert.False(e.Adventure!.Equipped.ContainsKey("cpu#0"));                 // 読めない者だけ落ちる
        Assert.Equal("glowing-chip", e.Adventure.Equipped["gpu#0"][0]);            // 位置は保つ
        Assert.Equal(3, e.Adventure.Equipped["gpu#0"].Length);

        // 本筋の側の一欄が読めなくても、その欄だけ
        var node = V060();
        node["Boots"] = "many";
        var c = PartyState.FromJson(node.ToJsonString());
        Assert.Equal(0, c.Boots);
        Assert.Equal(30512.75, c.Journey);
        Assert.Equal(98765.4, c.Experience["cpu#0"]);
    }

    [Fact]
    public void A_save_from_a_newer_version_loads_and_keeps_what_it_does_not_understand()
    {
        var node = V060();
        node["SaveVersion"] = PartyState.CurrentVersion + 6;
        node["Pets"] = JsonNode.Parse("""{ "cat": { "name": "tabby" } }""");
        node["Adventure"] = JsonNode.Parse("""
            { "Mode": "Ship", "Town": 2, "Quests": [1, 2, 3],
              "Inventory": [{ "id": "x" }, "copper-spatula"], "Gold": "lots" }
            """);
        var s = PartyState.FromJson(node.ToJsonString());

        AssertStoryAndLevelsKept(s);
        Assert.Equal(PartyState.CurrentVersion + 6, s.SaveVersion);   // 版の数は下げない
        Assert.Equal(AdventureMode.Town, s.Adventure!.Mode);
        Assert.Equal(2, s.Adventure.Town);
        Assert.Equal(new[] { "copper-spatula" }, s.Adventure.Inventory);
        Assert.Equal(AdventureState.StartingGold, s.Adventure.Gold);

        // 分からない欄は書き戻す。新しい版に戻したとき、そちらの進みが残っている
        var again = JsonNode.Parse(s.ToJson())!.AsObject();
        Assert.Equal("tabby", (string?)again["Pets"]!["cat"]!["name"]);
        Assert.Equal(3, again["Adventure"]!["Quests"]!.AsArray().Count);
        var sim = new Sim(seed: 6, state: s);
        for (int t = 0; t < 3600; t++) sim.Tick(Profile.Idle);
    }

    [Fact]
    public void A_garbage_file_is_set_aside_not_overwritten()
    {
        File.WriteAllText(StatePath, "{ \"Journey\": 30512.75, \"Experience\": { \"cpu#0\": 98");   // 途中で切れた
        var s = PartyState.Load(_dir);

        Assert.Equal(0, s.Journey);
        Assert.False(s.SaveBlocked);
        Assert.False(File.Exists(StatePath));
        var bad = Assert.Single(Directory.GetFiles(_dir, "state.json.bad-*"));
        Assert.Contains("30512.75", File.ReadAllText(bad));

        s.Save();
        Assert.True(File.Exists(StatePath));
        Assert.Contains("30512.75", File.ReadAllText(bad));   // 控えはそのまま
    }

    [Fact]
    public void Only_the_newest_three_bad_copies_are_kept()
    {
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(StatePath, "{ broken " + i);
            PartyState.Load(_dir);
        }
        var left = Directory.GetFiles(_dir, "state.json.bad-*").Select(File.ReadAllText).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "{ broken 3", "{ broken 4", "{ broken 5" }, left);
    }

    [Fact]
    public void A_repaired_load_keeps_the_original_text_as_a_copy()
    {
        File.WriteAllText(StatePath, WithAdventure("\"Gold\": 1.5"));
        var s = PartyState.Load(_dir);
        AssertStoryAndLevelsKept(s);
        Assert.True(File.Exists(StatePath));
        var copy = Assert.Single(Directory.GetFiles(_dir, "state.json.bad-*"));
        Assert.Contains("1.5", File.ReadAllText(copy));

        s.Save();
        var reread = PartyState.Load(_dir);
        AssertStoryAndLevelsKept(reread);
        Assert.Equal(1, reread.Adventure!.Gold);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_never_overwritten()
    {
        string original = TestData.Fixture("state-v0.6.0.json");
        File.WriteAllText(StatePath, original);
        PartyState s;
        using (new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            s = PartyState.Load(_dir);
        Assert.True(s.SaveBlocked);
        s.Save();
        Assert.Equal(original, File.ReadAllText(StatePath));
    }

    [Fact]
    public void A_good_save_round_trips_through_the_disk()
    {
        File.WriteAllText(StatePath, TestData.Fixture("state-v0.6.0.json"));
        var s = PartyState.Load(_dir);
        AssertStoryAndLevelsKept(s);
        Assert.Empty(Directory.GetFiles(_dir, "state.json.bad-*"));
        s.Save();
        AssertStoryAndLevelsKept(PartyState.Load(_dir));
    }
}
