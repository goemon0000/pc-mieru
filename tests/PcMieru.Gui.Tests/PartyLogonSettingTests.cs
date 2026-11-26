using Microsoft.Win32;
using PcMieru.Core.Siblings;

namespace PcMieru.Gui.Tests;

/// <summary>
/// DESIGN-C 3 item 5, the settings page's side: the setting "Start Parts Life at sign-in". Every test hands the page an in-memory store (or none); no test builds the real registry store, reads or
/// writes the Run key, or starts a process (the launcher is a fake). The exe path is a made-up absolute path that is never created.
/// </summary>
public sealed class PartyLogonSettingTests
{
    private const string Name = "PCMieru.Party";

    private static readonly string ExePath = Path.Combine(Path.GetTempPath(), "pcmieru-logon-setting-test", "PCMieru.Party.exe");

    private static readonly string Expected = "\"" + ExePath + "\" --logon";

    private static readonly byte[] DisabledStamp = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;

        public Rig(Language language = Language.En, bool withStore = true, bool exeThere = true)
        {
            _locale = Harness.Locale(language);
            ExeThere = exeThere;
            Services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
            Pump = new SnapshotPump(Services);
            Theme = new ThemeService(ThemeMode.Light);
            Context = new GuiContext(Services, Pump, Theme, new GuiSettings())
            {
                LogonStore = withStore ? Store : null,
                PartyExePath = ExePath,
                PartyLauncherOverride = new PartyLauncher(() => false, _ => ExeThere, (_, _) => true),
            };
            Page = new SettingsPage(Context);
        }

        public MemoryRunStore Store { get; } = new();

        public bool ExeThere { get; set; }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public ThemeService Theme { get; }

        public GuiContext Context { get; }

        public SettingsPage Page { get; }

        /// <summary>Reads the store again and pumps until the page shows the result.</summary>
        public void Refresh()
        {
            var task = Page.RefreshPartyLogon();
            Assert.True(Ui.PumpUntil(() => task.IsCompleted), "the read of the sign-in setting never ended");
        }

        /// <summary>Clicks the box (sets it, which is what a click does) and pumps until the change and its read-back have ended.</summary>
        public void Click(bool on)
        {
            Page.PartyLogonBox.Checked = on;
            var task = Page.PartyLogonPending;
            Assert.True(Ui.PumpUntil(() => task.IsCompleted), "the change of the sign-in setting never ended");
        }

        public int Writes() => Store.Calls.Count(c => c.Call is RunStoreCall.WriteRun or RunStoreCall.DeleteRun or RunStoreCall.WriteApproval or RunStoreCall.DeleteApproval);

