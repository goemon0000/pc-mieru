using System.Security.AccessControl;
using System.Text;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// After the shared open the gate looks at <c>I\update\journal.json</c> and <c>journal.json.tmp</c> (DESIGN-B 4): a journal, a lone temporary or a file that cannot be looked at means an update
/// was left open and has to be recovered before anything runs. The swapper (exclusive) does not look: it reads its own journal.
/// </summary>
public sealed class GateJournalTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private string Update(string exe)
    {
        var update = Scratch.UpdateOf(exe);
        Directory.CreateDirectory(update);
        return update;
    }

    [Fact]
    public void A_journal_means_recovery_is_pending_and_the_gate_is_not_held_so_the_recovery_can_take_it()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{ \"state\": \"swapping\" }");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
        Assert.False(entry.IsOpen);
        Assert.Equal(Path.Combine(Scratch.UpdateOf(exe), GateFile.RecoverNoteName), entry.RecoveryNotePath);
        Assert.False(File.Exists(entry.RecoveryNotePath)); // step 4 writes the note; this step only says where it will be
        using var recovery = InstallGate.EnterExclusive(exe);
        Assert.True(recovery.IsOpen, "the handle of the refused entry was closed: " + recovery.Detail);
    }

    [Fact]
    public void A_lone_temporary_of_a_journal_write_means_recovery_is_pending()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalTempName), "{ \"state\": \"swap");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
        Assert.Contains("journal.json.tmp", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_journal_and_its_temporary_together_mean_recovery_is_pending()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalTempName), "{}");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all \u0000\u0001")]
    [InlineData("{ \"state\": \"done\" }")]
    public void The_content_of_the_journal_is_not_judged_a_file_that_is_there_is_enough(string content)
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), content);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
    }

    [Fact]
    public void An_update_that_is_a_file_and_not_a_folder_means_recovery_is_pending()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Scratch.UpdateOf(exe), "in the way");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
    }

    [Fact]
    public void A_journal_that_cannot_be_looked_at_means_recovery_is_pending_it_is_not_taken_for_no_journal()
    {
        var exe = _scratch.Install();
        var update = Update(exe);
        var journal = Path.Combine(update, GateFile.JournalName);
        File.WriteAllText(journal, "{}");
        // Neither the file's attributes nor the folder's listing may be read: the way "access denied" shows itself to an attribute read (File.Exists would say "no" and let the start go on).
        _scratch.Deny(journal, FileSystemRights.ReadAttributes);
        _scratch.Deny(update, FileSystemRights.ListDirectory);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => File.GetAttributes(journal)); // the seam does what it should
        Assert.False(File.Exists(journal)); // ... and this is exactly what a File.Exists based gate would have believed

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
        Assert.Contains("access denied", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_temporary_that_cannot_be_looked_at_means_recovery_is_pending_too()
    {
        var exe = _scratch.Install();
        var update = Update(exe);
        var temporary = Path.Combine(update, GateFile.JournalTempName);
        File.WriteAllText(temporary, "{");
        _scratch.Deny(temporary, FileSystemRights.ReadAttributes);
        _scratch.Deny(update, FileSystemRights.ListDirectory);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
    }

    [Fact]
    public void A_recovery_that_failed_before_is_told_apart_by_the_marker_in_last_result_json()
    {
        var exe = _scratch.Install();
        var update = Update(exe);
        File.WriteAllText(Path.Combine(update, GateFile.JournalName), "{}");
        File.WriteAllText(Path.Combine(update, GateFile.LastResultName), "{ \"result\": " + GateFile.RecoveryFailedToken + ", \"at\": \"x\" }");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.RecoveryFailed, entry.Outcome);
        Assert.Equal(Path.Combine(update, GateFile.RecoverNoteName), entry.RecoveryNotePath);
        Assert.NotEqual(InstallGate.ReasonOf(GateOutcome.RecoveryPending), entry.Reason);
    }

    [Fact]
    public void A_last_result_without_the_marker_or_with_the_marker_far_beyond_what_is_read_is_only_pending()
    {
        var exe = _scratch.Install();
        var update = Update(exe);
        File.WriteAllText(Path.Combine(update, GateFile.JournalName), "{}");
        File.WriteAllText(Path.Combine(update, GateFile.LastResultName), "{ \"result\": \"ok\" }");
        using (var entry = InstallGate.EnterStartup(exe))
        {
            Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
        }

        var far = new string(' ', GateFile.MarkerReadBytes + 100) + GateFile.RecoveryFailedToken;
        File.WriteAllText(Path.Combine(update, GateFile.LastResultName), far, new UTF8Encoding(false));
        using var beyond = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.RecoveryPending, beyond.Outcome);
    }

    [Fact]
    public void A_last_result_alone_is_only_a_record_it_does_not_stop_the_start()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.LastResultName), "{ \"result\": " + GateFile.RecoveryFailedToken + " }");

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Open, entry.Outcome); // the journal is what says "left open"; once it is gone the transaction is over
    }

    [Fact]
    public void The_swapper_does_not_look_at_the_journal_it_reads_its_own()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalTempName), "{}");

        using var swapper = InstallGate.EnterExclusive(exe);

        Assert.Equal(GateOutcome.Open, swapper.Outcome);
    }

    [Fact]
    public void A_busy_gate_is_busy_first_the_journal_is_only_looked_at_after_the_shared_open_worked()
    {
        var exe = _scratch.Install();
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
        using var swapper = InstallGate.EnterExclusive(exe);

        using var entry = InstallGate.EnterStartup(exe);

        Assert.Equal(GateOutcome.Busy, entry.Outcome);
    }

    [Fact]
    public void When_the_journal_is_gone_the_next_start_opens_normally()
    {
        var exe = _scratch.Install();
        var journal = Path.Combine(Update(exe), GateFile.JournalName);
        File.WriteAllText(journal, "{}");
        using (var pending = InstallGate.EnterStartup(exe))
        {
            Assert.Equal(GateOutcome.RecoveryPending, pending.Outcome);
        }

        File.Delete(journal);

        using var entry = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.Open, entry.Outcome);
    }
}
