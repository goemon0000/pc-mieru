using System.Text.Json.Nodes;
using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// 保存を**読めなかったとき**に、空の進みで上書きしないこと（codex 3・4・5）。
/// 「無い」と決めるのは FileNotFound / DirectoryNotFound だけ。それ以外の失敗（権限・共有違反）は、最初から始めるが保存は止める。
/// </summary>
public sealed class SaveLoadSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "party-loadsafe-test-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "state.json");

    public SaveLoadSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>一欄だけ読めない（Gold が小数）保存。読めた欄は使い、直す前の本文を控えに写す形になる。</summary>
    private static string PartlyBroken()
    {
        var node = JsonNode.Parse(TestData.Fixture("state-v0.6.0.json"))!.AsObject();
        node["SaveVersion"] = 1;
        node["Adventure"] = JsonNode.Parse("{ \"Town\": 3, \"Expeditions\": 7, \"Gold\": 1.5 }");
        return node.ToJsonString();
    }

    [Fact]
    public void A_missing_file_is_a_fresh_start_that_may_save()
    {
        var s = PartyState.Load(_dir);
        Assert.False(s.SaveBlocked);
        Assert.Equal(0, s.Journey);
    }

    [Fact]
    public void A_missing_folder_is_a_fresh_start_that_may_save()
    {
        var s = PartyState.Load(Path.Combine(_dir, "not", "there"));
        Assert.False(s.SaveBlocked);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_blocks_saving_even_when_File_Exists_says_no()
    {
        // 「state.json」が実は読めないもの（ここでは同名のフォルダ）。File.Exists は偽を返すが、開こうとすると FileNotFound ではなく UnauthorizedAccess
        Directory.CreateDirectory(StatePath);
        Assert.False(File.Exists(StatePath));

        var s = PartyState.Load(_dir);

        Assert.True(s.SaveBlocked);
        Assert.Equal(0, s.Journey);
    }

    [Fact]
    public void A_file_held_open_with_no_sharing_blocks_saving_and_is_not_overwritten()
    {
        File.WriteAllText(StatePath, TestData.Fixture("state-v0.6.0.json"));
        string original = File.ReadAllText(StatePath);
        PartyState s;
        using (new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            s = PartyState.Load(_dir);

        Assert.True(s.SaveBlocked);
        s.Save();
        Assert.Equal(original, File.ReadAllText(StatePath));
    }

    [Fact]
    public void A_legacy_file_that_cannot_be_read_blocks_saving_but_a_missing_one_does_not()
    {
        string legacy = Path.Combine(_dir, "old", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);

        Assert.False(PartyState.Load(_dir, legacy).SaveBlocked);   // どちらも無い

        File.WriteAllText(legacy, TestData.Fixture("state-v0.6.0.json"));
        PartyState held;
        using (new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            held = PartyState.Load(_dir, legacy);
        Assert.True(held.SaveBlocked);

        var read = PartyState.Load(_dir, legacy);   // 読めれば引き継ぐ
        Assert.False(read.SaveBlocked);
        Assert.Equal(30512.75, read.Journey);
    }

    [Fact]
    public void A_repaired_save_whose_copy_cannot_be_made_blocks_saving()
    {
        File.WriteAllText(StatePath, PartlyBroken());

        var s = PartyState.Load(_dir, null, (path, move) => false);

        Assert.True(s.SaveBlocked);
        Assert.Equal(30512.75, s.Journey);   // 読めた欄は使う
        s.Save();
        Assert.Contains("1.5", File.ReadAllText(StatePath));   // 元の本文は残っている
    }

    [Fact]
    public void A_repaired_save_whose_copy_was_made_may_save()
    {
        File.WriteAllText(StatePath, PartlyBroken());

        var s = PartyState.Load(_dir);

        Assert.False(s.SaveBlocked);
        Assert.Single(Directory.GetFiles(_dir, "state.json.bad-*"));
    }

    [Fact]
    public void A_wholly_unreadable_save_whose_set_aside_fails_blocks_saving()
    {
        File.WriteAllText(StatePath, "{ broken");

        var s = PartyState.Load(_dir, null, (path, move) => false);

        Assert.True(s.SaveBlocked);
        s.Save();
        Assert.Equal("{ broken", File.ReadAllText(StatePath));
    }

    [Fact]
    public void A_blocked_writer_says_blocked_to_Submit_Flush_and_Last_and_writes_nothing()
    {
        var writer = new PartySaveWriter(saveBlocked: true);

        var submitted = writer.Submit("{}", StatePath);
        var flushed = writer.Flush(TimeSpan.FromSeconds(10));

        Assert.True(submitted.Blocked);
        Assert.False(submitted.Ok);
        Assert.True(flushed.Blocked);
        Assert.False(flushed.Ok);
        Assert.True(writer.Last.Blocked);
        Assert.True(writer.Blocked);
        Assert.Null(writer.LastSavedUtc);
        Assert.False(File.Exists(StatePath));
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void A_working_writer_is_never_reported_as_blocked()
    {
        var writer = new PartySaveWriter(saveBlocked: false);

        var submitted = writer.Submit("{}", StatePath);
        var flushed = writer.Flush(TimeSpan.FromSeconds(10));

        Assert.True(submitted.Ok);
        Assert.False(submitted.Blocked);
        Assert.True(flushed.Ok);
        Assert.False(flushed.Blocked);
    }
}
