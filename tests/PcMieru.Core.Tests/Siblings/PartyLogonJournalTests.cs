using PcMieru.Core.Siblings;
using Xunit;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>
/// C30 item 1: a journal of an interrupted switch that cannot be settled is settled by the owner's own Enable/Disable (the owner's explicit choice wins, the journal is deleted once it was written), so a
/// later recovery cannot undo it. Memory store and a journal file in a folder of its own: the registry is never touched.
/// </summary>
public sealed class PartyLogonJournalTests : IDisposable
{
    private const string OldExe = @"C:\Old\PartsLife.exe";
    private const string NewExe = @"C:\New\PCMieru.Party.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pcmieru-logon-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // a temp folder that stays is not a failure of the test
        }
    }

    private string JournalPath => RunSwitch.JournalPathFor(_folder);

    private static string UniqueLock() => @"Local\PCMieru.Test.PartyLogonJournal." + Guid.NewGuid().ToString("N");

    private static bool Exists(string path) => true;

    /// <summary>A switch that stopped in the middle (the delete of the old Run fails, so does the restore) and whose old entry the user then changed in Windows: the journal stays and says NeedsUserCheck.</summary>
    private MemoryRunStore StuckSwitch()
    {
        var store = new MemoryRunStore();
        store.SeedRun(RunSwitch.OldName, "\"" + OldExe + "\"");
        store.FailOn(RunStoreCall.DeleteRun, new IOException("injected"));
        var switched = RunSwitch.Switch(store, JournalPath, NewExe, Exists);
        Assert.Equal(RunSwitchStatus.RestoreFailed, switched.Status);
        Assert.True(File.Exists(JournalPath));
        store.Heal();
        store.SeedRun(RunSwitch.OldName, "\"C:\\Somewhere\\Else.exe\"");
        Assert.Equal(RunSwitchStatus.NeedsUserCheck, RunSwitch.Recover(store, JournalPath, Exists).Status);
        Assert.True(File.Exists(JournalPath));
        return store;
    }

    [Fact]
    public void Disable_settles_a_journal_that_needs_a_check_and_a_later_recovery_does_nothing()
    {
        var store = StuckSwitch();
        Assert.NotNull(store.ReadRun(PartyLogon.ValueName));

        var result = PartyLogon.Disable(store, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

        Assert.True(result.Ok, result.Failure + " " + result.Detail);
        Assert.Null(store.ReadRun(PartyLogon.ValueName));
        Assert.Null(store.ReadApproval(PartyLogon.ValueName));
        Assert.False(File.Exists(JournalPath));
        Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(store, JournalPath, Exists).Status);
        Assert.Null(store.ReadRun(PartyLogon.ValueName));
        Assert.Equal("\"C:\\Somewhere\\Else.exe\"", store.ReadRun(RunSwitch.OldName)?.Text);
    }

    [Fact]
    public void Enable_settles_a_journal_that_needs_a_check_and_a_later_recovery_does_not_undo_it()
    {
        var store = StuckSwitch();

        var result = PartyLogon.Enable(store, NewExe, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

        Assert.True(result.Ok, result.Failure + " " + result.Detail);
        Assert.Equal(PartyLogon.ExpectedValue(NewExe), store.ReadRun(PartyLogon.ValueName)?.Text);
        Assert.False(File.Exists(JournalPath));
        Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(store, JournalPath, Exists).Status);
        Assert.Equal(PartyLogon.ExpectedValue(NewExe), store.ReadRun(PartyLogon.ValueName)?.Text);
        Assert.Equal(PartyLogonState.On, PartyLogon.Read(store, NewExe));
    }

    [Fact]
    public void A_change_that_fails_leaves_the_journal_for_the_next_try()
    {
        var store = StuckSwitch();
        store.DenyOn(RunStoreCall.DeleteRun);

        var result = PartyLogon.Disable(store, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

        // the journal goes first (an owner's change is never written next to a journal that could roll it back): the change failed, so the journal is gone and the values are as the owner left them
        Assert.False(result.Ok);
        Assert.False(File.Exists(JournalPath));
        Assert.NotNull(store.ReadRun(PartyLogon.ValueName));
    }

    // ---- C31 item 6: an unreadable journal stops the change; an unsettled one is deleted before anything is written ----

    [Fact]
    public void A_journal_that_is_there_but_cannot_be_read_stops_the_change_and_nothing_is_written()
    {
        var store = new MemoryRunStore();
        store.SeedRun(PartyLogon.ValueName, PartyLogon.ExpectedValue(NewExe));
        Directory.CreateDirectory(JournalPath);   // opening a folder as a file: UnauthorizedAccessException, not "not found"
        var calls = store.Calls.Count;

        var disabled = PartyLogon.Disable(store, UniqueLock(), journalPath: JournalPath, fileExists: Exists);
        var enabled = PartyLogon.Enable(store, NewExe, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

        Assert.False(disabled.Ok);
        Assert.Equal(PartyLogonFailure.JournalUnreadable, disabled.Failure);
        Assert.False(enabled.Ok);
        Assert.Equal(PartyLogonFailure.JournalUnreadable, enabled.Failure);
        Assert.Equal(calls, store.Calls.Count);   // the store was not touched at all
        Assert.NotNull(store.ReadRun(PartyLogon.ValueName));
        Assert.True(Directory.Exists(JournalPath));
    }

    [Fact]
    public void An_unsettled_journal_that_cannot_be_deleted_stops_the_change_before_it_is_written()
    {
        var store = StuckSwitch();
        var calls = store.Calls.Count;

        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))   // readable, but not deletable
        {
            var result = PartyLogon.Disable(store, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

            Assert.False(result.Ok);
            Assert.Equal(PartyLogonFailure.JournalUnreadable, result.Failure);
        }

        Assert.NotNull(store.ReadRun(PartyLogon.ValueName));   // the change was not written
        Assert.True(File.Exists(JournalPath));
        Assert.DoesNotContain(store.Calls.Skip(calls), c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
    }

    [Fact]
    public void An_unsettled_journal_is_gone_before_the_owners_change_is_written()
    {
        var store = StuckSwitch();
        var steps = new List<string>();
        var watched = new JournalWatchStore(store, () => steps.Add(File.Exists(JournalPath) ? "write-with-journal" : "write-without-journal"));

        var result = PartyLogon.Disable(watched, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

        Assert.True(result.Ok, result.Failure + " " + result.Detail);
        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.Equal("write-without-journal", step));
    }

    private sealed class JournalWatchStore(MemoryRunStore inner, Action onWrite) : IRunStore
    {
        public RunValue? ReadRun(string name) => inner.ReadRun(name);

        public void WriteRun(string name, string text, Microsoft.Win32.RegistryValueKind kind = Microsoft.Win32.RegistryValueKind.String) { onWrite(); inner.WriteRun(name, text, kind); }

        public void DeleteRun(string name) { onWrite(); inner.DeleteRun(name); }

        public ApprovalValue? ReadApproval(string name) => inner.ReadApproval(name);

        public void WriteApproval(string name, byte[] bytes, Microsoft.Win32.RegistryValueKind kind = Microsoft.Win32.RegistryValueKind.Binary) { onWrite(); inner.WriteApproval(name, bytes, kind); }

        public void DeleteApproval(string name) { onWrite(); inner.DeleteApproval(name); }

        public void Flush() => inner.Flush();
    }

    [Fact]
    public void A_journal_whose_values_are_known_is_recovered_first_and_is_gone_afterwards()
    {
        var store = new MemoryRunStore();
        store.SeedRun(RunSwitch.OldName, "\"" + OldExe + "\"");
        store.FailOn(RunStoreCall.DeleteRun, new IOException("injected"));
        Assert.Equal(RunSwitchStatus.RestoreFailed, RunSwitch.Switch(store, JournalPath, NewExe, Exists).Status);
        store.Heal();

        // the values are the ones the switch itself leaves: the recovery puts them back, then the owner's change runs
        var result = PartyLogon.Disable(store, UniqueLock(), journalPath: JournalPath, fileExists: Exists);

        Assert.True(result.Ok, result.Failure + " " + result.Detail);
        Assert.False(File.Exists(JournalPath));
        Assert.Null(store.ReadRun(PartyLogon.ValueName));
        Assert.Equal("\"" + OldExe + "\"", store.ReadRun(RunSwitch.OldName)?.Text);
    }

    [Fact]
    public void With_no_journal_path_or_no_journal_it_is_a_plain_change()
    {
        var store = new MemoryRunStore();
        Assert.True(PartyLogon.Enable(store, NewExe, UniqueLock()).Ok);
        Assert.True(PartyLogon.Enable(store, NewExe, UniqueLock(), journalPath: JournalPath, fileExists: Exists).Ok);
        Assert.True(PartyLogon.Disable(store, UniqueLock(), journalPath: JournalPath, fileExists: Exists).Ok);
        Assert.False(File.Exists(JournalPath));
        Assert.Null(store.ReadRun(PartyLogon.ValueName));
    }
}
