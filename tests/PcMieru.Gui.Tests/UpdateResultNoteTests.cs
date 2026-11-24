using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Gui.Tests;

public sealed class UpdateResultNoteTests
{
    internal const string Before = "00000000-0000-0000-0000-000000000001";
    internal const string After = "00000000-0000-0000-0000-000000000002";

    [Theory]
    [InlineData(Language.Ja, "committed", false, "1.2.4 に更新しました。")]
    [InlineData(Language.En, "committed", false, "Updated to 1.2.4.")]
    [InlineData(Language.Ja, "rolledBack", true, "1.2.4 は起動を確かめられなかったため、元の 1.2.3 に戻しました。この版は今後入れません。")]
    [InlineData(Language.En, "rolledBack", true, "1.2.4 could not be started, so PC Mieru went back to 1.2.3. This version will not be installed again.")]
    [InlineData(Language.Ja, "rolledBack", false, "1.2.4 に更新できなかったため、元の 1.2.3 に戻しました。あとでもう一度試します。")]
    [InlineData(Language.En, "rolledBack", false, "1.2.4 could not be installed, so PC Mieru went back to 1.2.3. It will try again later.")]
    public void Result_text_matches_the_outcome_and_actual_blocked_build(Language language, string outcome, bool blocked, string expected)
    {
        using var locale = Harness.Locale(language);
        using var folder = new UpdateTestFolder();
        if (blocked)
        {
            Block(folder.Paths, After);
        }
        else if (outcome == "rolledBack")
        {
            Block(folder.Paths, Before);
        }

        var reason = outcome == "committed" ? "none" : blocked ? "launchFailed" : "swapIoFailure";
        File.WriteAllText(folder.Paths.LastResult, $$"""
            { "format": "pcm-last-result/1", "kind": "apply", "utc": "2026-11-25T01:02:03Z", "tx": "0123456789ab",
              "outcome": "{{outcome}}", "reason": "{{reason}}", "detail": "",
              "from": { "version": "1.2.3", "buildId": "{{Before}}" },
              "to": { "version": "1.2.4", "buildId": "{{After}}" } }
            """);
        Assert.Equal(expected, UpdateResultNote.Read(folder.Paths.Install)?.Text);
    }

    [Theory]
    [InlineData(Language.Ja, true, "AI アプリが pcmi を使っているため、更新を待っています。")]
    [InlineData(Language.En, true, "Waiting to update because an AI app is using pcmi.")]
    [InlineData(Language.Ja, false, "ほかの PCミエルが開いているため、更新を待っています。")]
    [InlineData(Language.En, false, "Waiting to update because another PC Mieru is open.")]
    public void Deferred_text_is_localized(Language language, bool pcmi, string expected)
    {
        using var locale = Harness.Locale(language);
        Assert.Equal(expected, new UpdateDeferral(pcmi ? GateHolders.Pcmi : GateHolders.Other).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("""{"format":"pcm-last-result/1","outcome":"committed"}""")]
    public void Missing_or_broken_result_is_silent(string? contents)
    {
        using var folder = new UpdateTestFolder();
        if (contents is not null)
        {
            File.WriteAllText(folder.Paths.LastResult, contents);
        }

        Assert.Null(UpdateResultNote.Read(folder.Paths.Install));
    }

    [Fact]
    public void Blocked_versions_are_given_to_the_checker_as_release_tags()
    {
        using var folder = new UpdateTestFolder();
        Block(folder.Paths, After);
        var options = UpdateComposition.OptionsFor(new AppVersion(1, 2, 3), folder.Paths.Install, folder.Paths.Install);
        Assert.Equal(["v1.2.4"], options.BlockedTags);
    }

    private static void Block(TxPaths paths, string build) => File.WriteAllText(paths.Blocked, $$"""
        { "format": "pcm-blocked/1", "tags": [
            { "version": "1.2.4", "buildId": "{{build}}", "reason": "launch-failed", "utc": "2026-11-25T01:02:03Z" }
        ] }
        """);
}

internal sealed class UpdateTestFolder : IDisposable
{
    public UpdateTestFolder()
    {
        var root = Environment.GetEnvironmentVariable("PCM_TEST_TMP") ?? Path.Combine(Harness.Root, ".testtmp");
        Paths = new TxPaths(Path.Combine(root, "gui-update-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.Update);
    }

    public TxPaths Paths { get; }

    public void Dispose() => Directory.Delete(Paths.Install, recursive: true);
}