        public void Dispose()
        {
            Page.Dispose();
            Pump.Dispose();
            Theme.Dispose();
            Services.Dispose();
            _locale.Dispose();
        }
    }

    [Fact]
    public void Without_a_store_the_box_is_off_and_unavailable_and_a_tick_does_nothing()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(withStore: false);
            Assert.Null(rig.Context.LogonStore);
            Assert.False(rig.Page.PartyLogonBox.Checked);
            Assert.False(rig.Page.PartyLogonBox.Enabled);
            Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            rig.Page.PartyLogonBox.Checked = true;
            Assert.True(rig.Page.RefreshPartyLogon().IsCompleted);
            Assert.False(rig.Page.PartyLogonBox.Enabled);
            Assert.Empty(rig.Store.Calls);
        });
    }

    [Fact]
    public void A_context_has_no_store_unless_one_is_set()
    {
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services);
        using var theme = new ThemeService(ThemeMode.Light);
        var context = new GuiContext(services, pump, theme, new GuiSettings());
        Assert.Null(context.LogonStore);
        Assert.True(Path.IsPathFullyQualified(context.PartyExePath));
        Assert.Equal(PartyLauncher.ExeName, Path.GetFileName(context.PartyExePath));
    }

    [Fact]
    public void The_setting_is_off_by_default_and_reading_it_writes_nothing()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            rig.Refresh();
            Assert.False(rig.Page.PartyLogonBox.Checked);
            Assert.True(rig.Page.PartyLogonBox.Enabled);
            Assert.Equal(PartyLogonState.Off, rig.Page.PartyLogonShown);
            Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            Assert.Equal(0, rig.Writes());
            Assert.Empty(rig.Store.RunNames);
            Assert.Empty(rig.Store.ApprovalNames);
        });
    }

    [Fact]
    public void Ticking_the_box_writes_exactly_the_expected_name_and_quoted_path_with_the_logon_argument_and_unticking_removes_it()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            rig.Refresh();
            rig.Click(true);
            Assert.Equal([Name], rig.Store.RunNames);
            var written = rig.Store.ReadRun(Name);
            Assert.NotNull(written);
            Assert.Equal(RegistryValueKind.String, written.Kind);
            Assert.Equal(Expected, written.Text);
            Assert.StartsWith("\"" + ExePath + "\" ", written.Text);
            Assert.EndsWith(" --logon", written.Text);
            Assert.Empty(rig.Store.ApprovalNames);
            Assert.True(rig.Page.PartyLogonBox.Checked);
            Assert.Equal(PartyLogonState.On, rig.Page.PartyLogonShown);
            Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);

            rig.Click(false);
            Assert.Empty(rig.Store.RunNames);
            Assert.Empty(rig.Store.ApprovalNames);
            Assert.False(rig.Page.PartyLogonBox.Checked);
            Assert.Equal(PartyLogonState.Off, rig.Page.PartyLogonShown);
        });
    }

    [Fact]
    public void The_change_runs_off_the_UI_thread()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            rig.Refresh();
            var uiThread = Environment.CurrentManagedThreadId;
            var onOtherThread = false;
            rig.Page.PartyLogonBox.Checked = true;
            // The call returned before the change ended: the work is not done on this thread (the page waits for it with an await, never with a block).
            var task = rig.Page.PartyLogonPending;
            Assert.False(rig.Page.PartyLogonBox.Enabled, "the box cannot be clicked again while a change is on its way");
            Assert.True(Ui.PumpUntil(() => task.IsCompleted));
            foreach (var (call, _) in rig.Store.Calls)
            {
                onOtherThread |= call == RunStoreCall.WriteRun;
            }

            Assert.True(onOtherThread);
            Assert.NotEqual(0, uiThread);
            Assert.True(rig.Page.PartyLogonBox.Enabled);
        });
    }

    [Fact]
    public void A_stamp_that_says_disabled_shows_the_line_in_both_languages_and_ticking_the_box_makes_the_entry_run_again()
    {
        Ui.Run(_ =>
        {
            foreach (var (language, line) in new[] { (Language.Ja, "Windows の設定で無効になっています"), (Language.En, "It is turned off in Windows settings") })
            {
                using var rig = new Rig(language);
                rig.Store.SeedRun(Name, Expected);
                rig.Store.SeedApproval(Name, DisabledStamp);
                rig.Refresh();
                Assert.Equal(PartyLogonState.DisabledInWindows, rig.Page.PartyLogonShown);
                Assert.False(rig.Page.PartyLogonBox.Checked);
                Assert.True(rig.Page.PartyLogonBox.Enabled);
                Assert.Equal(line, rig.Page.PartyLogonLineText);
                Assert.Equal(0, rig.Writes());

                rig.Click(true);
                Assert.Equal(PartyLogonState.On, rig.Page.PartyLogonShown);
                Assert.True(rig.Page.PartyLogonBox.Checked);
                Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            }
        });
    }

    [Fact]
    public void An_entry_of_another_folder_shows_its_line_in_both_languages_and_is_not_touched_by_looking()
    {
        Ui.Run(_ =>
        {
            foreach (var (language, line) in new[] { (Language.Ja, "別の場所のパーツぐらしが登録されています"), (Language.En, "A Parts Life in another folder is registered") })
            {
                using var rig = new Rig(language);
                var other = "\"" + Path.Combine(Path.GetTempPath(), "somewhere-else", "PCMieru.Party.exe") + "\" --logon";
                rig.Store.SeedRun(Name, other);
                rig.Refresh();
                Assert.Equal(PartyLogonState.OtherPlacement, rig.Page.PartyLogonShown);
                Assert.False(rig.Page.PartyLogonBox.Checked);
                Assert.Equal(line, rig.Page.PartyLogonLineText);
                Assert.Equal(other, rig.Store.ReadRun(Name)?.Text);
                Assert.Equal(0, rig.Writes());
            }
        });
    }

    [Fact]
    public void An_unreadable_key_shows_its_line_in_both_languages_the_box_is_not_usable_and_nothing_is_written()
    {
        Ui.Run(_ =>
        {
            foreach (var (language, line) in new[] { (Language.Ja, "ログオンの設定を読めませんでした"), (Language.En, "Could not read the sign-in setting") })
            {
                using var rig = new Rig(language);
                rig.Store.DenyOn(RunStoreCall.ReadRun);
                rig.Refresh();
                Assert.Equal(PartyLogonState.Unknown, rig.Page.PartyLogonShown);
                Assert.False(rig.Page.PartyLogonBox.Checked);
                Assert.False(rig.Page.PartyLogonBox.Enabled);
                Assert.Equal(line, rig.Page.PartyLogonLineText);
                Assert.Equal(0, rig.Writes());

                rig.Store.Heal();
                rig.Refresh();
                Assert.Equal(PartyLogonState.Off, rig.Page.PartyLogonShown);
                Assert.True(rig.Page.PartyLogonBox.Enabled);
                Assert.Equal(string.Empty, rig.Page.PartyLogonLineText);
            }
        });
    }

    [Theory]
    [InlineData(RunStoreCall.ReadRun)]
    [InlineData(RunStoreCall.WriteRun)]
    [InlineData(RunStoreCall.ReadApproval)]
    public void A_failure_of_one_call_when_ticking_leaves_the_box_on_what_the_store_holds_with_a_line_and_never_throws(RunStoreCall failing)
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            rig.Refresh();
            rig.Store.DenyOn(failing);
            rig.Click(true);
            Assert.True(rig.Page.PartyLogonPending.IsCompleted);
            Assert.False(rig.Page.PartyLogonPending.IsFaulted);
            // Whatever the store holds is what the box shows (the read-back may itself have been the call that failed).
            Assert.Equal(rig.Page.PartyLogonShown == PartyLogonState.On, rig.Page.PartyLogonBox.Checked);
            Assert.NotEqual(string.Empty, rig.Page.PartyLogonLineText);
            Assert.False(SmokeLikeText.IsPlaceholder(rig.Page.PartyLogonLineText));
        });
    }

    [Fact]
    public void A_failure_when_unticking_leaves_the_entry_and_says_so()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            rig.Store.SeedRun(Name, Expected);
            rig.Refresh();
            Assert.True(rig.Page.PartyLogonBox.Checked);
            rig.Store.DenyOn(RunStoreCall.DeleteRun);
            rig.Click(false);
            Assert.Equal(Expected, rig.Store.ReadRun(Name)?.Text);
            Assert.Equal(PartyLogonState.On, rig.Page.PartyLogonShown);
            Assert.True(rig.Page.PartyLogonBox.Checked);
            Assert.Equal("Could not change the sign-in setting", rig.Page.PartyLogonLineText);
        });
    }

    [Fact]
    public void Without_the_Party_exe_beside_this_one_the_box_cannot_register_it_but_can_still_remove_an_entry()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(exeThere: false);
            rig.Refresh();
            Assert.False(rig.Page.PartyLogonBox.Checked);
            Assert.False(rig.Page.PartyLogonBox.Enabled);
            Assert.Equal(Strings.Get("gui.party.missing"), rig.Page.PartyLogonLineText);
            Assert.Equal(0, rig.Writes());

            rig.Store.SeedRun(Name, Expected);
            rig.Refresh();
            Assert.True(rig.Page.PartyLogonBox.Checked);
            Assert.True(rig.Page.PartyLogonBox.Enabled);
            rig.Click(false);
            Assert.Empty(rig.Store.RunNames);
        });
    }

    [Fact]
    public void The_row_sits_after_the_network_meter_row_and_before_the_update_section_in_the_tab_order()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig();
            Assert.True(rig.Page.NetAutostartBox.TabIndex < rig.Page.PartyLogonBox.TabIndex);
            Assert.True(rig.Page.PartyLogonBox.TabIndex < rig.Page.UpdateModeBox.TabIndex);
        });
    }

    [Fact]
    public void The_strings_exist_in_both_languages_with_the_words_of_the_brief()
    {
        var all = SettingsStrings.All.ToDictionary(e => e.Key);
        Assert.Equal("ログオン時にパーツぐらしを起動", all["gui.settings.partylogon"].Ja);
        Assert.Equal("Start Parts Life at sign-in", all["gui.settings.partylogon"].En);
        Assert.Equal("Windows の設定で無効になっています", all["gui.settings.partylogon.disabled"].Ja);
        Assert.Equal("別の場所のパーツぐらしが登録されています", all["gui.settings.partylogon.other"].Ja);
        Assert.Equal("ログオンの設定を読めませんでした", all["gui.settings.partylogon.unknown"].Ja);
        foreach (var key in all.Keys.Where(k => k.StartsWith("gui.settings.partylogon", StringComparison.Ordinal)))
        {
            Assert.False(string.IsNullOrWhiteSpace(all[key].Ja), key);
            Assert.False(string.IsNullOrWhiteSpace(all[key].En), key);
        }
    }

    // --- the real store is named in one place only ---

    private static IEnumerable<string> SourceFiles(string folder) => Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    [Fact]
    public void The_real_registry_store_is_built_only_in_its_own_file_and_in_the_real_run_paths_of_the_two_Program_files()
    {
        // Nothing in src/ asks for the real store except RegistryRunStore.cs (the factory itself), the GUI's Program.cs and Party's Program.cs; no test and no smoke file builds it.
        var src = Path.Combine(Harness.Root, "src");
        string Rel(string file) => Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
        var users = SourceFiles(src)
            .Where(f => File.ReadAllText(f).Contains("RegistryRunStore", StringComparison.Ordinal))
            .Select(Rel)
            .Order(StringComparer.Ordinal)
            .ToArray();
        // (IRunStore.cs only names it in a comment; SmokeRun.Logon.cs only reads its counter, checked below; Party's Program.cs builds it in its first start and reads the counter in its smoke endings.)
        Assert.Equal(
            ["PcMieru.Core/Siblings/IRunStore.cs", "PcMieru.Core/Siblings/RegistryRunStore.cs", "PcMieru.Gui/Program.cs", "PcMieru.Gui/Smoke/SmokeRun.Logon.cs", "PcMieru.Party/Program.cs"],
            users);
        var builders = SourceFiles(src)
            .Where(f => File.ReadAllText(f).Contains("RegistryRunStore.ForCurrentUser(", StringComparison.Ordinal))
            .Select(Rel)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["PcMieru.Gui/Program.cs", "PcMieru.Party/Program.cs"], builders);

        // The smoke file only reads the counter that proves the store was never built; it does not build one.
        var smoke = File.ReadAllText(Path.Combine(Harness.GuiSource, "Smoke", "SmokeRun.Logon.cs"));
        Assert.DoesNotContain("ForCurrentUser", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("new RegistryRunStore", smoke, StringComparison.Ordinal);

        var program = File.ReadAllText(Path.Combine(Harness.GuiSource, "Program.cs"));
        Assert.Matches(@"if \(readResult\)\s*\{[^}]*LogonStore = PcMieru\.Core\.Siblings\.RegistryRunStore\.ForCurrentUser\(\);", program);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, "RegistryRunStore"));

        // No test file builds it either (this file is the only one that names the factory, in the strings of these checks).
        var tests = SourceFiles(Path.Combine(Harness.Root, "tests"))
            .Where(f => !f.EndsWith("PartyLogonSettingTests.cs", StringComparison.Ordinal)
                && (File.ReadAllText(f).Contains("RegistryRunStore.ForCurrentUser", StringComparison.Ordinal) || File.ReadAllText(f).Contains("new RegistryRunStore", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(tests);
    }

    [Fact]
    public void Party_builds_the_real_store_once_inside_FirstStart_which_only_a_real_start_reaches()
    {
        var program = File.ReadAllText(Path.Combine(Harness.Root, "src", "PcMieru.Party", "Program.cs"));

        // One construction site, and it lies inside the body of FirstStart (not in a helper that something else could call).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"RegistryRunStore\.ForCurrentUser\("));
        var first = program.IndexOf("private static bool FirstStart(", StringComparison.Ordinal);
        var site = program.IndexOf("RegistryRunStore.ForCurrentUser(", StringComparison.Ordinal);
        var next = program.IndexOf("    private static ", first + 10, StringComparison.Ordinal);
        Assert.True(first > 0 && site > first && site < next, $"the real store must be built inside FirstStart ({first} < {site} < {next})");

        // FirstStart is called from one place only, and only when this is not a smoke run (it is the same guard that keeps the first-start flow off the real folders in a smoke run).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"FirstStart\(language"));
        Assert.Matches(@"if \(!options\.Smoke && !FirstStart\(language, out loaded\)\)", program);

        // Both smoke endings (the plain run and the import previews) fail when the real store was built.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(program, @"failure \?\?= RealStoreBuiltInSmoke\(\);").Count);
        Assert.Contains("RegistryRunStore.Constructed == 0", program, StringComparison.Ordinal);

        // The store is only handed to the flow: recovery before the first-start flow, the question after it. (Party.Core names no registry class either: it is handed the store.)
        Assert.Contains("RunSwitchFlow.RecoverAtStart(runStore", program, StringComparison.Ordinal);
        Assert.Contains("RunSwitchFlow.AfterImport(report, runStore", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_store_has_not_been_built_by_this_test_run()
    {
        Assert.Equal(0, RegistryRunStore.Constructed);
    }

    /// <summary>The placeholder test of the smoke run, without the smoke run (a text that is empty or still a key name).</summary>
    private static class SmokeLikeText
    {
        public static bool IsPlaceholder(string text) => string.IsNullOrWhiteSpace(text) || text.StartsWith("gui.", StringComparison.Ordinal);
    }
}
