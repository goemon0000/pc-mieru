using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PcMieru.Wire.Gate;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The start gate of PCMieru.exe as the person meets it (DESIGN-B 4, step 3): the reason window, its words in ja and en and in light and dark, 「もう一度試す」 for a busy gate,
/// and the unattended ways (stderr for --smoke, a log line when a test answers for dialogs). The gate itself is the real one, on folders under a temporary root; no window is shown.
/// </summary>
public sealed class StartGateScreenTests : IDisposable
{
    private readonly Ui.TempRoot _root = new();
    private readonly List<Action> _undo = [];

    public void Dispose()
    {
        foreach (var undo in Enumerable.Reverse(_undo))
        {
            try
            {
                undo();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        _root.Dispose();
    }

    /// <summary>An install folder with an exe (a stand-in file) and, by default, the gate file. Returns the path of the exe.</summary>
    private string Install(string name = "app", bool gate = true)
    {
        var folder = Path.Combine(_root.Path, name);
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "PCMieru.exe");
        File.WriteAllText(exe, "not a program");
        if (gate)
        {
            File.WriteAllBytes(Path.Combine(folder, GateFile.FileName), GateFile.Bytes());
        }

        return exe;
    }

    private static string Update(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, GateFile.UpdateFolderName);

    /// <summary>mklink /J without elevation: a junction needs no privilege.</summary>
    private void Junction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        _undo.Add(() => Directory.Delete(link)); // removes the junction only, never the target
    }

    /// <summary>Denies reading the file to the current user (the owner can still change the rights back, which the cleanup does).</summary>
    private void DenyRead(string file)
    {
        var info = new FileInfo(file);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        _undo.Add(() =>
        {
            var back = info.GetAccessControl();
            back.RemoveAccessRule(rule);
            info.SetAccessControl(back);
        });
    }

    /// <summary>The refused entry of each outcome but Open, made the real way. The holder (for Busy) lives until the test ends.</summary>
    private GateEntry Refused(GateOutcome outcome)
    {
        switch (outcome)
        {
            case GateOutcome.Busy:
            {
                var exe = Install("busy");
                var holder = InstallGate.EnterExclusive(exe);
                Assert.True(holder.IsOpen);
                _undo.Add(holder.Dispose);
                return InstallGate.EnterStartup(exe);
            }

            case GateOutcome.RecoveryPending:
            {
                var exe = Install("pending");
                Directory.CreateDirectory(Update(exe));
                File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
                return InstallGate.EnterStartup(exe);
            }

            case GateOutcome.RecoveryFailed:
            {
                var exe = Install("failed");
                Directory.CreateDirectory(Update(exe));
                File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
                File.WriteAllText(Path.Combine(Update(exe), GateFile.LastResultName), "{ \"result\": " + GateFile.RecoveryFailedToken + " }");
                return InstallGate.EnterStartup(exe);
            }

            case GateOutcome.MissingGate:
                return InstallGate.EnterStartup(Install("nogate", gate: false));

            case GateOutcome.Denied:
            {
                var exe = Install("denied");
                DenyRead(Path.Combine(Path.GetDirectoryName(exe)!, GateFile.FileName));
                return InstallGate.EnterStartup(exe);
            }

            case GateOutcome.ReparseRefused:
            {
                var exe = Install("linked");
                var elsewhere = Path.Combine(_root.Path, "elsewhere");
                Directory.CreateDirectory(elsewhere);
                Junction(Update(exe), elsewhere);
                return InstallGate.EnterStartup(exe);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    public static TheoryData<GateOutcome, Language, bool> Everything()
    {
        var data = new TheoryData<GateOutcome, Language, bool>();
        foreach (var outcome in Enum.GetValues<GateOutcome>().Where(o => o != GateOutcome.Open))
        {
            foreach (var language in new[] { Language.Ja, Language.En })
            {
                foreach (var dark in new[] { false, true })
                {
                    data.Add(outcome, language, dark);
                }
            }
        }

        return data;
    }

    // ---- the window ----

    [Theory]
    [MemberData(nameof(Everything))]
    public void Every_refusal_has_a_reason_and_a_hint_in_both_languages_and_both_themes_and_only_a_busy_gate_offers_a_retry(GateOutcome outcome, Language language, bool dark)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(dark ? ThemeMode.Dark : ThemeMode.Light);
        var entry = Refused(outcome);
        Assert.Equal(outcome, entry.Outcome);

        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"));

        Assert.False(SmokeText.IsPlaceholder(window.ReasonText), window.ReasonText);
        Assert.False(SmokeText.IsPlaceholder(window.HintText), window.HintText);
        Assert.False(string.IsNullOrWhiteSpace(window.ReasonText));
        Assert.False(string.IsNullOrWhiteSpace(window.HintText));
        Assert.Equal(Strings.Get("gui.gate.title"), window.Text);
        Assert.Equal(outcome == GateOutcome.Busy, window.RetryOffered);
        Assert.Same(outcome == GateOutcome.Busy ? window.RetryButton : window.CloseButton, window.AcceptButton); // Enter does what the window offers
        Assert.Equal(Strings.Get("gui.common.close"), window.CloseButton.Text);

        // The reason is in the UI language (ja has Japanese letters, en has none of them).
        Assert.Equal(language == Language.Ja, window.ReasonText.Any(c => c >= '぀'));

        // Light or dark like the app.
        Assert.Equal(theme.Palette.Window, window.BackColor);
        Assert.Equal(theme.Palette.Text, window.ForeColor);

        if (outcome is GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed)
        {
            Assert.EndsWith(Path.Combine("update", GateFile.RecoverNoteName), window.ReasonText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_busy_gate_offers_to_try_again_and_the_retry_opens_the_gate_when_it_has_been_freed()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var exe = Install();
        var holder = InstallGate.EnterExclusive(exe);
        var first = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.Busy, first.Outcome);

        using var window = new StartGateWindow(theme, first, () => InstallGate.EnterStartup(exe));
        Assert.True(window.RetryOffered);
        Assert.Equal(Strings.Get("gui.gate.retry"), window.RetryButton.Text);

        // Still held: the window stays, says the same, and nothing is open.
        Assert.False(window.RetryNow());
        Assert.Null(window.Opened);
        Assert.Equal(Strings.Get("gui.gate.busy"), window.ReasonText);
        Assert.True(window.RetryOffered);

        holder.Dispose();
        Assert.True(window.RetryNow());
        Assert.NotNull(window.Opened);
        Assert.True(window.Opened.IsOpen);
        Assert.Equal(DialogResult.OK, window.DialogResult);

        // The entry that came back holds the gate (the swapper cannot take it) until the app lets go.
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(exe).Outcome);
        window.Opened.Dispose();
        using var after = InstallGate.EnterExclusive(exe);
        Assert.True(after.IsOpen);
    }

    [Fact]
    public void A_retry_that_meets_another_problem_shows_that_problem_and_drops_the_retry_button()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var exe = Install();
        var holder = InstallGate.EnterExclusive(exe);
        using var window = new StartGateWindow(theme, InstallGate.EnterStartup(exe), () => InstallGate.EnterStartup(exe));
        holder.Dispose();
        File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, GateFile.FileName));

