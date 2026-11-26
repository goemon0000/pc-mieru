using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using PcMieru.Core.Siblings;
using PcMieru.Wire;
using Xunit;

namespace PcMieru.Core.Tests.Siblings;

/// <summary>
/// DESIGN-C 4.3 / 8 "Run": the offer rows, the switch with a failure at every store call, the recovery from each phase, a user change, a journal that cannot be written. Memory store and memory journal only:
/// nothing here reaches the registry (the one file test uses a temp folder of its own).
/// </summary>
public sealed class RunSwitchTests
{
    private const string OldExe = @"C:\Old\PartsLife.exe";
    private const string NewExe = @"C:\New\PCMieru.Party.exe";
    private static readonly string OldCommand = "\"" + OldExe + "\"";
    private static readonly string NewCommand = PartyLogon.ExpectedValue(NewExe);

    private static string UniqueLock() => @"Local\PCMieru.Test.RunSwitch." + Guid.NewGuid().ToString("N");

    private static bool Exists(string path) => true;

    private static MemoryRunStore Seeded(bool stamp = false)
    {
        var store = new MemoryRunStore();
        store.SeedRun(RunSwitch.OldName, OldCommand);
        if (stamp)
        {
            store.SeedApproval(RunSwitch.OldName, StartupApprovedCodec.EncodeEnabled());
            store.SeedApproval(RunSwitch.NewName, StartupApprovedCodec.EncodeEnabled());
        }

        return store;
    }

