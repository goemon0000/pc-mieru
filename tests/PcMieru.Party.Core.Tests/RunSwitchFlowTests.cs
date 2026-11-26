using PcMieru.Core.Siblings;
using PcMieru.Party.Import;
using Xunit;

namespace PcMieru.Party.Core.Tests;

/// <summary>DESIGN-C 4.3, Party side: asked only after an import, only for an enabled old entry; a failure shows the notice only when a person must look. Memory store, temp folder, scripted prompts.</summary>
public sealed class RunSwitchFlowTests : IDisposable
{
    private const string NewExe = @"C:\New\PCMieru.Party.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcmieru-switchflow-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldExe;

    public RunSwitchFlowTests()
    {
        Directory.CreateDirectory(_dir);
        _oldExe = Path.Combine(_dir, "PartsLife.exe");
        File.WriteAllText(_oldExe, "x");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Script(bool answer) : IRunSwitchPrompts
    {
        public int Asked { get; private set; }
        public int Notices { get; private set; }

        public bool AskSwitch()
        {
            Asked++;
            return answer;
        }

        public void ShowCheckNotice() => Notices++;
    }

    private MemoryRunStore Store()
    {
        var store = new MemoryRunStore();
        store.SeedRun(RunSwitch.OldName, "\"" + _oldExe + "\"");
        return store;
    }

    private static FirstStartReport Imported(bool imported = true) => new() { Imported = imported };

    [Fact]
    public void Nothing_is_asked_when_this_run_did_not_import()
    {
        var store = Store();
        var prompts = new Script(true);
        Assert.Equal(RunSwitchFlowEnd.NotAsked, RunSwitchFlow.AfterImport(Imported(false), store, _dir, NewExe, prompts));
        Assert.Equal(0, prompts.Asked);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public void Nothing_is_asked_when_the_old_entry_is_not_on_or_is_not_there()
    {
        var disabled = Store();
        disabled.SeedApproval(RunSwitch.OldName, PcMieru.Wire.StartupApprovedCodec.EncodeDisabled(1));
        var prompts = new Script(true);
        Assert.Equal(RunSwitchFlowEnd.NotAsked, RunSwitchFlow.AfterImport(Imported(), disabled, _dir, NewExe, prompts));
        Assert.Equal(RunSwitchFlowEnd.NotAsked, RunSwitchFlow.AfterImport(Imported(), new MemoryRunStore(), _dir, NewExe, prompts));
        Assert.Equal(0, prompts.Asked);
    }

    [Fact]
    public void Not_now_writes_nothing()
    {
        var store = Store();
        var prompts = new Script(false);
        Assert.Equal(RunSwitchFlowEnd.Declined, RunSwitchFlow.AfterImport(Imported(), store, _dir, NewExe, prompts));
        Assert.Equal(1, prompts.Asked);
        Assert.DoesNotContain(store.Calls, c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
        Assert.False(File.Exists(RunSwitch.JournalPathFor(_dir)));
    }

    [Fact]
    public void Yes_switches_and_says_nothing_more()
    {
        var store = Store();
        var prompts = new Script(true);
        Assert.Equal(RunSwitchFlowEnd.Switched, RunSwitchFlow.AfterImport(Imported(), store, _dir, NewExe, prompts));
        Assert.Null(store.ReadRun(RunSwitch.OldName));
        Assert.Equal(PartyLogon.ExpectedValue(NewExe), store.ReadRun(PartyLogon.ValueName)?.Text);
        Assert.Equal(0, prompts.Notices);
        Assert.False(File.Exists(RunSwitch.JournalPathFor(_dir)));
    }

    [Fact]
    public void A_clean_rollback_says_nothing()
    {
        var store = Store();
        store.FailOn(RunStoreCall.ReadApproval, new IOException("x"));   // the offer already fails on this: nothing is asked
        var prompts = new Script(true);
        Assert.Equal(RunSwitchFlowEnd.NotAsked, RunSwitchFlow.AfterImport(Imported(), store, _dir, NewExe, prompts));
        Assert.Equal(0, prompts.Notices);
    }

    [Fact]
    public void A_restore_that_fails_shows_the_notice_and_the_next_start_puts_everything_back()
    {
        var store = Store();
        var prompts = new Script(true);
        store.DenyOn(RunStoreCall.WriteRun);   // the new value cannot be written, and neither can the old one be put back
        Assert.Equal(RunSwitchFlowEnd.Notified, RunSwitchFlow.AfterImport(Imported(), store, _dir, NewExe, prompts));
        Assert.Equal(1, prompts.Notices);
        Assert.True(File.Exists(RunSwitch.JournalPathFor(_dir)));

        store.Heal();
        var later = new Script(true);
        Assert.False(RunSwitchFlow.RecoverAtStart(store, _dir, later));
        Assert.Equal(0, later.Notices);
        Assert.False(File.Exists(RunSwitch.JournalPathFor(_dir)));
        Assert.Equal("\"" + _oldExe + "\"", store.ReadRun(RunSwitch.OldName)?.Text);
        Assert.Null(store.ReadRun(PartyLogon.ValueName));
    }

    [Fact]
    public void Recovery_after_a_change_by_the_user_shows_the_notice_and_writes_nothing()
    {
        var store = Store();
        store.DenyOn(RunStoreCall.WriteRun);
        RunSwitchFlow.AfterImport(Imported(), store, _dir, NewExe, new Script(true));
        store.Heal();
        store.SeedRun(RunSwitch.OldName, "\"C:\\Else\\Other.exe\"");
        var seen = store.Calls.Count;

        var prompts = new Script(true);
        Assert.True(RunSwitchFlow.RecoverAtStart(store, _dir, prompts));
        Assert.Equal(1, prompts.Notices);
        Assert.DoesNotContain(store.Calls.Skip(seen), c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
        Assert.True(File.Exists(RunSwitch.JournalPathFor(_dir)));
    }

    [Fact]
    public void Recovery_with_no_journal_does_nothing()
    {
        var store = Store();
        Assert.False(RunSwitchFlow.RecoverAtStart(store, _dir, new Script(true)));
        Assert.Empty(store.Calls);
    }
}
