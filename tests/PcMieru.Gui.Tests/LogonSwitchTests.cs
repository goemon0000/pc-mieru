using System.Diagnostics;
using PcMieru.Core.Siblings;

namespace PcMieru.Gui.Tests;

/// <summary>
/// C30: the sign-in setting next to the switch from the old Parts Life start. Ticking the box while the old start is on switches it (never two starts) and says how it ended; the owner's click settles a
/// journal that is stuck; the recovery at start tries again while the lock is busy. In-memory store, a temporary roaming folder, a lock of the test's own: nothing here names the real registry or the
/// owner's folders.
/// </summary>
public sealed class LogonSwitchTests
{
    private const string OldExe = @"C:\Old\PartsLife.exe";
    private const string Check = "Please check the sign-in setting";
    private const string Switched = "Switched from the old Parts Life sign-in start";
    private const string NotSwitched = "Could not switch; the old Parts Life still starts at sign-in";

    private static readonly string ExePath = Path.Combine(Path.GetTempPath(), "pcmieru-logon-switch-test", "PCMieru.Party.exe");
    private static readonly string Expected = PartyLogon.ExpectedValue(ExePath);
    private static readonly string OldCommand = "\"" + OldExe + "\"";

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly bool? _auto;

        public Rig(string roamingRoot, TimeSpan? retryDelay = null)
        {
            _locale = Harness.Locale(Language.En);
            _auto = Dialogs.AutoAnswer;
            Dialogs.AutoAnswer = false;
            Roaming = roamingRoot;
            LockName = @"Local\PCMieru.Test.LogonSwitch." + Guid.NewGuid().ToString("N");
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, new ThemeService(ThemeMode.Light), new GuiSettings())
            {
                PartyRoamingRoot = roamingRoot,
                LogonStore = Store,
                PartyExePath = ExePath,
                PartyLauncherOverride = new PartyLauncher(() => false, _ => true, (_, _) => true),
                LogonSeam = new LogonSeams(FileExists: _ => true, LockName: LockName, LockWait: TimeSpan.FromMilliseconds(50), RetryDelay: retryDelay ?? TimeSpan.Zero),
            };
            Page = new SettingsPage(Context);
            Store.SeedRun(RunSwitch.OldName, OldCommand);
        }

        public MemoryRunStore Store { get; } = new();

        public string Roaming { get; }

        public string LockName { get; }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        public SettingsPage Page { get; }

        public string Journal => RunSwitch.JournalPathFor(Path.Combine(Roaming, "PCMieru", "party"));

        public void Refresh()
        {
            var task = Page.RefreshPartyLogon();
            Assert.True(Ui.PumpUntil(() => task.IsCompleted), "the read of the sign-in setting never ended");
        }

        public void Click(bool on)
        {
            Page.PartyLogonBox.Checked = on;
            var task = Page.PartyLogonPending;
            Assert.True(Ui.PumpUntil(() => task.IsCompleted), "the change of the sign-in setting never ended");
        }

        /// <summary>An interrupted switch whose old entry the user then changed in Windows: the journal is there and the recovery says "check".</summary>
        public void MakeStuckJournal()
        {
            Store.FailOn(RunStoreCall.DeleteRun, new IOException("injected"));
            Assert.Equal(RunSwitchStatus.RestoreFailed, RunSwitch.Switch(Store, Journal, ExePath, _ => true).Status);
            Store.Heal();
            Store.SeedRun(RunSwitch.OldName, "\"C:\\Somewhere\\Else.exe\"");
            Assert.Equal(RunSwitchStatus.NeedsUserCheck, RunSwitch.Recover(Store, Journal, _ => true).Status);
            Assert.True(File.Exists(Journal));
        }

        public void Dispose()
        {
            Page.Dispose();
            Dialogs.AutoAnswer = _auto;
            Pump.Dispose();
            Services.Dispose();
            _locale.Dispose();
        }
    }

    // --- item 2: ticking the box while the old start is on ---

    [Fact]
    public void Ticking_the_box_while_the_old_start_is_on_switches_it_and_there_are_never_two_starts()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.Refresh();
            Assert.False(rig.Page.PartyLogonBox.Checked);

            rig.Click(true);

            Assert.Null(rig.Store.ReadRun(RunSwitch.OldName));
            Assert.Equal(Expected, rig.Store.ReadRun(PartyLogon.ValueName)?.Text);
            Assert.True(rig.Page.PartyLogonBox.Checked);
            Assert.Equal(PartyLogonState.On, rig.Page.PartyLogonShown);
            Assert.Equal(Switched, rig.Page.PartyLogonLineText);
            Assert.Equal(CellTone.Good, rig.Page.PartyLogonLineTone);
            Assert.False(File.Exists(rig.Journal));
        });
    }

    [Fact]
    public void Ticking_the_box_without_an_old_start_is_the_plain_change_and_says_nothing_more()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.Store.DeleteRun(RunSwitch.OldName);
            rig.Refresh();

            rig.Click(true);

            Assert.Equal(Expected, rig.Store.ReadRun(PartyLogon.ValueName)?.Text);
            Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            Assert.False(File.Exists(rig.Journal));
        });
    }

    [Fact]
    public void A_switch_that_is_rolled_back_says_so_and_leaves_the_old_start_alone()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.Refresh();
            rig.Store.FailOn(RunStoreCall.WriteApproval, new IOException("injected"));

            rig.Click(true);

            Assert.Equal(OldCommand, rig.Store.ReadRun(RunSwitch.OldName)?.Text);
            Assert.Null(rig.Store.ReadRun(PartyLogon.ValueName));
            Assert.False(rig.Page.PartyLogonBox.Checked);
            Assert.Equal(NotSwitched, rig.Page.PartyLogonLineText);
            Assert.False(rig.Context.LogonNeedsCheck);
            Assert.False(File.Exists(rig.Journal));
        });
    }

    [Fact]
    public void A_switch_whose_restore_fails_shows_the_check_line()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.Refresh();
            rig.Store.FailOn(RunStoreCall.DeleteRun, new IOException("injected"));

            rig.Click(true);

            Assert.Equal(Check, rig.Page.PartyLogonLineText);
            Assert.True(rig.Context.LogonNeedsCheck);
            Assert.True(File.Exists(rig.Journal), "the journal is kept so that the next start tries again");
        });
    }

    [Fact]
    public void An_old_start_with_nowhere_to_keep_the_journal_is_not_started_twice()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.Context.PartyRoamingRoot = null;   // the roaming folder is not known: no journal path
            rig.Refresh();

            rig.Click(true);

            Assert.Null(rig.Store.ReadRun(PartyLogon.ValueName));
            Assert.Equal(OldCommand, rig.Store.ReadRun(RunSwitch.OldName)?.Text);
            Assert.Equal("Could not change the sign-in setting", rig.Page.PartyLogonLineText);
        });
    }

    // --- item 1: the owner's click settles a stuck journal ---

    [Fact]
    public void Switching_off_settles_a_stuck_journal_and_a_later_recovery_does_not_undo_it()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.MakeStuckJournal();
            rig.Context.LogonNeedsCheck = true;
            rig.Refresh();
            Assert.True(rig.Page.PartyLogonBox.Checked, "the entry of the interrupted switch is still there");
            Assert.Equal(Check, rig.Page.PartyLogonLineText);

            rig.Click(false);

            Assert.Null(rig.Store.ReadRun(PartyLogon.ValueName));
            Assert.False(File.Exists(rig.Journal));
            Assert.False(rig.Context.LogonNeedsCheck);
            Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(rig.Store, rig.Journal, _ => true).Status);
            Assert.Null(rig.Store.ReadRun(PartyLogon.ValueName));
        });
    }

    [Fact]
    public void Switching_on_settles_a_stuck_journal_and_a_later_recovery_does_not_undo_it()
    {
        Ui.Run(_ =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            rig.MakeStuckJournal();
            rig.Context.LogonNeedsCheck = true;
            rig.Store.DeleteRun(PartyLogon.ValueName);   // the owner took our entry off in Windows: the box is off, the journal stuck
            rig.Refresh();
            Assert.False(rig.Page.PartyLogonBox.Checked);

            rig.Click(true);

            Assert.Equal(Expected, rig.Store.ReadRun(PartyLogon.ValueName)?.Text);
            Assert.Equal("\"C:\\Somewhere\\Else.exe\"", rig.Store.ReadRun(RunSwitch.OldName)?.Text);   // the owner's own entry is not ours to touch
            Assert.False(File.Exists(rig.Journal));
            Assert.False(rig.Context.LogonNeedsCheck);
            Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            Assert.Equal(RunSwitchStatus.NothingToDo, RunSwitch.Recover(rig.Store, rig.Journal, _ => true).Status);
            Assert.Equal(Expected, rig.Store.ReadRun(PartyLogon.ValueName)?.Text);
        });
    }

    // --- item 3: Busy at start ---

    [Fact]
    public void A_busy_lock_at_start_is_tried_again_in_the_background_and_the_recovery_then_runs()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path, retryDelay: TimeSpan.FromMilliseconds(100));
            rig.MakeStuckJournal();
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var held = RunSwitchLock.TryAcquire(rig.LockName, TimeSpan.Zero);
            Assert.NotNull(held);

            var done = form.RecoverLogonAsync();
            var watch = Stopwatch.StartNew();
            Assert.True(ui.Until(() => watch.ElapsedMilliseconds >= 120));
            Assert.False(done.IsCompleted, "the first try found the lock busy and the recovery is waiting for the next one");
            Assert.False(rig.Context.LogonNeedsCheck);
            held!.Dispose();

            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.True(rig.Context.LogonNeedsCheck, "a later try got the lock and found the journal that needs a check");
        });
    }

    [Fact]
    public void A_lock_that_stays_busy_ends_after_four_tries_and_leaves_the_journal_for_the_next_start()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path, retryDelay: TimeSpan.FromMilliseconds(10));
            rig.MakeStuckJournal();
            var before = rig.Store.Calls.Count;
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var held = RunSwitchLock.TryAcquire(rig.LockName, TimeSpan.Zero);
            Assert.NotNull(held);
            try
            {
                var watch = Stopwatch.StartNew();
                var done = form.RecoverLogonAsync();
                Assert.True(ui.Until(() => done.IsCompleted));

                Assert.True(watch.ElapsedMilliseconds >= 3 * 10 + 3 * 40, "it waited for the lock on each of its four tries: " + watch.ElapsedMilliseconds + " ms");
                Assert.False(rig.Context.LogonNeedsCheck);
                Assert.True(File.Exists(rig.Journal));
                Assert.Equal(before, rig.Store.Calls.Count);   // nothing was read or written while the lock was not ours
            }
            finally
            {
                held!.Dispose();
            }
        });
    }
}