    private static string Fingerprint(MemoryRunStore store)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { RunSwitch.OldName, RunSwitch.NewName })
        {
            var run = store.ReadRun(name);
            sb.Append("run ").Append(name).Append('=').Append(run is null ? "-" : run.Kind + ":" + run.Text).Append('\n');
            var approval = store.ReadApproval(name);
            sb.Append("stamp ").Append(name).Append('=').Append(approval is null ? "-" : approval.Kind + ":" + Convert.ToHexString(approval.Bytes)).Append('\n');
        }

        return sb.ToString();
    }

    private sealed class MemoryJournal : IRunSwitchJournal
    {
        public string? Text;
        public int Writes;
        public int FailWriteAt = -1;
        public bool FailDelete;

        /// <summary>Thrown by Read instead of the text (a journal that is there but cannot be opened).</summary>
        public Exception? ReadFailure;

        /// <summary>Called after a write took effect (with the number of the write).</summary>
        public Action<int>? OnWrite;

        /// <summary>Called when the journal is removed (before it is gone).</summary>
        public Action? OnDelete;

        public List<string> Written { get; } = [];

        public string Read() => ReadFailure is not null ? throw ReadFailure : Text ?? throw new FileNotFoundException();

        public void Write(string text)
        {
            Writes++;
            if (FailWriteAt == Writes)
            {
                throw new IOException("journal write failed (injected)");
            }

            Text = text;
            Written.Add(text);
            OnWrite?.Invoke(Writes);
        }

        public void Delete()
        {
            if (FailDelete)
            {
                throw new IOException("journal delete failed (injected)");
            }

            OnDelete?.Invoke();
            Text = null;
        }
    }

    /// <summary>Counts every call of the inner store and throws at the n-th (once, or from then on).</summary>
    private sealed class FaultStore(MemoryRunStore inner) : IRunStore
    {
        public int Count { get; private set; }
        public int FailAt { get; set; } = -1;
        public bool Persistent { get; set; }

        private void Tick()
        {
            Count++;
            if (FailAt > 0 && (Count == FailAt || (Persistent && Count > FailAt)))
            {
                throw new IOException("injected at call " + Count);
            }
        }

        public RunValue? ReadRun(string name) { Tick(); return inner.ReadRun(name); }

        public void WriteRun(string name, string text, RegistryValueKind kind = RegistryValueKind.String) { Tick(); inner.WriteRun(name, text, kind); }

        public void DeleteRun(string name) { Tick(); inner.DeleteRun(name); }

        public ApprovalValue? ReadApproval(string name) { Tick(); return inner.ReadApproval(name); }

        public void WriteApproval(string name, byte[] bytes, RegistryValueKind kind = RegistryValueKind.Binary) { Tick(); inner.WriteApproval(name, bytes, kind); }

        public void DeleteApproval(string name) { Tick(); inner.DeleteApproval(name); }

        public void Flush() { Tick(); inner.Flush(); }
    }

    // ---- Offer ----

    [Fact]
    public void Offer_asks_when_the_old_value_is_enabled_by_having_no_stamp()
    {
        var offer = RunSwitch.Offer(Seeded(), NewExe, Exists);
        Assert.True(offer.Ask);
        Assert.Equal(RunSwitchReason.None, offer.Reason);
    }

    [Fact]
    public void Offer_asks_for_an_explicitly_enabled_stamp()
    {
        var store = Seeded();
        store.SeedApproval(RunSwitch.OldName, StartupApprovedCodec.EncodeEnabled());
        Assert.True(RunSwitch.Offer(store, NewExe, Exists).Ask);
    }

    [Fact]
    public void Offer_does_not_ask_for_a_disabled_stamp()
    {
        var store = Seeded();
        store.SeedApproval(RunSwitch.OldName, StartupApprovedCodec.EncodeDisabled(1));
        Assert.Equal(new RunSwitchOffer(false, RunSwitchReason.NotEnabled), RunSwitch.Offer(store, NewExe, Exists));
    }

    [Fact]
    public void Offer_does_not_ask_for_a_stamp_of_an_unknown_format()
    {
        var store = Seeded();
        store.SeedApproval(RunSwitch.OldName, [1, 2, 3]);
        Assert.Equal(RunSwitchReason.NotEnabled, RunSwitch.Offer(store, NewExe, Exists).Reason);
    }

    [Fact]
    public void Offer_does_not_ask_when_a_read_fails_or_is_denied()
    {
        var failing = Seeded();
        failing.FailOn(RunStoreCall.ReadApproval, new IOException("x"));
        Assert.Equal(new RunSwitchOffer(false, RunSwitchReason.Unreadable), RunSwitch.Offer(failing, NewExe, Exists));

        var denied = Seeded();
        denied.DenyOn(RunStoreCall.ReadRun);
        Assert.Equal(RunSwitchReason.Unreadable, RunSwitch.Offer(denied, NewExe, Exists).Reason);
    }

    [Fact]
    public void Offer_does_not_ask_when_the_new_name_already_has_a_value()
    {
        var store = Seeded();
        store.SeedRun(RunSwitch.NewName, NewCommand);
        Assert.Equal(RunSwitchReason.NewExists, RunSwitch.Offer(store, NewExe, Exists).Reason);
    }

    [Fact]
    public void Offer_does_not_ask_without_an_old_value_or_for_another_exe_or_a_missing_file()
    {
        Assert.Equal(RunSwitchReason.OldNotThere, RunSwitch.Offer(new MemoryRunStore(), NewExe, Exists).Reason);

        var other = new MemoryRunStore();
        other.SeedRun(RunSwitch.OldName, "\"C:\\x\\Other.exe\"");
        Assert.Equal(RunSwitchReason.NotOldExe, RunSwitch.Offer(other, NewExe, Exists).Reason);

        Assert.Equal(RunSwitchReason.NotOldExe, RunSwitch.Offer(Seeded(), NewExe, _ => false).Reason);
        Assert.Equal(RunSwitchReason.InvalidPath, RunSwitch.Offer(Seeded(), "relative.exe", Exists).Reason);
    }

    [Fact]
    public void Offer_takes_the_party_exe_name_in_any_case_and_an_unquoted_path()
    {
        var store = new MemoryRunStore();
        store.SeedRun(RunSwitch.OldName, @"C:\Old\partsparty.EXE");
        Assert.True(RunSwitch.Offer(store, NewExe, Exists).Ask);
    }

    [Fact]
    public void Offer_checks_the_file_on_disk_by_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-runswitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "PartsLife.exe");
            var store = new MemoryRunStore();
            store.SeedRun(RunSwitch.OldName, "\"" + exe + "\"");
            Assert.Equal(RunSwitchReason.NotOldExe, RunSwitch.Offer(store, NewExe).Reason);
            File.WriteAllText(exe, "x");
            Assert.True(RunSwitch.Offer(store, NewExe).Ask);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Switch ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Switch_moves_the_entry_and_leaves_only_the_new_one_enabled(bool stamps)
    {
        var store = Seeded(stamps);
        var journal = new MemoryJournal();
        var result = RunSwitch.Switch(store, journal, NewExe, Exists, UniqueLock());

        Assert.Equal(RunSwitchStatus.Switched, result.Status);
        Assert.Null(store.ReadRun(RunSwitch.OldName));
        Assert.Null(store.ReadApproval(RunSwitch.OldName));
        Assert.Equal(new RunValue(RegistryValueKind.String, NewCommand), store.ReadRun(RunSwitch.NewName));
        var stamp = store.ReadApproval(RunSwitch.NewName);
        Assert.True(stamp is null || StartupApprovedCodec.Decode(stamp.Kind, stamp.Bytes) == ApprovalState.Enabled);
        Assert.Null(journal.Text);
        Assert.Equal(2, journal.Written.Count);
        Assert.Contains("\"phase\": \"prepared\"", journal.Written[0]);
        Assert.Contains("\"phase\": \"committed\"", journal.Written[1]);
        Assert.Contains("PartsLife", journal.Written[0]);
    }

    [Fact]
    public void Switch_enables_the_new_entry_completely_before_it_deletes_the_old_one()
    {
        var inner = Seeded();
        var store = new FaultStore(inner);
        RunSwitch.Switch(store, new MemoryJournal(), NewExe, Exists, UniqueLock());
        var calls = inner.Calls.Select(c => c.Call + ":" + c.Name).ToList();
        Assert.Equal(
            [
                "ReadRun:PartsLife", "ReadRun:PCMieru.Party", "ReadApproval:PartsLife", "ReadApproval:PCMieru.Party",
                "WriteApproval:PCMieru.Party", "WriteRun:PCMieru.Party", "DeleteApproval:PCMieru.Party", "DeleteRun:PartsLife", "DeleteApproval:PartsLife",
                "ReadRun:PartsLife", "ReadRun:PCMieru.Party", "ReadApproval:PartsLife", "ReadApproval:PCMieru.Party",
                "Flush:",
            ],
            calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_transient_failure_at_every_store_call_leaves_the_four_values_as_they_were(bool stamps)
    {
        var probe = new FaultStore(Seeded(stamps));
        Assert.Equal(RunSwitchStatus.Switched, RunSwitch.Switch(probe, new MemoryJournal(), NewExe, Exists, UniqueLock()).Status);
        var total = probe.Count;
        Assert.Equal(SwitchCalls, total);

        for (var i = 1; i <= total; i++)
        {
            var inner = Seeded(stamps);
            var before = Fingerprint(inner);
            var store = new FaultStore(inner) { FailAt = i };
            var journal = new MemoryJournal();
            var result = RunSwitch.Switch(store, journal, NewExe, Exists, UniqueLock());

            Assert.True(result.Status == RunSwitchStatus.RolledBack, "call " + i + ": " + result);
            Assert.Equal(before, Fingerprint(inner));
            Assert.Null(journal.Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_store_that_stays_broken_keeps_the_journal_and_a_later_recovery_puts_everything_back(bool stamps)
    {
        for (var i = 5; i <= SwitchCalls; i++)
        {
            var inner = Seeded(stamps);
            var before = Fingerprint(inner);
            var journal = new MemoryJournal();
            var result = RunSwitch.Switch(new FaultStore(inner) { FailAt = i, Persistent = true }, journal, NewExe, Exists, UniqueLock());

            Assert.True(result.Status == RunSwitchStatus.RestoreFailed, "call " + i + ": " + result);
            Assert.NotNull(journal.Text);

            var recovered = RunSwitch.Recover(inner, journal, UniqueLock());
            Assert.True(recovered.Status == RunSwitchStatus.Recovered, "call " + i + ": " + recovered);
            Assert.Equal(before, Fingerprint(inner));
            Assert.Null(journal.Text);
        }
    }

    [Fact]
    public void A_store_that_is_broken_from_the_start_changes_nothing_and_writes_no_journal()
    {
        var inner = Seeded();
        var before = Fingerprint(inner);
        var journal = new MemoryJournal();
        var result = RunSwitch.Switch(new FaultStore(inner) { FailAt = 1, Persistent = true }, journal, NewExe, Exists, UniqueLock());
        Assert.Equal(RunSwitchStatus.RolledBack, result.Status);
        Assert.Equal(0, journal.Writes);
        Assert.Equal(before, Fingerprint(inner));
    }

    [Fact]
    public void A_journal_that_cannot_be_written_changes_nothing()
    {
        var inner = Seeded();
        var before = Fingerprint(inner);
        inner.Heal();
        var journal = new MemoryJournal { FailWriteAt = 1 };
        var store = new FaultStore(inner);
        var result = RunSwitch.Switch(store, journal, NewExe, Exists, UniqueLock());

        Assert.Equal(RunSwitchStatus.RolledBack, result.Status);
        Assert.Equal(4, store.Count);
        Assert.Equal(before, Fingerprint(inner));
        Assert.Null(journal.Text);
    }

    [Fact]
    public void A_journal_file_in_an_impossible_place_changes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-runswitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var blocker = Path.Combine(dir, "file");
            File.WriteAllText(blocker, "x");
            var inner = Seeded();
            var before = Fingerprint(inner);
            var result = RunSwitch.Switch(inner, new FileRunSwitchJournal(Path.Combine(blocker, "run-switch.json")), NewExe, Exists, UniqueLock());
            Assert.Equal(RunSwitchStatus.RolledBack, result.Status);
            Assert.Equal(before, Fingerprint(inner));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_commit_mark_that_cannot_be_written_rolls_back()
    {
        var inner = Seeded(true);
        var before = Fingerprint(inner);
        var journal = new MemoryJournal { FailWriteAt = 2 };
        var result = RunSwitch.Switch(inner, journal, NewExe, Exists, UniqueLock());
        Assert.Equal(RunSwitchStatus.RolledBack, result.Status);
        Assert.Equal(before, Fingerprint(inner));
        Assert.Null(journal.Text);
    }

    [Fact]
    public void A_journal_that_cannot_be_removed_after_the_commit_mark_keeps_the_switch_and_a_recovery_removes_it()
    {
        var inner = Seeded();
        var journal = new MemoryJournal { FailDelete = true };
        var result = RunSwitch.Switch(inner, journal, NewExe, Exists, UniqueLock());
        Assert.Equal(RunSwitchStatus.Switched, result.Status);
        Assert.Contains("\"phase\": \"committed\"", journal.Text);
        var switched = Fingerprint(inner);

        journal.FailDelete = false;
        Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(inner, journal, UniqueLock()).Status);
        Assert.Null(journal.Text);
        Assert.Equal(switched, Fingerprint(inner));
    }

    [Fact]
    public void Switch_does_nothing_when_the_state_changed_since_the_offer_or_an_old_journal_is_there()
    {
        var disabled = Seeded();
        disabled.SeedApproval(RunSwitch.OldName, StartupApprovedCodec.EncodeDisabled(1));
        var store = new FaultStore(disabled);
        Assert.Equal(RunSwitchStatus.NotOffered, RunSwitch.Switch(store, new MemoryJournal(), NewExe, Exists, UniqueLock()).Status);
        Assert.Equal(4, store.Count);

        var inner = Seeded();
        var stale = new MemoryJournal { Text = "{}" };
        var result = RunSwitch.Switch(new FaultStore(inner), stale, NewExe, Exists, UniqueLock());
        Assert.Equal(RunSwitchStatus.NeedsUserCheck, result.Status);
        Assert.Equal("{}", stale.Text);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Switch_and_the_logon_setting_wait_for_each_other()
    {
        var name = UniqueLock();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var taken = RunSwitchLock.TryAcquire(name, TimeSpan.FromSeconds(5));
            Assert.NotNull(taken);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

        var inner = Seeded();
        var store = new FaultStore(inner);
        Assert.Equal(RunSwitchStatus.Busy, RunSwitch.Switch(store, new MemoryJournal(), NewExe, Exists, name, TimeSpan.FromMilliseconds(100)).Status);
        Assert.Equal(RunSwitchStatus.Busy, RunSwitch.Recover(store, new MemoryJournal(), name, TimeSpan.FromMilliseconds(100)).Status);
        var enable = PartyLogon.Enable(store, NewExe, name, TimeSpan.FromMilliseconds(100));
        Assert.Equal(PartyLogonFailure.Busy, enable.Failure);
        Assert.Equal(PartyLogonFailure.Busy, PartyLogon.Disable(store, name, TimeSpan.FromMilliseconds(100)).Failure);
        Assert.Equal(0, store.Count);

        release.Set();
        await holder;
        Assert.True(PartyLogon.Enable(store, NewExe, name).Ok);
    }

    [Fact]
    public void The_lock_name_is_the_documented_one()
    {
        const string sid = "S-1-5-21-1111-2222-3333-1001";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16].ToLowerInvariant();
        Assert.Equal(@"Global\PCMieru.Party.RunSwitch.v1." + hash, RunSwitchLock.NameFor(sid));
        Assert.Matches(@"^Global\\PCMieru\.Party\.RunSwitch\.v1\.[0-9a-f]{16}$", RunSwitchLock.CurrentName());
    }

    // ---- Recover ----

    [Fact]
    public void Recover_with_no_journal_touches_nothing()
    {
        var store = new FaultStore(Seeded());
        Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(store, new MemoryJournal(), UniqueLock()).Status);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Recover_removes_a_committed_journal_without_touching_the_values()
    {
        var inner = Seeded();
        var journal = new MemoryJournal();
        RunSwitch.Switch(inner, journal, NewExe, Exists, UniqueLock());
        journal.Text = journal.Written[1];
        var store = new FaultStore(inner);
        Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(store, journal, UniqueLock()).Status);
        Assert.Null(journal.Text);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Recover_from_a_crash_in_each_phase_restores_the_originals()
    {
        // a crash = the store dies for good at call i and the process ends (the journal stays, the values stay half done)
        for (var i = 5; i <= SwitchCalls; i++)
        {
            var inner = Seeded(true);
            var before = Fingerprint(inner);
            var journal = new MemoryJournal();
            RunSwitch.Switch(new FaultStore(inner) { FailAt = i, Persistent = true }, journal, NewExe, Exists, UniqueLock());
            var half = Fingerprint(inner);

            var recovered = RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: Exists);
            Assert.True(recovered.Status == RunSwitchStatus.Recovered, "phase " + i + ": " + recovered + "\n" + half);
            Assert.Equal(before, Fingerprint(inner));
            Assert.Null(journal.Text);
        }
    }

    [Fact]
    public void Recover_after_a_user_change_writes_nothing_and_keeps_the_journal()
    {
        var inner = Seeded();
        var journal = new MemoryJournal();
        // crash after the new Run value was written: both Run values exist
        RunSwitch.Switch(new FaultStore(inner) { FailAt = 7, Persistent = true }, journal, NewExe, Exists, UniqueLock());
        Assert.NotNull(inner.ReadRun(RunSwitch.NewName));

        // the user then changed the old entry to something else in Windows
        inner.SeedRun(RunSwitch.OldName, "\"C:\\Somewhere\\Else.exe\"");
        var changed = Fingerprint(inner);
        var store = new FaultStore(inner);
        var text = journal.Text;

        var result = RunSwitch.Recover(store, journal, UniqueLock());
        Assert.Equal(RunSwitchStatus.NeedsUserCheck, result.Status);
        Assert.True(result.NeedsAttention);
        Assert.Equal(4, store.Count);
        Assert.Equal(changed, Fingerprint(inner));
        Assert.Equal(text, journal.Text);
    }

    [Fact]
    public void Recover_after_the_user_changed_the_new_stamp_to_something_unknown_stops()
    {
        var inner = Seeded();
        var journal = new MemoryJournal();
        RunSwitch.Switch(new FaultStore(inner) { FailAt = 7, Persistent = true }, journal, NewExe, Exists, UniqueLock());
        inner.SeedApproval(RunSwitch.NewName, [9, 9, 9, 9]);
        Assert.Equal(RunSwitchStatus.NeedsUserCheck, RunSwitch.Recover(inner, journal, UniqueLock()).Status);
        Assert.NotNull(journal.Text);
    }

    [Fact]
    public void Recover_keeps_the_journal_when_the_store_fails_and_works_on_a_retry()
    {
        var inner = Seeded();
        var before = Fingerprint(inner);
        var journal = new MemoryJournal();
        RunSwitch.Switch(new FaultStore(inner) { FailAt = 7, Persistent = true }, journal, NewExe, Exists, UniqueLock());

        var broken = RunSwitch.Recover(new FaultStore(inner) { FailAt = 5, Persistent = true }, journal, UniqueLock());
        Assert.Equal(RunSwitchStatus.RecoveryFailed, broken.Status);
        Assert.NotNull(journal.Text);

        Assert.Equal(RunSwitchStatus.Recovered, RunSwitch.Recover(inner, journal, UniqueLock()).Status);
        Assert.Equal(before, Fingerprint(inner));
    }

    [Fact]
    public void Recover_does_not_trust_an_unreadable_journal()
    {
        var inner = Seeded();
        var store = new FaultStore(inner);
        var journal = new MemoryJournal { Text = "not json" };
        Assert.Equal(RunSwitchStatus.NeedsUserCheck, RunSwitch.Recover(store, journal, UniqueLock()).Status);
        Assert.Equal(0, store.Count);
        Assert.Equal("not json", journal.Text);
    }

    [Fact]
    public void The_journal_file_is_written_and_removed_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-runswitch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = RunSwitch.JournalPathFor(dir);
            Assert.Equal(Path.Combine(dir, "run-switch.json"), path);
            var journal = new FileRunSwitchJournal(path);
            var inner = Seeded();
            var result = RunSwitch.Switch(new FaultStore(inner) { FailAt = 7, Persistent = true }, journal, NewExe, Exists, UniqueLock());
            Assert.Equal(RunSwitchStatus.RestoreFailed, result.Status);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Contains("\"phase\": \"prepared\"", File.ReadAllText(path));

            Assert.Equal(RunSwitchStatus.Recovered, RunSwitch.Recover(inner, path, Exists).Status);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- C29: step order, restore order, command length, journal read, durability, an uninstalled old exe ----

    /// <summary>The store calls of a clean switch: 4 reads, 5 writes (new stamp disabled, new Run, new stamp enabled or removed, old Run, old stamp), 4 reads back, 1 flush.</summary>
    private const int SwitchCalls = 14;

    /// <summary>"Effectively enabled" by the C26 rules: the Run value is there and the stamp is absent or of the enabled kind (a disabled or unknown stamp = not enabled).</summary>
    private static bool Effective(MemoryRunStore store, string name)
    {
        if (store.ReadRun(name) is null)
        {
            return false;
        }

        var stamp = store.ReadApproval(name);
        return stamp is null || StartupApprovedCodec.Decode(stamp.Kind, stamp.Bytes) == ApprovalState.Enabled;
    }

    /// <summary>A switch whose store dies for good at call <paramref name="crashAt"/> and whose process ends there: the journal stays, the values stay half done.</summary>
    private static (MemoryRunStore Store, MemoryJournal Journal) CrashedSwitch(bool stamps, int crashAt)
    {
        var inner = Seeded(stamps);
        var journal = new MemoryJournal();
        RunSwitch.Switch(new FaultStore(inner) { FailAt = crashAt, Persistent = true }, journal, NewExe, Exists, UniqueLock());
        return (inner, journal);
    }

    private static string PathOfLength(int length)
    {
        var sb = new StringBuilder(@"C:\");
        while (length - sb.Length > 100)
        {
            sb.Append('d', 100).Append('\\');
        }

        sb.Append('f', length - sb.Length - 4).Append(".exe");
        Assert.Equal(length, sb.Length);
        return sb.ToString();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void After_a_crash_at_every_store_call_of_the_switch_at_least_one_of_old_and_new_is_effectively_enabled(bool stamps)
    {
        for (var i = 1; i <= SwitchCalls; i++)
        {
            var (inner, journal) = CrashedSwitch(stamps, i);
            var label = "crash at call " + i + "\n" + Fingerprint(inner);
            Assert.True(Effective(inner, RunSwitch.OldName) || Effective(inner, RunSwitch.NewName), label);
            if (i >= 5)
            {
                Assert.NotNull(journal.Text);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_crash_points_leave_the_states_the_order_promises(bool stamps)
    {
        // (old effectively enabled, new effectively enabled) after the store died at call i
        (int Call, bool Old, bool New)[] expected =
        [
            (5, true, false),    // new stamp not written yet: nothing changed
            (6, true, false),    // new stamp disabled, no new Run yet
            (7, true, false),    // new Run there but its stamp is still the disabled one
            (8, true, true),     // new entry fully enabled, old Run still there: both start once (Recover settles it)
            (9, false, true),    // old Run deleted, new enabled
            (10, false, true),   // everything written, reading back failed
            (14, false, true),   // only the flush failed
        ];
        foreach (var (call, oldOn, newOn) in expected)
        {
            var (inner, _) = CrashedSwitch(stamps, call);
            var label = "crash at call " + call + "\n" + Fingerprint(inner);
            Assert.True(oldOn == Effective(inner, RunSwitch.OldName), label);
            Assert.True(newOn == Effective(inner, RunSwitch.NewName), label);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_restore_stopped_after_its_first_write_never_leaves_both_entries_effectively_enabled_and_the_next_start_finishes_it(bool stamps)
    {
        var before = Fingerprint(Seeded(stamps));
        for (var crash = 5; crash <= SwitchCalls; crash++)
        {
            for (var stop = 6; stop <= 14; stop++)
            {
                var (inner, journal) = CrashedSwitch(stamps, crash);
                var half = RunSwitch.Recover(new FaultStore(inner) { FailAt = stop, Persistent = true }, journal, UniqueLock(), fileExists: Exists);
                var label = "switch crash " + crash + ", restore stopped at call " + stop + ": " + half + "\n" + Fingerprint(inner);

                // the first restore call took the new entry away: from then on a stop never leaves two starts
                Assert.False(Effective(inner, RunSwitch.OldName) && Effective(inner, RunSwitch.NewName), label);

                var next = RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: Exists);
                Assert.True(next.Status == (half.Status == RunSwitchStatus.Recovered ? RunSwitchStatus.NothingToDo : RunSwitchStatus.Recovered), label + "\nnext: " + next);
                Assert.Equal(before, Fingerprint(inner));
                Assert.Null(journal.Text);
            }
        }
    }

    [Fact]
    public void A_restore_that_cannot_take_the_new_run_away_does_not_bring_the_old_entry_back()
    {
        var (inner, journal) = CrashedSwitch(stamps: false, crashAt: 9); // old Run deleted, new entry enabled
        inner.DenyOn(RunStoreCall.DeleteRun);
        var tail = inner.Calls.Count;

        var result = RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: Exists);

        Assert.Equal(RunSwitchStatus.RecoveryFailed, result.Status);
        Assert.NotNull(journal.Text);
        Assert.Null(inner.ReadRun(RunSwitch.OldName));
        Assert.True(Effective(inner, RunSwitch.NewName));
        var writesToOld = inner.Calls.Skip(tail).Where(c => c.Name == RunSwitch.OldName && c.Call is RunStoreCall.WriteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
        Assert.Empty(writesToOld);
    }

    // ---- command length ----

    [Fact]
    public void A_run_command_of_260_characters_or_more_is_refused_before_anything_is_written()
    {
        var tooLong = PathOfLength(250);
        Assert.Equal(260, PartyLogon.ExpectedValue(tooLong).Length);
        var fits = PathOfLength(249);
        Assert.Equal(259, PartyLogon.ExpectedValue(fits).Length);

        var offered = Seeded();
        Assert.Equal(new RunSwitchOffer(false, RunSwitchReason.TooLong), RunSwitch.Offer(offered, tooLong, Exists));
        Assert.True(RunSwitch.Offer(offered, fits, Exists).Ask);

        var inner = Seeded();
        var before = Fingerprint(inner);
        var journal = new MemoryJournal();
        var result = RunSwitch.Switch(inner, journal, tooLong, Exists, UniqueLock());
        Assert.Equal(RunSwitchStatus.NotOffered, result.Status);
        Assert.Equal(nameof(RunSwitchReason.TooLong), result.Detail);
        Assert.Equal(0, journal.Writes);
        Assert.Equal(before, Fingerprint(inner));
        Assert.DoesNotContain(inner.Calls, c => c.Call is RunStoreCall.WriteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteRun or RunStoreCall.DeleteApproval);

        var logon = new MemoryRunStore();
        var refused = PartyLogon.Enable(logon, tooLong, UniqueLock());
        Assert.False(refused.Ok);
        Assert.Equal(PartyLogonFailure.TooLong, refused.Failure);
        Assert.Empty(logon.Calls);
        Assert.True(PartyLogon.Enable(logon, fits, UniqueLock()).Ok);
    }

    // ---- journal read: only "not found" is "no journal" ----

    [Fact]
    public void A_journal_that_cannot_be_read_is_not_taken_for_no_journal()
    {
        var store = new FaultStore(Seeded());
        var journal = new MemoryJournal { Text = "{}", ReadFailure = new UnauthorizedAccessException("access denied (injected)") };

        var recovered = RunSwitch.Recover(store, journal, UniqueLock());
        Assert.Equal(RunSwitchStatus.RecoveryFailed, recovered.Status);
        Assert.True(recovered.NeedsAttention);
        Assert.Equal(0, store.Count);
        Assert.Equal("{}", journal.Text);

        var inner = Seeded();
        var before = Fingerprint(inner);
        var switched = RunSwitch.Switch(inner, journal, NewExe, Exists, UniqueLock());
        Assert.Equal(RunSwitchStatus.RolledBack, switched.Status);
        Assert.Equal(0, journal.Writes);
        Assert.Equal(before, Fingerprint(inner));
        Assert.Equal("{}", journal.Text);
    }

    [Fact]
    public void File_not_found_and_folder_not_found_are_the_only_ways_to_say_no_journal()
    {
        foreach (Exception absent in new Exception[] { new FileNotFoundException(), new DirectoryNotFoundException() })
        {
            var store = new FaultStore(Seeded());
            var journal = new MemoryJournal { ReadFailure = absent };
            Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(store, journal, UniqueLock()).Status);
            Assert.Equal(0, store.Count);
        }

        foreach (Exception unknown in new Exception[] { new IOException("sharing violation (injected)"), new UnauthorizedAccessException(), new PathTooLongException() })
        {
            var store = new FaultStore(Seeded());
            var result = RunSwitch.Recover(store, new MemoryJournal { ReadFailure = unknown }, UniqueLock());
            Assert.Equal(RunSwitchStatus.RecoveryFailed, result.Status);
            Assert.Equal(0, store.Count);
        }
    }

    [Fact]
    public void A_journal_path_that_cannot_be_opened_on_disk_is_not_taken_for_no_journal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-runswitch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = RunSwitch.JournalPathFor(dir);
            Directory.CreateDirectory(path); // a folder where the journal file should be: it exists, and it cannot be read as a file (File.Exists says false for it)
            var inner = Seeded();
            var before = Fingerprint(inner);

            var result = RunSwitch.Recover(inner, path, Exists);
            Assert.Equal(RunSwitchStatus.RecoveryFailed, result.Status);
            Assert.Equal(before, Fingerprint(inner));
            Assert.True(Directory.Exists(path));
            Assert.Equal(RunSwitchStatus.RolledBack, RunSwitch.Switch(inner, path, NewExe, Exists).Status);
            Assert.Equal(before, Fingerprint(inner));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- durability ----

    [Fact]
    public void Switch_flushes_the_store_after_its_last_registry_write_and_before_the_commit_mark()
    {
        var inner = Seeded();
        var flushesWhenWritten = new List<int>();
        var journal = new MemoryJournal { OnWrite = _ => flushesWhenWritten.Add(inner.FlushCount) };
        Assert.Equal(RunSwitchStatus.Switched, RunSwitch.Switch(inner, journal, NewExe, Exists, UniqueLock()).Status);

        Assert.Equal([0, 1], flushesWhenWritten); // the "prepared" write before any flush, the "committed" write after exactly one
        var calls = inner.Calls.ToList();
        var lastWrite = calls.FindLastIndex(c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
        Assert.True(calls.FindIndex(c => c.Call == RunStoreCall.Flush) > lastWrite);
    }

    [Fact]
    public void Recover_flushes_after_it_put_the_values_back_and_before_it_removes_the_journal()
    {
        var (inner, journal) = CrashedSwitch(stamps: true, crashAt: 9);
        var flushesBefore = inner.FlushCount;
        var flushesWhenRemoved = -1;
        var callsWhenRemoved = -1;
        journal.OnDelete = () =>
        {
            flushesWhenRemoved = inner.FlushCount;
            callsWhenRemoved = inner.Calls.Count;
        };
        var tail = inner.Calls.Count;

        Assert.Equal(RunSwitchStatus.Recovered, RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: Exists).Status);

        Assert.Equal(flushesBefore + 1, flushesWhenRemoved);
        var calls = inner.Calls.Take(callsWhenRemoved).Skip(tail).ToList();
        var lastWrite = calls.FindLastIndex(c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);
        Assert.True(lastWrite >= 0 && calls.FindIndex(c => c.Call == RunStoreCall.Flush) > lastWrite);
    }

    [Fact]
    public void A_flush_that_fails_keeps_the_journal_of_a_recovery()
    {
        var (inner, journal) = CrashedSwitch(stamps: false, crashAt: 9);
        inner.DenyOn(RunStoreCall.Flush);
        var result = RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: Exists);
        Assert.Equal(RunSwitchStatus.RecoveryFailed, result.Status);
        Assert.NotNull(journal.Text);

        inner.Heal();
        Assert.Equal(RunSwitchStatus.Recovered, RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: Exists).Status);
        Assert.Null(journal.Text);
    }

    [Fact]
    public void The_journal_file_is_replaced_by_a_write_through_rename_and_an_absent_one_reads_as_not_found()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-runswitch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = RunSwitch.JournalPathFor(dir);
            var journal = new FileRunSwitchJournal(path);
            Assert.Throws<DirectoryNotFoundException>(() => journal.Read());

            journal.Write("first");
            journal.Write("second"); // MoveFileEx with MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH over the first
            Assert.Equal("second", journal.Read());
            Assert.False(File.Exists(path + ".tmp"));

            journal.Delete();
            Assert.Throws<FileNotFoundException>(() => journal.Read());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- an old exe that was uninstalled in the meantime ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void When_the_old_exe_is_gone_and_the_new_one_is_there_a_recovery_finishes_the_switch_instead_of_bringing_the_old_entry_back(bool stamps)
    {
        static bool OldGone(string path) => !string.Equals(path, OldExe, StringComparison.OrdinalIgnoreCase);

        for (var crash = 5; crash <= SwitchCalls; crash++)
        {
            var (inner, journal) = CrashedSwitch(stamps, crash);
            var label = "crash at call " + crash + "\n" + Fingerprint(inner);
            var result = RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: OldGone);

            Assert.True(result.Status == RunSwitchStatus.Recovered, label + "\n" + result);
            Assert.Null(inner.ReadRun(RunSwitch.OldName));
            Assert.Null(inner.ReadApproval(RunSwitch.OldName));
            Assert.Equal(new RunValue(RegistryValueKind.String, NewCommand), inner.ReadRun(RunSwitch.NewName));
            Assert.True(Effective(inner, RunSwitch.NewName), label);
            Assert.Null(journal.Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void When_the_old_exe_and_the_new_one_are_both_gone_a_recovery_puts_the_originals_back(bool stamps)
    {
        var before = Fingerprint(Seeded(stamps));
        for (var crash = 5; crash <= SwitchCalls; crash++)
        {
            var (inner, journal) = CrashedSwitch(stamps, crash);
            var result = RunSwitch.Recover(inner, journal, UniqueLock(), fileExists: _ => false);
            Assert.True(result.Status == RunSwitchStatus.Recovered, "crash at call " + crash + ": " + result);
            Assert.Equal(before, Fingerprint(inner));
            Assert.Null(journal.Text);
        }
    }
}
