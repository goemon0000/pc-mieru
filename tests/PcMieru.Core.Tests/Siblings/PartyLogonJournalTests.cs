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

        Assert.False(result.Ok);
        Assert.True(File.Exists(JournalPath));
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
