using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PcMieru.Update.Transaction;
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
                File.WriteAllText(Path.Combine(Update(exe), GateFile.RecoveryFailedName), string.Empty); // the sentinel: its presence is the whole message
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

            case GateOutcome.Unreadable:
            {
                // A look that cannot be made at all: the gate path is longer than the system takes (neither "missing" nor "access denied").
                var exe = Path.Combine(_root.Path, new string('q', 33000), "PCMieru.exe");
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
    public void Every_refusal_has_a_reason_in_both_languages_and_both_themes_and_only_a_busy_gate_offers_a_retry(GateOutcome outcome, Language language, bool dark)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(dark ? ThemeMode.Dark : ThemeMode.Light);
        var entry = Refused(outcome);
        Assert.Equal(outcome, entry.Outcome);

        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"));

        Assert.False(SmokeText.IsPlaceholder(window.ReasonText), window.ReasonText);
        Assert.False(SmokeText.IsPlaceholder(window.HintText), window.HintText);
        Assert.False(string.IsNullOrWhiteSpace(window.ReasonText));
        // The reason is the whole text. The one dim line under it exists only for an interrupted update (which folder it is) and for a link in the folder (its one sentence of advice); it is not an
        // empty label that takes room under any other state.
        var hasDimLine = outcome is GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed or GateOutcome.ReparseRefused;
        Assert.Equal(hasDimLine, window.HintText.Length > 0);

        Assert.Equal(Strings.Get("gui.gate.title"), window.Text);
        Assert.Equal(outcome == GateOutcome.Busy, window.RetryOffered);
        Assert.Same(outcome == GateOutcome.Busy ? window.RetryButton : window.CloseButton, window.AcceptButton); // Enter does what the window offers
        Assert.Equal(Strings.Get("gui.common.close"), window.CloseButton.Text);

        // The reason is in the UI language (ja has Japanese letters, en has none of them).
        Assert.Equal(language == Language.Ja, window.ReasonText.Any(c => c >= '぀'));

        // Light or dark like the app.
        Assert.Equal(theme.Palette.Window, window.BackColor);
        Assert.Equal(theme.Palette.Text, window.ForeColor);

        // No text of a window names a file or a path (not even the recovery note's name; the note is named in the unattended stderr line only).
        Assert.DoesNotContain(GateFile.RecoverNoteName, window.ReasonText, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", window.ReasonText, StringComparison.Ordinal);
        Assert.Null(window.HintProblem());
        if (outcome is GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed)
        {
            // The one dim line says which folder this is: the words, then the folder of the gate that was looked at (the final path), broken only at backslashes.
            var folder = Path.GetDirectoryName(entry.GatePath)!;
            Assert.Equal(Strings.Format("gui.gate.folder", Fmt.Dict(("path", folder))), window.HintText.Replace("\n", string.Empty, StringComparison.Ordinal));
            Assert.StartsWith(language == Language.Ja ? "このフォルダー:" : "This folder:", window.HintText, StringComparison.Ordinal);
        }
    }

    // The words of every refusal, as the coordinator's usability review fixed them (it is the text of a person who is stuck: one text, what is wrong, what to do).
    public static TheoryData<GateOutcome, Language, string> TheWords() => new()
    {
        { GateOutcome.RecoveryPending, Language.Ja, "前回の更新が途中で止まったため、このフォルダーからは PCミエルを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。" },
        { GateOutcome.RecoveryPending, Language.En, "The last update did not finish, so PC Mieru cannot open from this folder. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there." },
        { GateOutcome.RecoveryFailed, Language.Ja, "前回の更新を元に戻せなかったため、このフォルダーからは PCミエルを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。" },
        { GateOutcome.RecoveryFailed, Language.En, "PC Mieru could not undo the last update, so it cannot open from this folder. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there." },
        { GateOutcome.MissingGate, Language.Ja, "起動に必要なファイルが見つからないため、PCミエルを開けません。ダウンロードした ZIP をもう一度新しいフォルダーに展開し、そこから開いてください。" },
        { GateOutcome.MissingGate, Language.En, "PC Mieru cannot open because a required file is missing. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there." },
        { GateOutcome.Denied, Language.Ja, "必要なファイルを開けないため、PCミエルを起動できません。ダウンロードした ZIP をもう一度、デスクトップなど、自分が使える場所の新しいフォルダーに展開して開いてください。それでも開けないときは、PC の管理者に相談してください。" },
        { GateOutcome.Denied, Language.En, "PC Mieru cannot open a required file. Unzip the downloaded ZIP file into a new folder in a place you can use, such as the Desktop, and open PC Mieru from there. If it still cannot open, contact the person who manages this PC." },
        { GateOutcome.Busy, Language.Ja, "PCミエルのファイルが使用中です。しばらく待ってから、もう一度試してください。" },
        { GateOutcome.Busy, Language.En, "PC Mieru's files are in use right now. Wait a moment and try again." },
        { GateOutcome.Unreadable, Language.Ja, "起動に必要なファイル PCMieru.gate.txt の状態を確かめられませんでした。ダウンロードした ZIP をもう一度、デスクトップなど、自分が使える場所の、パスの短い新しいフォルダーに展開して開いてください。それでも開けないときは、PC の管理者に相談してください。" },
        { GateOutcome.Unreadable, Language.En, "The state of PCMieru.gate.txt, which is needed to start, could not be checked. Unzip the downloaded ZIP file into a new folder with a short path in a place you can use, such as the Desktop, and open PC Mieru from there. If it still cannot open, contact the person who manages this PC." },
    };

    [Theory]
    [MemberData(nameof(TheWords))]
    public void The_window_says_exactly_the_words_that_were_fixed_for_each_refusal(GateOutcome outcome, Language language, string expected)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(ThemeMode.Light);

        using var window = new StartGateWindow(theme, Refused(outcome), () => throw new InvalidOperationException("no retry here"));

        Assert.Equal(expected, window.ReasonText);
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void A_missing_gate_does_not_say_that_the_person_deleted_or_moved_anything_and_the_unreadable_text_is_not_the_denied_text(Language language)
    {
        using var _ = Harness.Locale(language);
        foreach (var key in new[] { "gui.gate.missinggate", "gui.gate.denied", "gui.gate.unreadable", "gui.gate.recoverypending", "gui.gate.recoveryfailed", "gui.gate.recoverypending.button", "gui.gate.recoveryfailed.button", "gui.gate.recover.failed" })
        {
            var text = Strings.Get(key, language);
            foreach (var blame in language == Language.Ja ? new[] { "削除", "移動", "消し", "動かし" } : new[] { "deleted", "moved", "you deleted", "your fault" })
            {
                Assert.DoesNotContain(blame, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.NotEqual(Strings.Get("gui.gate.denied", language), Strings.Get("gui.gate.unreadable", language));
        // The unreadable text keeps its own first sentence (the file could not be checked), then gives the advice of the denied text: a new folder, then the person who manages this PC.
        var unreadable = Strings.Get("gui.gate.unreadable", language);
        Assert.Contains(language == Language.Ja ? "確かめられませんでした。" : "could not be checked.", unreadable, StringComparison.Ordinal);
        Assert.Contains(language == Language.Ja ? "PC の管理者に相談してください。" : "contact the person who manages this PC.", unreadable, StringComparison.Ordinal);
    }

    // ---- the folder line of an interrupted update ----

    private static int Ten(string text) => text.Length * 10;

    [Fact]
    public void A_folder_line_that_fits_stays_one_line_and_is_the_text_unchanged()
    {
        var wrapped = FolderLineWrap.Wrap("This folder: ", @"C:\Apps\PC Mieru", Ten, room: 300);

        Assert.Equal(@"This folder: C:\Apps\PC Mieru", wrapped);
    }

    [Fact]
    public void A_long_folder_is_broken_only_after_a_backslash_every_line_fits_and_nothing_is_lost()
    {
        const string prefix = "This folder: ";
        const string path = @"C:\Users\Administrator\AppData\Local\Temp\claude\c--Users-Administrator\scratchpad\tmpB3fix\pcmieru-smoke-gate-abcd1234\pending";

        var wrapped = FolderLineWrap.Wrap(prefix, path, Ten, room: 300);

        Assert.NotNull(wrapped);
        var lines = wrapped.Split('\n');
        Assert.True(lines.Length > 1);
        Assert.All(lines, line => Assert.True(Ten(line) <= 300, line));
        Assert.All(lines.Take(lines.Length - 1), line => Assert.EndsWith("\\", line, StringComparison.Ordinal));
        Assert.StartsWith(prefix, lines[0], StringComparison.Ordinal);
        Assert.Equal(prefix + path, string.Concat(lines));
    }

    [Fact]
    public void The_words_in_front_of_the_path_stay_alone_on_their_line_when_not_even_the_first_name_fits_after_them()
    {
        var wrapped = FolderLineWrap.Wrap("This folder: ", @"Programs-and-more\app", Ten, room: 250);

        Assert.Equal("This folder:\nPrograms-and-more\\app", wrapped);
    }

    [Fact]
    public void A_name_that_is_wider_than_a_line_by_itself_leaves_the_path_out_instead_of_being_cut_in_the_middle()
    {
        var wrapped = FolderLineWrap.Wrap("This folder: ", @"C:\Users\an-extremely-long-folder-name-that-never-fits\app", Ten, room: 300);

        Assert.Null(wrapped);
    }

    [Fact]
    public void A_network_folder_and_a_folder_with_spaces_are_broken_the_same_way()
    {
        var wrapped = FolderLineWrap.Wrap("このフォルダー: ", @"\\server\share\My Apps\PC Mieru 2\bin", Ten, room: 260);

        Assert.NotNull(wrapped);
        Assert.Equal(@"このフォルダー: \\server\share\My Apps\PC Mieru 2\bin", wrapped.Replace("\n", string.Empty, StringComparison.Ordinal));
        Assert.All(wrapped.Split('\n').Take(wrapped.Split('\n').Length - 1), line => Assert.True(line.EndsWith('\\') || line.EndsWith(':'), line));
    }

    [Theory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void The_real_window_breaks_a_deep_folder_after_backslashes_and_every_line_fits_the_label(Language language)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(ThemeMode.Light);
        var exe = Install(Path.Combine("a-folder-with-a-long-name", "and-another-one-below-it", "then-a-third-one-for-good-measure", "and-a-fourth-so-it-cannot-fit", "pending-install"));
        Directory.CreateDirectory(Update(exe));
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
        var entry = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);

        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"));

        Assert.True(window.HintText.Split('\n').Length >= 2, window.HintText);
        Assert.Null(window.HintProblem());
        Assert.Equal(Strings.Format("gui.gate.folder", Fmt.Dict(("path", Path.GetDirectoryName(entry.GatePath)!))), window.HintText.Replace("\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void The_folder_line_does_not_take_an_ampersand_in_a_folder_name_for_a_shortcut_key()
    {
        using var _ = Harness.Locale();
        using var theme = new ThemeService(ThemeMode.Light);
        var exe = Install("R&D tools");
        Directory.CreateDirectory(Update(exe));
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");

        using var window = new StartGateWindow(theme, InstallGate.EnterStartup(exe), () => throw new InvalidOperationException("no retry here"));

        Assert.False(window.HintLabel.UseMnemonic);
        Assert.Contains("R&D tools", window.HintText.Replace("\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
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
        // Not only an update holds the gate (any program that has it exclusively does), so the words do not say who is using the files.
        Assert.Equal("PC Mieru's files are in use right now. Wait a moment and try again.", window.ReasonText);

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

    [Theory]
    [InlineData(Language.Ja, "PCミエルのファイルが使用中です。しばらく待ってから、もう一度試してください。")]
    [InlineData(Language.En, "PC Mieru's files are in use right now. Wait a moment and try again.")]
    public void The_busy_window_says_that_the_files_are_in_use_right_now_and_keeps_its_button(Language language, string expected)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(ThemeMode.Light);

        using var window = new StartGateWindow(theme, Refused(GateOutcome.Busy), () => throw new InvalidOperationException("no retry here"));

        Assert.Equal(expected, window.ReasonText);
        Assert.True(window.RetryOffered);
        Assert.Equal(language == Language.Ja ? "もう一度試す" : "Try again", window.RetryButton.Text);
    }

    [Theory]
    [InlineData(Language.Ja, "起動に必要なファイル PCMieru.gate.txt の状態を確かめられませんでした。")]
    [InlineData(Language.En, "The state of PCMieru.gate.txt, which is needed to start, could not be checked.")]
    public void A_gate_file_that_could_not_be_looked_at_starts_with_its_own_sentence_it_is_not_the_access_denied_text(Language language, string firstSentence)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(ThemeMode.Light);
        var entry = Refused(GateOutcome.Unreadable);

        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"));

        Assert.Equal(GateOutcome.Unreadable, entry.Outcome);
        Assert.StartsWith(firstSentence, window.ReasonText, StringComparison.Ordinal);
        Assert.NotEqual(Strings.Get("gui.gate.denied"), window.ReasonText);
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
    [InlineData(GateOutcome.Unreadable)]
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
            // The note is the one the runner writes (I\update\RECOVER.txt, see RecoverNote): this is the path of it, as the gate reports it.
            Assert.Contains(GateFile.RecoverNoteName, error.ToString(), StringComparison.Ordinal);
            Assert.EndsWith(" (see " + Path.Combine(Path.GetDirectoryName(refused.GatePath)!, GateFile.UpdateFolderName, GateFile.RecoverNoteName) + ")" + Environment.NewLine, error.ToString(), StringComparison.Ordinal);
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

    // ---- 「回復する」 ----

    /// <summary>A recovery that is told what to answer and counts what it is asked; nothing is started.</summary>
    private sealed class FakeRecovery(bool available, bool starts = true) : IGateRecovery
    {
        public int Asked { get; private set; }

        public List<GateOutcome> Started { get; } = [];

        public bool IsAvailable(GateEntry entry)
        {
            Asked++;
            return available;
        }

        public bool Start(GateEntry entry, out string detail)
        {
            Started.Add(entry.Outcome);
            detail = starts ? "started" : "refused by the test";
            return starts;
        }
    }

    public static TheoryData<GateOutcome, Language, bool, string> TheWordsWithTheButton() => new()
    {
        { GateOutcome.RecoveryPending, Language.Ja, false, "前回の更新が途中で止まったため、このフォルダーからは PCミエルを開けません。「回復する」を押すと、途中の更新を片づけてから PCミエルを開きます。うまくいかないときは、ダウンロードした ZIP を新しいフォルダーに展開し、そこから開いてください。" },
        { GateOutcome.RecoveryPending, Language.En, false, "The last update did not finish, so PC Mieru cannot open from this folder. Press \"Recover\" to clear up the unfinished update and then open PC Mieru. If that does not work, unzip the downloaded ZIP file into a new folder and open PC Mieru from there." },
        { GateOutcome.RecoveryFailed, Language.Ja, true, "前回の更新を元に戻せなかったため、このフォルダーからは PCミエルを開けません。「回復する」を押すと、もう一度元に戻してみます。うまくいかないときは、ダウンロードした ZIP を新しいフォルダーに展開し、そこから開いてください。" },
        { GateOutcome.RecoveryFailed, Language.En, true, "PC Mieru could not undo the last update, so it cannot open from this folder. Press \"Recover\" to try putting the earlier version back once more. If that does not work, unzip the downloaded ZIP file into a new folder and open PC Mieru from there." },
    };

    [Theory]
    [MemberData(nameof(TheWordsWithTheButton))]
    public void An_interrupted_update_that_can_be_recovered_has_the_recover_button_its_words_and_the_new_folder_as_the_way_on(GateOutcome outcome, Language language, bool dark, string expected)
    {
        using var _ = Harness.Locale(language);
        using var theme = new ThemeService(dark ? ThemeMode.Dark : ThemeMode.Light);
        var recovery = new FakeRecovery(available: true);
        var entry = Refused(outcome);

        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"), recovery);

        Assert.Equal(expected, window.ReasonText);
        Assert.True(window.RecoverOffered);
        Assert.Equal(language == Language.Ja ? "回復する" : "Recover", window.RecoverButton.Text);
        Assert.Same(window.RecoverButton, window.AcceptButton); // Enter does what the window offers
        Assert.False(window.RetryOffered);
        Assert.Equal(1, recovery.Asked);
        Assert.Empty(recovery.Started); // looking at the window starts nothing
        Assert.StartsWith(language == Language.Ja ? "このフォルダー:" : "This folder:", window.HintText, StringComparison.Ordinal); // the folder line is as it was
        Assert.DoesNotContain("\\", window.ReasonText, StringComparison.Ordinal);
        Assert.DoesNotContain(GateFile.RecoverNoteName, window.ReasonText, StringComparison.Ordinal);
        Assert.Equal(theme.Palette.Window, window.BackColor);
    }

    [Theory]
    [InlineData(GateOutcome.RecoveryPending)]
    [InlineData(GateOutcome.RecoveryFailed)]
    public void Without_a_recover_exe_that_the_records_vouch_for_there_is_no_button_and_the_text_is_the_plain_one(GateOutcome outcome)
    {
        using var _ = Harness.Locale(Language.En);
        using var theme = new ThemeService(ThemeMode.Light);

        // The default recovery looks at the real folder: no I\update\recover.exe there, so nothing is offered.
        var entry = Refused(outcome);
        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"));

        Assert.False(window.RecoverOffered);
        Assert.False(window.RecoverButton.Visible);
        Assert.Equal(StartGateWindow.ReasonFor(entry), window.ReasonText);
        Assert.Same(window.CloseButton, window.AcceptButton);
        Assert.Contains("Unzip the downloaded ZIP file into a new folder", window.ReasonText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GateOutcome.Busy)]
    [InlineData(GateOutcome.MissingGate)]
    [InlineData(GateOutcome.Denied)]
    [InlineData(GateOutcome.ReparseRefused)]
    [InlineData(GateOutcome.Unreadable)]
    public void Only_an_interrupted_update_can_be_recovered_no_other_refusal_asks_or_offers_it(GateOutcome outcome)
    {
        using var _ = Harness.Locale(Language.En);
        using var theme = new ThemeService(ThemeMode.Light);
        var recovery = new FakeRecovery(available: true);

        using var window = new StartGateWindow(theme, Refused(outcome), () => throw new InvalidOperationException("no retry here"), recovery);

        Assert.False(window.RecoverOffered);
        Assert.False(window.RecoverButton.Visible);
        Assert.Equal(0, recovery.Asked);
        Assert.False(window.RecoverNow()); // the call without a button does nothing
        Assert.Empty(recovery.Started);
    }

    [Fact]
    public void A_recovery_that_says_it_is_not_available_gives_no_button()
    {
        using var _ = Harness.Locale(Language.Ja);
        using var theme = new ThemeService(ThemeMode.Light);
        var recovery = new FakeRecovery(available: false);
        var entry = Refused(GateOutcome.RecoveryPending);

        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"), recovery);

        Assert.False(window.RecoverOffered);
        Assert.Equal(StartGateWindow.ReasonFor(entry), window.ReasonText);
        Assert.Equal(1, recovery.Asked);
    }

    [Fact]
    public void Pressing_recover_hands_over_once_closes_the_window_and_does_not_open_the_gate()
    {
        using var _ = Harness.Locale(Language.En);
        using var theme = new ThemeService(ThemeMode.Light);
        var recovery = new FakeRecovery(available: true);
        using var window = new StartGateWindow(theme, Refused(GateOutcome.RecoveryFailed), () => throw new InvalidOperationException("no retry here"), recovery);

        Assert.True(window.RecoverNow());

        Assert.Equal([GateOutcome.RecoveryFailed], recovery.Started);
        Assert.True(window.RecoveryStarted);
        Assert.Null(window.Opened); // the app does not start from this window: recover.exe starts it once the install is in order
        Assert.Equal(DialogResult.Cancel, window.DialogResult);
    }

    [Fact]
    public void A_recovery_that_cannot_be_started_says_so_offers_the_new_folder_and_takes_the_button_away()
    {
        using var _ = Harness.Locale(Language.En);
        using var theme = new ThemeService(ThemeMode.Light);
        var recovery = new FakeRecovery(available: true, starts: false);
        using var window = new StartGateWindow(theme, Refused(GateOutcome.RecoveryPending), () => throw new InvalidOperationException("no retry here"), recovery);

        Assert.False(window.RecoverNow());

        Assert.Equal("The recovery program could not be started. Unzip the downloaded ZIP file into a new folder and open PC Mieru from there.", window.ReasonText);
        Assert.False(window.RecoverOffered);
        Assert.False(window.RecoverButton.Visible);
        Assert.Same(window.CloseButton, window.AcceptButton);
        Assert.False(window.RecoveryStarted);
        Assert.False(window.RecoverNow()); // once is all: there is no button to press again
        Assert.Single(recovery.Started);
    }

    [Fact]
    public void The_recovery_is_asked_once_per_entry_and_again_when_a_retry_brings_another_one()
    {
        using var _ = Harness.Locale(Language.En);
        using var theme = new ThemeService(ThemeMode.Light);
        var recovery = new FakeRecovery(available: true);
        GateEntry? latest = null;
        var exe = Install();
        var busy = InstallGate.EnterExclusive(exe);
        using var window = new StartGateWindow(theme, InstallGate.EnterStartup(exe), () => latest = InstallGate.EnterStartup(exe), recovery);
        Assert.Equal(0, recovery.Asked); // busy: nothing to recover

        busy.Dispose();
        Directory.CreateDirectory(Update(exe));
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
        Assert.False(window.RetryNow()); // the retry meets an interrupted update

        Assert.Equal(GateOutcome.RecoveryPending, latest!.Outcome);
        Assert.True(window.RecoverOffered);
        Assert.False(window.RetryOffered);
        Assert.Equal(1, recovery.Asked);
    }

    // The real recovery, on a real folder: it vouches for recover.exe by the records, and starts it with the options the runner understands.

    private sealed class RecordingStarter(bool starts = true) : IRunnerStarter
    {
        public List<(string Exe, string Folder, string[] Arguments)> Calls { get; } = [];

        public bool TryStart(string exePath, string workingFolder, IReadOnlyList<string> arguments, out string detail)
        {
            Calls.Add((exePath, workingFolder, [.. arguments]));
            detail = starts ? "started" : "refused by the test";
            return starts;
        }
    }

    /// <summary>An interrupted install (a journal in I\update) whose installed.json records a Swap.exe of these bytes; recover.exe is a file of <paramref name="onDisk"/> (null: there is none).</summary>
    private GateEntry InterruptedInstallWithRunner(byte[] recorded, byte[]? onDisk, string name = "runner")
    {
        var exe = Install(name);
        Directory.CreateDirectory(Update(exe));
        File.WriteAllText(Path.Combine(Update(exe), GateFile.JournalName), "{}");
        var record = new InstalledRecord("0.1.0", Guid.NewGuid().ToString("D"), [new InstalledFile("PCMieru.Swap.exe", recorded.Length, Convert.ToHexStringLower(SHA256.HashData(recorded)))]);
        File.WriteAllBytes(Path.Combine(Update(exe), "installed.json"), record.ToUtf8Bytes());
        if (onDisk is not null)
        {
            File.WriteAllBytes(Path.Combine(Update(exe), "recover.exe"), onDisk);
        }

        var entry = InstallGate.EnterStartup(exe);
        Assert.Equal(GateOutcome.RecoveryPending, entry.Outcome);
        return entry;
    }

    [Fact]
    public void The_real_recovery_is_available_only_for_the_recorded_recover_exe_and_starts_it_with_the_hand_over_options()
    {
        byte[] swap = [1, 2, 3, 4, 5, 6, 7, 8];
        var entry = InterruptedInstallWithRunner(swap, swap);
        var install = Path.GetDirectoryName(entry.GatePath)!;
        var starter = new RecordingStarter();
        var recovery = new RunnerGateRecovery(starter, processId: 4242);

        Assert.True(recovery.IsAvailable(entry));
        Assert.True(recovery.Start(entry, out _));

        var call = Assert.Single(starter.Calls);
        Assert.Equal(Path.Combine(install, "update", "recover.exe"), call.Exe);
        Assert.Equal(install, call.Folder);
        Assert.Equal(["--recover", "--after-pid", "4242", "--then-start"], call.Arguments);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other")]
    public void The_real_recovery_refuses_a_recover_exe_that_is_missing_or_is_not_the_recorded_file_and_starts_nothing(string what)
    {
        byte[] swap = [1, 2, 3, 4, 5, 6, 7, 8];
        var entry = InterruptedInstallWithRunner(swap, what == "missing" ? null : [9, 9, 9, 9, 9, 9, 9, 9]);
        var starter = new RecordingStarter();
        var recovery = new RunnerGateRecovery(starter, processId: 1);

        Assert.False(recovery.IsAvailable(entry));
        Assert.False(recovery.Start(entry, out var detail));
        Assert.Empty(starter.Calls);
        Assert.False(string.IsNullOrEmpty(detail));
    }

    [Fact]
    public void The_real_recovery_checks_again_when_it_starts_a_recover_exe_that_was_swapped_after_the_window_was_built()
    {
        byte[] swap = [1, 2, 3, 4, 5, 6, 7, 8];
        var entry = InterruptedInstallWithRunner(swap, swap);
        var starter = new RecordingStarter();
        var recovery = new RunnerGateRecovery(starter, processId: 1);
        Assert.True(recovery.IsAvailable(entry));

        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(entry.GatePath)!, "update", "recover.exe"), [7, 7, 7]);

        Assert.False(recovery.Start(entry, out _));
        Assert.Empty(starter.Calls);
    }

    [Fact]
    public void The_real_recovery_reports_a_start_that_failed_and_the_window_then_offers_the_new_folder()
    {
        using var _ = Harness.Locale(Language.Ja);
        using var theme = new ThemeService(ThemeMode.Light);
        byte[] swap = [1, 2, 3, 4, 5, 6, 7, 8];
        var entry = InterruptedInstallWithRunner(swap, swap);
        var starter = new RecordingStarter(starts: false);
        using var window = new StartGateWindow(theme, entry, () => throw new InvalidOperationException("no retry here"), new RunnerGateRecovery(starter, processId: 1));
        Assert.True(window.RecoverOffered);

        Assert.False(window.RecoverNow());

        Assert.Single(starter.Calls);
        Assert.Equal(Strings.Get("gui.gate.recover.failed"), window.ReasonText);
        Assert.False(window.RecoverOffered);
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