        Assert.False(window.RetryNow());

        Assert.Equal(Strings.Get("gui.gate.missinggate"), window.ReasonText);
        Assert.False(window.RetryOffered);
    }

    // ---- Enter: the three ways out ----

    [Fact]
    public void A_free_gate_gives_the_entry_and_no_window_and_no_output()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var exe = Install();
        var error = new StringWriter();
        Dialogs.Log.Clear();

        using var entry = StartGateScreen.Enter(theme, unattended: false, error, () => InstallGate.EnterStartup(exe));

        Assert.NotNull(entry);
        Assert.True(entry.IsOpen);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Empty(Dialogs.Log);
        Assert.Equal(GateOutcome.Busy, InstallGate.EnterExclusive(exe).Outcome); // held for as long as the entry lives
    }

    [Theory]
    [InlineData(GateOutcome.Busy)]
    [InlineData(GateOutcome.MissingGate)]
    [InlineData(GateOutcome.RecoveryPending)]
    public void An_unattended_run_never_waits_on_a_window_the_reason_goes_to_stderr_and_there_is_no_entry(GateOutcome outcome)
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var refused = Refused(outcome);
        var error = new StringWriter();

        var entry = StartGateScreen.Enter(theme, unattended: true, error, () => refused);

        Assert.Null(entry);
        Assert.StartsWith("PCMieru: " + InstallGate.ReasonOf(outcome), error.ToString(), StringComparison.Ordinal);
        if (outcome == GateOutcome.RecoveryPending)
        {
            Assert.Contains(GateFile.RecoverNoteName, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void When_a_test_answers_for_dialogs_the_window_is_not_opened_and_the_reason_is_logged()
    {
        using var _ = Harness.Locale(Language.Ja);
        using var theme = new ThemeService(ThemeMode.Dark);
        var refused = Refused(GateOutcome.MissingGate);
        Dialogs.Log.Clear();
        Dialogs.AutoAnswer = false;
        try
        {
            var entry = StartGateScreen.Enter(theme, unattended: false, TextWriter.Null, () => refused);

            Assert.Null(entry);
            Assert.Equal([Strings.Get("gui.gate.title") + " | " + Strings.Get("gui.gate.missinggate")], Dialogs.Log);
        }
        finally
        {
            Dialogs.AutoAnswer = null;
        }
    }

    // ---- the words ----

    [Fact]
    public void The_gate_strings_exist_for_every_outcome_that_has_a_window()
    {
        using var _ = Harness.Locale();
        foreach (var language in new[] { Language.Ja, Language.En })
        {
            foreach (var outcome in Enum.GetValues<GateOutcome>().Where(o => o != GateOutcome.Open))
            {
                var key = "gui.gate." + outcome.ToString().ToLowerInvariant();
                Assert.False(SmokeText.IsPlaceholder(Strings.Get(key, language)), key);
            }
        }
    }
}
