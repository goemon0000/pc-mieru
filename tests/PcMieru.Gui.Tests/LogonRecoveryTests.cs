using System.Text.RegularExpressions;
using PcMieru.Core.Siblings;

namespace PcMieru.Gui.Tests;

/// <summary>
/// DESIGN-C 4.3, the main window's side of the sign-in switch: at a real start it puts back what an unfinished switch left, off the UI thread and without a dialog; only a switch that cannot be
/// settled shows a line on the setting. Every test hands the window an in-memory store and a temporary roaming folder; nothing names the real registry or the owner's folders.
/// </summary>
public sealed class LogonRecoveryTests
{
    private const string Check = "Please check the sign-in setting";

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly bool? _auto;

        public Rig(string? roamingRoot, bool withStore = true)
        {
            _locale = Harness.Locale(Language.En);
            _auto = Dialogs.AutoAnswer;
            Dialogs.AutoAnswer = false;   // a dialog never opens and never waits for a click
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Context = new GuiContext(Services, Pump, new ThemeService(ThemeMode.Light), new GuiSettings())
            {
                PartyRoamingRoot = roamingRoot,
                LogonStore = withStore ? Store : null,
                PartyExePath = Path.Combine(Path.GetTempPath(), "pcmieru-logon-recovery-test", "PCMieru.Party.exe"),
                PartyLauncherOverride = new PartyLauncher(() => false, _ => true, (_, _) => true),
            };
        }

        public MemoryRunStore Store { get; } = new();

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiContext Context { get; }

        public int Writes() => Store.Calls.Count(c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);

