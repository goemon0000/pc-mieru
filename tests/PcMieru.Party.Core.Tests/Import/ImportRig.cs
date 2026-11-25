using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Party.Adventure;
using PcMieru.Party.Game;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>Shared helpers of the importer tests: build sources, read the converted bytes back, compare states field by field.</summary>
internal static class ImportRig
{
    public static JsonObject Fixture(string name) => JsonNode.Parse(TestData.Fixture(name))!.AsObject();

    /// <summary>A real v0.3 save (cpu, cpu-cooler and gpu still carry the one-machine names; no Adventure).</summary>
    public static JsonObject V03() => Fixture("state-v0.3.json");

    /// <summary>A real v0.6.0 save: numbered keys, no SaveVersion, no Adventure.</summary>
    public static JsonObject V060() => Fixture("state-v0.6.0.json");

    /// <summary>A v1 save with a mid-expedition adventure, gear in three slots, display settings and an UpdateMode.</summary>
    public static JsonObject V1() => Fixture("state-v1.json");

    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    public static ImportVerdict Inspect(JsonNode node) => StateImport.Inspect(Bytes(node));

    public static ImportVerdict Inspect(string json) => StateImport.Inspect(Encoding.UTF8.GetBytes(json));

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The converted bytes read the way the new app will read them.</summary>
    public static PartyState Read(ImportVerdict v)
    {
        Assert.NotNull(v.Converted);
        return PartyState.FromJson(new UTF8Encoding(false, true).GetString(v.Converted!));
    }

    public static JsonObject ConvertedJson(ImportVerdict v)
    {
        Assert.NotNull(v.Converted);
        return JsonNode.Parse(v.Converted!)!.AsObject();
    }

    /// <summary>A minimal source that passes every hold rule; tests add the thing they are about.</summary>
    public static JsonObject Minimal(int? saveVersion = 1)
    {
        var o = new JsonObject
        {
            ["Experience"] = new JsonObject { ["memory"] = 100.5 },
            ["Journey"] = 10.0,
            ["Chapter"] = 1,
            ["Scene"] = 2,
        };
        if (saveVersion is int v) o["SaveVersion"] = v;
        return o;
    }

    /// <summary>
    /// Every carried field, one by one. <paramref name="expected"/> is what the old app had in memory (or a literal built by the test);
    /// <paramref name="actual"/> is the converted bytes read back.
    /// </summary>
    public static void AssertSameProgress(PartyState expected, PartyState actual)
    {
        Assert.Equal(expected.Experience.OrderBy(k => k.Key, StringComparer.Ordinal), actual.Experience.OrderBy(k => k.Key, StringComparer.Ordinal));
        Assert.Equal(expected.Journey, actual.Journey);
        Assert.Equal(expected.Chapter, actual.Chapter);
        Assert.Equal(expected.Scene, actual.Scene);
        Assert.Equal(expected.Log, actual.Log);
        Assert.Equal(expected.LastEventAt.OrderBy(k => k.Key, StringComparer.Ordinal), actual.LastEventAt.OrderBy(k => k.Key, StringComparer.Ordinal));
        Assert.Equal(expected.RecentVignettes, actual.RecentVignettes);
        Assert.Equal(expected.UptimeSeconds, actual.UptimeSeconds);
        Assert.Equal(expected.LastSeenUnix, actual.LastSeenUnix);
        Assert.Equal(expected.Boots, actual.Boots);

        Assert.Equal(expected.Lang, actual.Lang);
        Assert.Equal(expected.Zoom, actual.Zoom);
        Assert.Equal(expected.WindowLeft, actual.WindowLeft);
        Assert.Equal(expected.WindowTop, actual.WindowTop);
        Assert.Equal(expected.Topmost, actual.Topmost);
        Assert.Equal(expected.Expanded, actual.Expanded);
        Assert.Equal(expected.Labels, actual.Labels);

        Assert.Equal(PartyState.CurrentVersion, actual.SaveVersion);
        AssertSameAdventure(expected.Adventure!, actual.Adventure!);
    }

    public static void AssertSameAdventure(AdventureState e, AdventureState a)
    {
        Assert.Equal(e.Mode, a.Mode);
        Assert.Equal(e.Phase, a.Phase);
        Assert.Equal(e.Town, a.Town);
        Assert.Equal(e.Place, a.Place);
        Assert.Equal(e.LastPlace, a.LastPlace);
        Assert.Equal(e.Depth, a.Depth);
        Assert.Equal(e.Planned, a.Planned);
        Assert.Equal(e.BackSteps, a.BackSteps);
        Assert.Equal(e.Pending, a.Pending);
        Assert.Equal(e.Hp, a.Hp);
        Assert.Equal(e.Fatigue, a.Fatigue);
        Assert.Equal(e.Streak, a.Streak);
        Assert.Equal(e.Gold, a.Gold);
        Assert.Equal(e.Loot, a.Loot);
        Assert.Equal(e.Inventory, a.Inventory);
        Assert.Equal(e.Equipped.Keys.OrderBy(k => k, StringComparer.Ordinal), a.Equipped.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var k in e.Equipped.Keys) Assert.Equal(e.Equipped[k], a.Equipped[k]);
        Assert.Equal(e.Progress, a.Progress);
        Assert.Equal(e.Rests, a.Rests);
        Assert.Equal(e.Bought, a.Bought);
        Assert.Equal(e.Stance, a.Stance);
        Assert.Equal(e.Expeditions, a.Expeditions);
        Assert.Equal(e.Returns, a.Returns);
        Assert.Equal(e.Wins, a.Wins);
        Assert.Equal(e.Losses, a.Losses);
    }

    public static void AssertHold(ImportVerdict v, string reason, byte[]? source = null)
    {
        Assert.Equal(ImportStatus.Hold, v.Status);
        Assert.Equal("hold", v.Code);
        Assert.Equal(reason, v.Reason);
        Assert.Null(v.Converted);
        Assert.Matches("^[0-9a-f]{64}$", v.SourceSha256);
        if (source is not null) Assert.Equal(Sha(source), v.SourceSha256);
    }
}