        public void Dispose()
        {
            Dialogs.AutoAnswer = _auto;
            Pump.Dispose();
            Services.Dispose();
            _locale.Dispose();
        }
    }

    private static ToneLabel LineOf(SettingsPage page) => Assert.IsType<ToneLabel>(Assert.Single(page.Controls.Find("settingsPartyLogonLine", searchAllChildren: true)));

    private static string JournalIn(string root) => RunSwitch.JournalPathFor(Path.Combine(root, "PCMieru", "party"));

    /// <summary>A journal that cannot be read: the recovery cannot tell what a switch left, so it touches nothing and says "check" (NeedsUserCheck).</summary>
    private static void WriteUnreadableJournal(string root)
    {
        var journal = JournalIn(root);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "this is not a journal");
    }

    [Fact]
    public void A_journal_that_cannot_be_settled_shows_the_line_on_the_setting_and_writes_nothing()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            WriteUnreadableJournal(roaming.Path);
            using var rig = new Rig(roaming.Path);
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var logged = Dialogs.Log.Count;

            var done = form.RecoverLogonAsync();
            Assert.True(ui.Until(() => done.IsCompleted));

            Assert.True(rig.Context.LogonNeedsCheck);
            Assert.Equal(logged, Dialogs.Log.Count);   // a status line, never a dialog
            Assert.Equal(0, rig.Writes());
            Assert.True(File.Exists(JournalIn(roaming.Path)), "the journal is kept for the next start");

            // a page made after the recovery ended (the usual order) shows the line when it reads the store (a page of its own here: one inside the unshown window reports Visible false)
            using var page = new SettingsPage(rig.Context);
            var read = page.RefreshPartyLogon();
            Assert.True(ui.Until(() => read.IsCompleted));
            Assert.Equal(Check, page.PartyLogonLineText);
            Assert.Equal(CellTone.Warning, page.PartyLogonLineTone);
        });
    }

    [Fact]
    public void A_settings_page_that_is_already_open_shows_the_line_as_soon_as_the_recovery_ends()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            WriteUnreadableJournal(roaming.Path);
            using var rig = new Rig(roaming.Path);
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var page = (SettingsPage)form.EnsurePage("settings")!;
            var first = page.RefreshPartyLogon();
            Assert.True(ui.Until(() => first.IsCompleted));
            Assert.Equal(string.Empty, page.PartyLogonLineText);

            var done = form.RecoverLogonAsync();
            Assert.True(ui.Until(() => done.IsCompleted));

            // the label's own text and flag (the page sits in a window that was never shown, where Visible reads false whatever is set)
            Assert.Equal(Check, LineOf(page).Text);
            Assert.Equal(CellTone.Warning, LineOf(page).Tone);
        });
    }

    [Fact]
    public void With_no_journal_nothing_is_shown_and_nothing_is_written()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            using var rig = new Rig(roaming.Path);
            using var form = new MainForm(rig.Context);
            _ = form.Handle;
            var logged = Dialogs.Log.Count;

            var done = form.RecoverLogonAsync();
            Assert.True(ui.Until(() => done.IsCompleted));

            Assert.False(rig.Context.LogonNeedsCheck);
            Assert.Equal(logged, Dialogs.Log.Count);
            Assert.Equal(0, rig.Writes());
            var page = (SettingsPage)form.EnsurePage("settings")!;
            var read = page.RefreshPartyLogon();
            Assert.True(ui.Until(() => read.IsCompleted));
            Assert.Equal(string.Empty, LineOf(page).Text);
        });
    }

    [Fact]
    public void Without_a_store_or_without_a_roaming_folder_the_recovery_looks_nowhere()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            WriteUnreadableJournal(roaming.Path);

            // a run that names no store (a test, a measurement): the journal is not even read
            using (var rig = new Rig(roaming.Path, withStore: false))
            {
                using var form = new MainForm(rig.Context);
                _ = form.Handle;
                var done = form.RecoverLogonAsync();
                Assert.True(ui.Until(() => done.IsCompleted));
                Assert.False(rig.Context.LogonNeedsCheck);
            }

            // a run that names no roaming folder: the store is not asked anything
            using (var rig = new Rig(null))
            {
                using var form = new MainForm(rig.Context);
                _ = form.Handle;
                var done = form.RecoverLogonAsync();
                Assert.True(ui.Until(() => done.IsCompleted));
                Assert.False(rig.Context.LogonNeedsCheck);
                Assert.Empty(rig.Store.Calls);
            }
        });
    }

    [Fact]
    public void A_store_that_fails_is_never_a_crash_and_the_window_stays_usable()
    {
        Ui.Run(ui =>
        {
            using var roaming = new Ui.TempRoot();
            WriteUnreadableJournal(roaming.Path);
            using var rig = new Rig(roaming.Path);
            rig.Store.DenyOn(RunStoreCall.ReadRun);
            using var form = new MainForm(rig.Context);
            _ = form.Handle;

            var done = form.RecoverLogonAsync();
            Assert.True(ui.Until(() => done.IsCompleted));
            Assert.False(done.IsFaulted);
            Assert.Equal(0, rig.Writes());
        });
    }

    [Fact]
    public void The_recovery_is_started_by_StartUpdates_only_which_a_smoke_run_and_a_measurement_never_call()
    {
        var source = File.ReadAllText(Path.Combine(Harness.GuiSource, "Shell", "MainForm.cs"));
        Assert.Single(Regex.Matches(source, @"LogonRecovery = RecoverLogonAsync\(\);"));
        var start = source.IndexOf("internal void StartUpdates()", StringComparison.Ordinal);
        var end = source.IndexOf("internal Task LogonRecovery", StringComparison.Ordinal);
        Assert.InRange(source.IndexOf("LogonRecovery = RecoverLogonAsync();", StringComparison.Ordinal), start + 1, end);
        Assert.Contains("if (IsDisposed || _updatesStarted || _context.Verifying)", source[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_has_both_languages()
    {
        var all = SettingsStrings.All.ToDictionary(e => e.Key);
        Assert.Equal("ログオン設定の復元を確認してください", all["gui.settings.partylogon.check"].Ja);
        Assert.Equal(Check, all["gui.settings.partylogon.check"].En);
    }
}
