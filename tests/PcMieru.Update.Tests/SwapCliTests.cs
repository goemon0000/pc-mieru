using System.Globalization;
using System.Reflection;
using PcMieru.Swap.TestBuild;
using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Tests;

/// <summary>A starter that starts nothing and remembers what it was asked to start.</summary>
internal sealed class FakeStarter : IRunnerStarter
{
    public List<(string Exe, string Folder, string[] Arguments)> Starts { get; } = [];

    public bool Succeeds { get; set; } = true;

    public Action<string>? OnStart { get; set; }

    public bool TryStart(string exePath, string workingFolder, IReadOnlyList<string> arguments, out string detail)
    {
        Starts.Add((exePath, workingFolder, [.. arguments]));
        OnStart?.Invoke(exePath);
        detail = Succeeds ? "started" : "the fake refused";
        return Succeeds;
    }
}

/// <summary>
/// <c>PCMieru.Swap.exe</c> as a library (DESIGN-B 5, 13 item 3): the command line, the two roles (Runner = <c>I\update\recover.exe</c>, Launcher = <c>I\PCMieru.Swap.exe</c>), the waits (the process that handed over,
/// the gate), <c>--then-start</c>, the refusal of an elevated token, and every exit code. In-process, on scratch folders, with a fake starter: nothing is started and no real install is looked at.
/// <c>Run</c> of the harness is the PRODUCT's program (the shipped exe); <c>RunTestBuild</c> is the program of the TEST BUILD of the runner, whose test-only entry (<see cref="TestEntry"/>, the file that exe is made from) is
/// what gives a swap its scripted verdict and its stop at a named step. The product has no such entry: TestBuildSeparationTests and tools/publish.ps1 (on the built exe) say so.
/// The real exe is exercised by the trial script (with the test build) and by the smoke run of the publish folder.
/// </summary>
public sealed class SwapCliTests
{
    private const int OwnPid = 4711;

    private sealed class Harness(Rig rig, string self)
    {
        public StringWriter Out { get; } = new();

        public StringWriter Err { get; } = new();

        public FakeStarter Starter { get; } = new();

        public Dictionary<string, string> Environment { get; } = [];

        public List<(int Pid, TimeSpan Limit)> Waits { get; } = [];

        public List<TimeSpan> Sleeps { get; } = [];

        public Func<int, bool> PidEnded { get; set; } = static _ => true;

        public Action<int>? OnWait { get; set; }

        public Action<int>? OnSleep { get; set; }

        public bool Alone { get; set; }

        public TxSeams Seams { get; set; } = TxSeams.Production();

        public TimeSpan SwapGateWait { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan RecoverGateWait { get; set; } = TimeSpan.FromSeconds(1);

        public Func<string, string?>? GetVariable { get; set; }

        /// <summary>The environment of this harness as the test build's switches read it (<see cref="Environment"/>, a dictionary: nothing of the real environment is looked at).</summary>
        public string? Lookup(string name) => GetVariable is { } get ? get(name) : Environment.TryGetValue(name, out var value) ? value : null;

        /// <summary>What the host says to start I\PCMieru.exe with at the end: nothing, like the product's runner, unless a test (standing for the test build's Program.cs) fills it from <see cref="TestEntry.ProgramArguments"/>.</summary>
        public IReadOnlyList<string> ProgramArguments { get; set; } = [];

        public string Self { get; set; } = self;

        /// <summary>What the process says about its token. False unless a test says otherwise, whatever the shell the tests are run from.</summary>
        public bool Elevated { get; set; }

        /// <summary>The PRODUCT's program (what the shipped PCMieru.Swap.exe runs).</summary>
        public int Run(params string[] args) => SwapCli.Run(args, Host());

        /// <summary>The program of the TEST BUILD of the runner: the test-only entry first (tests/PcMieru.Swap.TestBuild/TestEntry.cs, the very file the test build exe is made from), the product's program for everything else.</summary>
        public int RunTestBuild(params string[] args) =>
            TestEntry.Run(args, Host(), GetVariable ?? (name => Environment.TryGetValue(name, out var value) ? value : null));

        private SwapHost Host()
        {
            return new SwapHost
            {
                SelfPath = Self,
                Out = Out,
                Error = Err,
                IsElevated = () => Elevated,
                ProgramArguments = ProgramArguments,
                ProcessId = OwnPid,
                Seams = Seams,
                Starter = Starter,
                WaitForProcessEnd = (pid, limit) =>
                {
                    Waits.Add((pid, limit));
                    OnWait?.Invoke(pid);
                    return PidEnded(pid);
                },
                Sleep = span =>
                {
                    Sleeps.Add(span);
                    OnSleep?.Invoke(Sleeps.Count);
                },
                ConsoleIsAlone = () => Alone,
                SwapGateWait = SwapGateWait,
                RecoverGateWait = RecoverGateWait,
                AfterPidWait = TimeSpan.FromSeconds(30),
                ReadingPause = TimeSpan.FromSeconds(7),
            };
        }

        public string[] TestSwap(string verdict, string step = "-", string edge = "Before", string nth = "1") =>
            ["--test-swap", rig.Staged, step, edge, nth, Path.Combine(rig.Root, "marker.txt"), verdict];

        public void SwitchOn() => Environment[TestEntry.Variable] = rig.Install;
    }

    private static Harness Runner(Rig rig) => new(rig, rig.Paths.Recover);

    private static Harness Launcher(Rig rig) => new(rig, Path.Combine(rig.Install, "PCMieru.Swap.exe"));

    private static void CutSwap(Rig rig, TxStep step, TxEdge edge)
    {
        var cutter = new Cutter(new CutPoint(step, edge, 1));
        Assert.Throws<PowerCutException>(() => rig.Swap(Rig.Seams(cutter, FakeLauncher.Ok())));
        Assert.True(cutter.Fired, "the power was never cut");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the codes

    [Fact]
    public void The_exit_codes_are_distinct_and_all_of_them_are_in_the_usage_text()
    {
        var codes = typeof(SwapExit).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(int)).Select(f => (Name: f.Name, Value: (int)f.GetRawConstantValue()!)).ToList();

        Assert.Equal(13, codes.Count);
        Assert.Equal(codes.Count, codes.Select(c => c.Value).Distinct().Count());
        foreach (var (name, value) in codes)
        {
            Assert.Contains(value.ToString(CultureInfo.InvariantCulture) + " ", SwapCli.Usage, StringComparison.Ordinal);
            Assert.True(value is >= 0 and < 100, name);
        }
    }

    [Fact]
    public void An_outcome_of_the_engine_has_its_own_code_and_only_four_say_the_install_can_be_started()
    {
        Assert.Equal(SwapExit.NothingToDo, SwapExit.For(TxOutcome.NothingToDo));
        Assert.Equal(SwapExit.Committed, SwapExit.For(TxOutcome.Committed));
        Assert.Equal(SwapExit.RolledBack, SwapExit.For(TxOutcome.RolledBack));
        Assert.Equal(SwapExit.Aborted, SwapExit.For(TxOutcome.Aborted));
        Assert.Equal(SwapExit.RecoveryFailed, SwapExit.For(TxOutcome.RecoveryFailed));
        Assert.Equal(new[] { 0, 10, 11, 12 }, Enumerable.Range(0, 100).Where(SwapExit.InstallUsable).ToArray());
    }

    [Fact]
    public void Every_outcome_and_reason_has_a_plain_sentence_in_both_languages_and_none_has_an_address()
    {
        foreach (var outcome in Enum.GetValues<TxOutcome>())
        {
            foreach (var reason in Enum.GetValues<TxReason>())
            {
                var result = new TxResult(outcome, reason, string.Empty) { ToVersion = "0.5.1" };
                var ja = SwapSentences.Ja(result);
                var en = SwapSentences.En(result);
                if (outcome is not TxOutcome.Aborted || TxReasonText.Ja(reason).Length > 0)
                {
                    Assert.False(string.IsNullOrWhiteSpace(ja), $"no Japanese sentence for {outcome}/{reason}");
                    Assert.False(string.IsNullOrWhiteSpace(en), $"no English sentence for {outcome}/{reason}");
                }

                Assert.DoesNotContain("://", ja + en, StringComparison.Ordinal);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the command line

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Help_prints_the_usage_and_changes_nothing(string flag)
    {
        using var rig = Rig.Create("cli-help", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.NothingToDo, cli.Run(flag));

        Assert.Contains("exit codes", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("--then-start", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, cli.Err.ToString());
        rig.AssertUnchanged(before, "help", includeUpdate: true);
        Assert.Empty(cli.Starter.Starts);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--recover|extra")]
    [InlineData("--after-pid")]
    [InlineData("--after-pid|abc")]
    [InlineData("--after-pid|0")]
    [InlineData("--after-pid|-5")]
    [InlineData("--after-pid|99999999999")]
    [InlineData("--test-swap")]
    [InlineData("--test-swap|s|-|Before|1|m|ok")] // the product does not know the test build's entry: it is a command line it does not understand, with the switch on or off
    [InlineData("--then-start|--test-swap|s|-|Before|1|m|ok")]
    public void A_command_line_that_is_not_understood_is_exit_2_and_changes_nothing(string line)
    {
        using var rig = Rig.Create("cli-bad", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);
        cli.SwitchOn();

        Assert.Equal(SwapExit.BadCommandLine, cli.Run(line.Split('|')));

        Assert.StartsWith("swap: ", cli.Err.ToString(), StringComparison.Ordinal);
        rig.AssertUnchanged(before, "a bad command line", includeUpdate: true);
        Assert.Empty(cli.Starter.Starts);
        Assert.Empty(cli.Waits);
    }

    [Theory]
    [InlineData("--test-swap")]
    [InlineData("--test-swap|s|-|Before|1|m")]
    [InlineData("--test-swap|s|-|Before|1|m|ok|extra")]
    [InlineData("--test-swap|s|no-such-step|Before|1|m|ok")]
    [InlineData("--test-swap|s|-|Middle|1|m|ok")]
    [InlineData("--test-swap|s|-|before|1|m|ok")]
    [InlineData("--test-swap|s|-|Before|0|m|ok")]
    [InlineData("--test-swap|s|-|Before|x|m|ok")]
    [InlineData("--test-swap|s|-|Before|1|m|maybe")]
    [InlineData("--then-start|--test-swap|s|-|Before|1|m|ok")]
    public void The_test_build_entry_refuses_a_command_line_it_does_not_understand_with_exit_2_and_changes_nothing(string line)
    {
        using var rig = Rig.Create("cli-bad-tb", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);
        cli.SwitchOn();

        Assert.Equal(SwapExit.BadCommandLine, cli.RunTestBuild(line.Split('|')));

        Assert.StartsWith("swap: ", cli.Err.ToString(), StringComparison.Ordinal);
        rig.AssertUnchanged(before, "a bad command line of the test entry", includeUpdate: true);
        Assert.Empty(cli.Starter.Starts);
        Assert.Empty(cli.Waits);
    }

    [Fact]
    public void The_test_build_passes_everything_that_is_not_its_entry_to_the_product_unchanged()
    {
        using var rig = Rig.Create("cli-tb-pass", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();

        Assert.Equal(SwapExit.NothingToDo, cli.RunTestBuild("--recover"));
        Assert.Equal(SwapExit.NothingToDo, cli.RunTestBuild());
        Assert.Equal(SwapExit.BadCommandLine, cli.RunTestBuild("--bogus"));
        Assert.Equal(SwapExit.NothingToDo, cli.RunTestBuild("--help"));
        Assert.Contains("exit codes", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--test-swap", cli.Out.ToString(), StringComparison.Ordinal); // the usage of the product does not name the test entry
    }

    [Fact]
    public void An_exe_that_is_neither_the_installed_swap_exe_nor_recover_exe_does_nothing_to_an_install()
    {
        using var rig = Rig.Create("cli-wrong", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);

        foreach (var self in new[] { Path.Combine(rig.Install, "other.exe"), Path.Combine(rig.Install, "recover.exe"), Path.Combine(rig.Install, "update", "PCMieru.exe"), Path.Combine(rig.Root, "elsewhere", "recover.exe") })
        {
            var cli = new Harness(rig, self);
            cli.SwitchOn();
            Assert.Equal(SwapExit.WrongPlace, cli.Run("--recover"));
            Assert.Equal(SwapExit.WrongPlace, cli.RunTestBuild(cli.TestSwap("ok")));
            Assert.Contains("PCMieru.Swap.exe", cli.Err.ToString(), StringComparison.Ordinal);
            Assert.Empty(cli.Starter.Starts);
        }

        rig.AssertUnchanged(before, "a Swap.exe in the wrong place", includeUpdate: true);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- an elevated token (review of step 4, S4)

    [Theory]
    [InlineData(true, "--recover")]
    [InlineData(true, "--recover|--then-start")]
    [InlineData(true, "--after-pid|4242|--recover|--then-start")]
    [InlineData(true, "")]
    [InlineData(false, "--recover")]
    [InlineData(false, "--then-start|--recover")]
    [InlineData(false, "")]
    public void An_exe_that_runs_as_administrator_neither_recovers_nor_hands_over_and_changes_nothing(bool runner, string line)
    {
        // A swap or a recovery that runs elevated leaves files that the normal start can no longer replace, and a recovery would touch files a person did not expect an administrator's program to touch.
        using var rig = Rig.Create("cli-elevated", recorded: true);
        CutSwap(rig, TxSteps.SwapOverwrite, TxEdge.After); // an open journal: exactly the situation a recovery is for
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var journal = File.ReadAllBytes(rig.Paths.Journal);
        var cli = runner ? Runner(rig) : Launcher(rig);
        cli.Elevated = true;

        var code = cli.Run(line.Length == 0 ? [] : line.Split('|'));

        Assert.Equal(SwapExit.Elevated, code);
        Assert.False(SwapExit.InstallUsable(code));
        Assert.Empty(cli.Starter.Starts); // nothing was started, not even the runner
        Assert.Empty(cli.Waits);          // and nothing was waited for
        Assert.Empty(cli.Sleeps);
        rig.AssertUnchanged(before, "an elevated exe changes nothing", includeUpdate: true);
        Assert.Equal(journal, File.ReadAllBytes(rig.Paths.Journal));
        Assert.False(File.Exists(rig.Paths.Sentinel));
        Assert.False(File.Exists(rig.Paths.LastResult), "no result is written: nothing was tried");
        Assert.Contains("Elevated", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("administrator", cli.Err.ToString(), StringComparison.Ordinal);
        Assert.Contains(TxReasonText.Ja(TxReason.Elevated), cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains(TxReasonText.En(TxReason.Elevated), cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_exe_with_a_normal_token_does_the_recovery_so_the_refusal_above_is_the_token_and_nothing_else()
    {
        using var rig = Rig.Create("cli-not-elevated", recorded: true);
        CutSwap(rig, TxSteps.SwapOverwrite, TxEdge.After);
        var cli = Runner(rig);
        cli.Elevated = false;

        Assert.Equal(SwapExit.RolledBack, cli.Run("--recover"));

        Assert.DoesNotContain("Elevated", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal(Generation.Old, rig.AssertEnded("after the recovery"));
    }

    [Fact]
    public void The_help_text_works_even_when_elevated_because_it_changes_nothing()
    {
        using var rig = Rig.Create("cli-elevated-help", recorded: true);
        var cli = Runner(rig);
        cli.Elevated = true;

        Assert.Equal(SwapExit.NothingToDo, cli.Run("--help"));
        Assert.Contains("32", cli.Out.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the test switch (the test build's entry)

    [Fact]
    public void The_test_only_entry_is_refused_without_the_switch_before_anything_is_placed_or_written()
    {
        using var rig = Rig.Create("cli-switch", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);

        foreach (var cli in new[] { Runner(rig), Launcher(rig) })
        {
            Assert.Equal(TestEntry.SwitchMissing, cli.RunTestBuild(cli.TestSwap("ok")));
            Assert.Contains(TestEntry.Variable, cli.Err.ToString(), StringComparison.Ordinal);
            Assert.Empty(cli.Starter.Starts);
            Assert.Empty(cli.Waits);
        }

        rig.AssertUnchanged(before, "the switch is off", includeUpdate: true); // not even recover.exe was placed by the Launcher
        Assert.False(File.Exists(rig.Paths.Recover));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\some\\other\\folder")]
    [InlineData("1")]
    [InlineData("true")]
    public void The_switch_names_one_folder_and_anything_else_opens_nothing(string value)
    {
        using var rig = Rig.Create("cli-switch2", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);
        cli.Environment[TestEntry.Variable] = value;

        Assert.Equal(TestEntry.SwitchMissing, cli.RunTestBuild(cli.TestSwap("ok")));

        rig.AssertUnchanged(before, "a wrong switch value", includeUpdate: true);
    }

    [Fact]
    public void The_switch_is_the_install_folder_whatever_its_spelling()
    {
        using var rig = Rig.Create("cli-switch3", recorded: true);
        var cli = Runner(rig);
        cli.Environment[TestEntry.Variable] = rig.Install.ToUpperInvariant() + Path.DirectorySeparatorChar;

        Assert.Equal(SwapExit.Committed, cli.RunTestBuild(cli.TestSwap("ok")));
        rig.AssertTree(Generation.New, "a switch spelled with another case and a trailing separator");
    }

    [Fact]
    public void A_switch_that_cannot_be_read_is_exit_3_and_nothing_changes()
    {
        using var rig = Rig.Create("cli-crash", recorded: true);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);
        cli.GetVariable = _ => throw new InvalidOperationException("the environment is gone");

        Assert.Equal(SwapExit.Crashed, cli.RunTestBuild(cli.TestSwap("ok")));

        Assert.Contains("unexpected InvalidOperationException", cli.Err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("environment is gone", cli.Err.ToString(), StringComparison.Ordinal); // the type only: a message may hold a path
        rig.AssertUnchanged(before, "a crash", includeUpdate: true);
    }

    [Fact]
    public void A_power_cut_of_a_test_is_not_swallowed_as_a_crash()
    {
        using var rig = Rig.Create("cli-cut", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();
        cli.Seams = new TxSeams { Fault = new Cutter(new CutPoint(TxSteps.JournalPrepared, TxEdge.After, 1)) };

        Assert.Throws<PowerCutException>(() => cli.RunTestBuild(cli.TestSwap("ok")));

        Assert.True(File.Exists(rig.Paths.Journal), "the cut left the journal, as a real cut does");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the test switch: the final start of the installed program

    /// <summary>A report file the switch is given. It is never created: the switch only reads its name, and the fake starter starts nothing.</summary>
    private const string ReportFile = "C:\\scratch\\smoke-report.json";

    [Fact]
    public void The_start_variable_does_nothing_unless_the_test_switch_names_this_install()
    {
        using var rig = Rig.Create("cli-start1", recorded: true);
        var cli = Runner(rig);
        cli.Environment[TestEntry.StartVariable] = ReportFile;

        Assert.Empty(TestEntry.ProgramArguments(cli.Self, cli.Lookup)); // no switch at all

        foreach (var wrong in new[] { "", "   ", "1", "true", "C:\\some\\other\\folder", Path.Combine(rig.Install, "update") })
        {
            cli.Environment[TestEntry.Variable] = wrong;
            Assert.Empty(TestEntry.ProgramArguments(cli.Self, cli.Lookup));
        }

        var elsewhere = new Harness(rig, Path.Combine(rig.Install, "other.exe")); // an exe that is not this install's runner is not given the switch either
        elsewhere.SwitchOn();
        elsewhere.Environment[TestEntry.StartVariable] = ReportFile;
        Assert.Empty(TestEntry.ProgramArguments(elsewhere.Self, elsewhere.Lookup));

        cli.SwitchOn();
        Assert.Equal(new[] { "--smoke", ReportFile, "--fakes" }, TestEntry.ProgramArguments(cli.Self, cli.Lookup));
    }

    [Fact]
    public void The_start_variable_is_read_with_the_same_spelling_of_the_install_as_the_switch_and_for_the_launcher_too()
    {
        using var rig = Rig.Create("cli-start2", recorded: true);
        foreach (var cli in new[] { Runner(rig), Launcher(rig) })
        {
            cli.Environment[TestEntry.Variable] = rig.Install.ToUpperInvariant() + Path.DirectorySeparatorChar;
            cli.Environment[TestEntry.StartVariable] = ReportFile;

            Assert.Equal(new[] { "--smoke", ReportFile, "--fakes" }, TestEntry.ProgramArguments(cli.Self, cli.Lookup));
        }
    }

    [Fact]
    public void The_start_variable_missing_leaves_the_plain_start_even_with_the_switch_on()
    {
        using var rig = Rig.Create("cli-start3", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();

        Assert.Empty(TestEntry.ProgramArguments(cli.Self, cli.Lookup));
        Assert.Equal("PCMIERU_SWAP_TEST_START", TestEntry.StartVariable);
        Assert.StartsWith(TestEntry.Variable, TestEntry.StartVariable, StringComparison.Ordinal); // the name the publish scan already looks for
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("smoke.json")]
    [InlineData("sub\\smoke.json")]
    [InlineData("..\\smoke.json")]
    [InlineData("\\smoke.json")]
    [InlineData("C:smoke.json")]
    [InlineData("C:")]
    [InlineData("C:\\")]
    [InlineData("C:\\scratch\\")]
    [InlineData("C:\\scratch/")]
    [InlineData("\\\\server\\share\\smoke.json")]
    [InlineData("\\\\?\\C:\\scratch\\smoke.json")]
    [InlineData("\\\\.\\pipe\\smoke")]
    [InlineData("http://example.invalid/smoke.json")]
    public void A_start_file_that_is_empty_relative_a_folder_or_not_a_path_on_a_local_drive_is_ignored_and_the_start_stays_plain(string value)
    {
        using var rig = Rig.Create("cli-start4", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();
        cli.Environment[TestEntry.StartVariable] = value;

        Assert.Empty(TestEntry.ProgramArguments(cli.Self, cli.Lookup));
    }

    [Fact]
    public void The_product_starts_the_installed_program_plainly_whatever_the_environment_says_and_the_test_build_starts_the_smoke_run_with_the_fakes()
    {
        using var rig = Rig.Create("cli-start5", recorded: true);
        var report = Path.Combine(rig.Root, "smoke-report.json");

        var product = Runner(rig);
        product.SwitchOn();
        product.Environment[TestEntry.StartVariable] = report; // the product's host never reads the environment
        Assert.Equal(SwapExit.NothingToDo, product.Run("--recover", "--then-start"));
        var plain = Assert.Single(product.Starter.Starts);
        Assert.Equal(Path.GetFullPath(rig.ExePath), plain.Exe);
        Assert.Empty(plain.Arguments);

        var testBuild = Runner(rig);
        testBuild.SwitchOn();
        testBuild.Environment[TestEntry.StartVariable] = report;
        testBuild.ProgramArguments = TestEntry.ProgramArguments(testBuild.Self, testBuild.Lookup); // what the test build's Program.cs does
        Assert.Equal(SwapExit.NothingToDo, testBuild.Run("--recover", "--then-start"));
        var smoke = Assert.Single(testBuild.Starter.Starts);
        Assert.Equal(Path.GetFullPath(rig.ExePath), smoke.Exe);
        Assert.Equal(new[] { "--smoke", report, "--fakes" }, smoke.Arguments);
        Assert.False(File.Exists(report), "nothing was started: the fake starter was only asked");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the Launcher (I\PCMieru.Swap.exe)

    [Fact]
    public void The_launcher_places_recover_exe_hands_over_to_it_and_ends_without_waiting()
    {
        using var rig = Rig.Create("cli-launch", recorded: true);
        var before = Rig.Snapshot(rig.Install);
        var cli = Launcher(rig);

        var code = cli.Run("--recover");

        Assert.Equal(SwapExit.HandedOver, code);
        var start = Assert.Single(cli.Starter.Starts);
        Assert.Equal(Path.GetFullPath(rig.Paths.Recover), start.Exe);
        Assert.Equal(Path.GetFullPath(rig.Install), start.Folder);
        Assert.Equal(new[] { "--after-pid", OwnPid.ToString(CultureInfo.InvariantCulture), "--recover" }, start.Arguments);
        Assert.Equal(SyntheticSet.Bytes("swap-old", 2000), File.ReadAllBytes(rig.Paths.Recover));
        Assert.Contains("result HandedOver exit=1", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(cli.Waits); // the launcher never waits: its own image is what the swap replaces
        Assert.Empty(cli.Sleeps);
        rig.AssertUnchanged(before, "the launcher changes only the update folder");
        Assert.False(File.Exists(rig.Paths.Journal));
    }

    [Fact]
    public void The_launcher_forwards_then_start_and_gives_recover_exe_its_own_id_not_the_one_it_was_given()
    {
        using var rig = Rig.Create("cli-launch2", recorded: true);
        var cli = Launcher(rig);

        Assert.Equal(SwapExit.HandedOver, cli.Run("--after-pid", "777", "--then-start", "--recover"));

        Assert.Equal(new[] { "--after-pid", OwnPid.ToString(CultureInfo.InvariantCulture), "--then-start", "--recover" }, Assert.Single(cli.Starter.Starts).Arguments);
    }

    [Fact]
    public void The_launcher_with_no_argument_hands_over_the_recovery_too()
    {
        using var rig = Rig.Create("cli-launch3", recorded: true);
        var cli = Launcher(rig);

        Assert.Equal(SwapExit.HandedOver, cli.Run());

        Assert.Equal(new[] { "--after-pid", OwnPid.ToString(CultureInfo.InvariantCulture) }, Assert.Single(cli.Starter.Starts).Arguments);
    }

    [Fact]
    public void The_launcher_never_swaps_even_with_the_test_entry_switched_on()
    {
        using var rig = Rig.Create("cli-launch4", recorded: true);
        var cli = Launcher(rig);
        cli.SwitchOn();
        var arguments = cli.TestSwap("ok");

        Assert.Equal(SwapExit.HandedOver, cli.RunTestBuild(arguments));

        rig.AssertTree(Generation.Old, "the installed Swap.exe must not swap");
        Assert.False(File.Exists(rig.Paths.Journal));
        Assert.Equal(new[] { "--after-pid", OwnPid.ToString(CultureInfo.InvariantCulture) }.Concat(arguments).ToArray(), Assert.Single(cli.Starter.Starts).Arguments);
        Assert.Equal(Path.GetFullPath(rig.Paths.Recover), cli.Starter.Starts[0].Exe);
    }

    [Fact]
    public void The_launcher_does_not_start_a_recover_exe_it_could_not_make_from_the_recorded_swap_exe()
    {
        using var rig = Rig.Create("cli-launch5", recorded: true);
        var damaged = SyntheticSet.Bytes("swap-old", 2000);
        damaged[7] ^= 0x33;
        File.WriteAllBytes(Path.Combine(rig.Install, "PCMieru.Swap.exe"), damaged);
        var cli = Launcher(rig);

        Assert.Equal(SwapExit.RunnerUnavailable, cli.Run("--recover"));

        Assert.Empty(cli.Starter.Starts);
        Assert.False(File.Exists(rig.Paths.Recover));
        Assert.Contains("not ready", cli.Err.ToString(), StringComparison.Ordinal);
        Assert.Contains("新しいフォルダー", cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_launcher_says_so_when_recover_exe_cannot_be_started()
    {
        using var rig = Rig.Create("cli-launch6", recorded: true);
        var cli = Launcher(rig);
        cli.Starter.Succeeds = false;

        Assert.Equal(SwapExit.RunnerUnavailable, cli.Run("--recover"));

        Assert.Contains("could not be started", cli.Err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("HandedOver", cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_launcher_of_a_folder_with_no_record_does_nothing()
    {
        using var rig = Rig.Create("cli-launch7");
        File.Delete(Path.Combine(rig.Install, "PCMieru.bundle.json"));
        var cli = Launcher(rig);

        Assert.Equal(SwapExit.RunnerUnavailable, cli.Run("--recover"));

        Assert.Empty(cli.Starter.Starts);
        Assert.False(File.Exists(rig.Paths.Recover));
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the Runner: recovery

    [Fact]
    public void The_runner_recovers_nothing_when_nothing_is_open_and_says_so_in_two_languages()
    {
        using var rig = Rig.Create("cli-rec0", recorded: true);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.NothingToDo, cli.Run("--recover"));

        var lines = cli.Out.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.StartsWith("result NothingToDo ", lines[0], StringComparison.Ordinal);
        Assert.Contains("exit=0", lines[0], StringComparison.Ordinal);
        Assert.Contains("PCミエル", lines[1], StringComparison.Ordinal);
        Assert.Contains("PC Mieru", lines[2], StringComparison.Ordinal);
        Assert.Empty(cli.Starter.Starts);
        rig.AssertTree(Generation.Old, "nothing to recover");
    }

    [Fact]
    public void No_argument_is_the_recovery_which_is_what_a_double_click_does()
    {
        using var rig = Rig.Create("cli-rec1", recorded: true);
        CutSwap(rig, TxSteps.SwapOverwrite, TxEdge.After);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.RolledBack, cli.Run());

        rig.AssertTree(Generation.Old, "recovered by a bare double-click");
        using var start = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.Open, start.Outcome);
    }

    [Fact]
    public void An_interrupted_swap_is_rolled_back_by_the_runner_with_exit_11()
    {
        using var rig = Rig.Create("cli-rec2", recorded: true);
        CutSwap(rig, TxSteps.SwapAdd, TxEdge.After);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.RolledBack, cli.Run("--recover"));

        Assert.Contains("result RolledBack ", cli.Out.ToString(), StringComparison.Ordinal);
        rig.AssertTree(Generation.Old, "rolled back");
        Assert.False(File.Exists(rig.Paths.Journal));
        Assert.False(File.Exists(rig.Paths.Note), "RECOVER.txt is removed with the journal");
    }

    [Fact]
    public void A_swap_that_had_decided_to_commit_is_finished_by_the_runner_with_exit_10()
    {
        using var rig = Rig.Create("cli-rec3", recorded: true);
        CutSwap(rig, TxSteps.JournalCommitIntent, TxEdge.After);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.Committed, cli.Run("--recover"));

        rig.AssertTree(Generation.New, "committed by the recovery");
    }

    [Fact]
    public void A_journal_that_cannot_be_read_is_exit_20_and_is_kept()
    {
        using var rig = Rig.Create("cli-rec4", recorded: true);
        CutSwap(rig, TxSteps.SwapOverwrite, TxEdge.After);
        var bytes = File.ReadAllBytes(rig.Paths.Journal);
        var broken = bytes[..(bytes.Length / 2)];
        File.WriteAllBytes(rig.Paths.Journal, broken);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.RecoveryFailed, cli.Run("--recover", "--then-start"));

        Assert.Equal(broken, File.ReadAllBytes(rig.Paths.Journal));
        Assert.Contains("新しいフォルダー", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("new folder", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Empty(cli.Starter.Starts); // an install that failed to recover is never started
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the Runner: the test-only swap

    [Fact]
    public void The_runner_swaps_a_staged_folder_with_a_scripted_ok_and_ends_with_exit_10()
    {
        using var rig = Rig.Create("cli-swap-ok", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();

        Assert.Equal(SwapExit.Committed, cli.RunTestBuild(cli.TestSwap("ok")));

        rig.AssertTree(Generation.New, "committed");
        Assert.Contains("result Committed ", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("0.5.1", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal(SyntheticSet.Bytes("swap-old", 2000), File.ReadAllBytes(rig.Paths.Recover)); // the copy of the generation that was installed when it was made
        Assert.Empty(cli.Starter.Starts);
        using var start = InstallGate.EnterStartup(rig.ExePath);
        Assert.Equal(GateOutcome.Open, start.Outcome);
    }

    [Fact]
    public void A_scripted_fail_is_three_failed_launches_then_a_rollback_with_exit_11_and_the_users_files_intact()
    {
        using var rig = Rig.Create("cli-swap-fail", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();

        Assert.Equal(SwapExit.RolledBack, cli.RunTestBuild(cli.TestSwap("fail")));

        rig.AssertTree(Generation.Old, "rolled back after three failed launches");
        Assert.Contains("result RolledBack LaunchFailed ", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal(("rolledBack", "launchFailed", "swap"), TxSwapTests.ReadLastResult(rig));
    }

    [Fact]
    public void A_staged_folder_that_is_not_there_is_aborted_with_exit_12_and_nothing_changes()
    {
        using var rig = Rig.Create("cli-swap-abort", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();
        var before = Rig.Snapshot(rig.Install);

        Assert.Equal(SwapExit.Aborted, cli.RunTestBuild("--test-swap", Path.Combine(rig.Root, "no-such-staged"), "-", "Before", "1", Path.Combine(rig.Root, "m.txt"), "ok"));

        rig.AssertUnchanged(before, "an abort");
    }

    [Fact]
    public void The_step_names_of_the_test_entry_are_the_steps_the_engine_has()
    {
        // The kill tests of the trial name a step on the command line; a name that stopped parsing would only show up there. Every step parses, with each edge.
        using var rig = Rig.Create("cli-steps", recorded: true);
        var cli = Runner(rig);
        cli.SwitchOn();
        var marker = Path.Combine(rig.Root, "never.txt");
        foreach (var step in TxSteps.All)
        {
            foreach (var edge in new[] { "Before", "Mid", "After" })
            {
                // The verdict is checked LAST: a step and an edge that parse get as far as the verdict, which is refused, so nothing runs; an unknown step or edge would be named instead.
                cli.Err.GetStringBuilder().Clear();
                Assert.Equal(SwapExit.BadCommandLine, cli.RunTestBuild("--test-swap", rig.Staged, step.Name, edge, "1", marker, "neither"));
                Assert.Contains("the verdict is ok or fail", cli.Err.ToString(), StringComparison.Ordinal);
            }
        }

        Assert.False(File.Exists(marker));
        rig.AssertTree(Generation.Old, "only parsed");
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the Runner: the waits

    [Fact]
    public void The_runner_waits_for_the_process_that_handed_over_before_it_looks_at_the_gate()
    {
        using var rig = Rig.Create("cli-after", recorded: true);
        var program = InstallGate.EnterStartup(rig.ExePath); // the closing program still holds the gate
        Assert.True(program.IsOpen);
        var cli = Runner(rig);
        cli.OnWait = _ => program.Dispose(); // it ends: its handle goes with it

        Assert.Equal(SwapExit.NothingToDo, cli.Run("--recover", "--after-pid", "4242"));

        Assert.Equal((4242, TimeSpan.FromSeconds(30)), Assert.Single(cli.Waits));
        Assert.Empty(cli.Sleeps); // the gate was free when it was asked: nothing to poll for
    }

    [Fact]
    public void A_process_that_does_not_end_in_time_does_not_stop_the_recovery_the_gate_decides()
    {
        using var rig = Rig.Create("cli-after2", recorded: true);
        var cli = Runner(rig);
        cli.PidEnded = _ => false;

        Assert.Equal(SwapExit.NothingToDo, cli.Run("--after-pid", "4243", "--recover"));

        Assert.Single(cli.Waits);
    }

    [Fact]
    public void A_gate_that_stays_held_is_waited_for_by_the_limit_then_it_is_exit_30_and_nothing_changes()
    {
        using var rig = Rig.Create("cli-busy", recorded: true);
        using var program = InstallGate.EnterStartup(rig.ExePath);
        Assert.True(program.IsOpen);
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.Busy, cli.Run("--recover"));

        Assert.Equal(4, cli.Sleeps.Count); // 250 ms polls up to the 1 s limit
        Assert.All(cli.Sleeps, s => Assert.Equal(TimeSpan.FromMilliseconds(250), s));
        Assert.Contains("result Gate Busy exit=30", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("PC Mieru", cli.Out.ToString(), StringComparison.Ordinal);
        rig.AssertUnchanged(before, "busy", includeUpdate: true);
    }

    [Fact]
    public void A_swap_waits_for_its_own_limit_and_a_recovery_for_its_own()
    {
        using var rig = Rig.Create("cli-limits", recorded: true);
        using var program = InstallGate.EnterStartup(rig.ExePath);
        var cli = Runner(rig);
        cli.SwitchOn();
        cli.SwapGateWait = TimeSpan.FromSeconds(2);
        cli.RecoverGateWait = TimeSpan.FromSeconds(1);

        Assert.Equal(SwapExit.Busy, cli.RunTestBuild(cli.TestSwap("ok")));
        Assert.Equal(8, cli.Sleeps.Count);
        cli.Sleeps.Clear();
        Assert.Equal(SwapExit.Busy, cli.Run("--recover"));
        Assert.Equal(4, cli.Sleeps.Count);
        rig.AssertTree(Generation.Old, "never entered");
    }

    [Fact]
    public void A_gate_that_is_let_go_during_the_wait_lets_the_work_go_on()
    {
        using var rig = Rig.Create("cli-busy2", recorded: true);
        var program = InstallGate.EnterStartup(rig.ExePath);
        var cli = Runner(rig);
        cli.OnSleep = count =>
        {
            if (count == 2)
            {
                program.Dispose(); // PC Mieru closed
            }
        };

        Assert.Equal(SwapExit.NothingToDo, cli.Run("--recover"));

        Assert.Equal(2, cli.Sleeps.Count);
    }

    [Fact]
    public void A_gate_that_cannot_be_entered_is_exit_31_with_the_reason()
    {
        using var rig = Rig.Create("cli-refused", recorded: true);
        File.Delete(Path.Combine(rig.Install, GateFile.FileName));
        var before = Rig.Snapshot(rig.Install, includeUpdate: true);
        var cli = Runner(rig);

        Assert.Equal(SwapExit.GateRefused, cli.Run("--recover"));

        Assert.Contains("result Gate ", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("exit=31", cli.Out.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("swap: ", cli.Err.ToString(), StringComparison.Ordinal);
        rig.AssertUnchanged(before, "a missing gate file", includeUpdate: true);
        Assert.Empty(cli.Sleeps);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- --then-start

    [Fact]
    public void Then_start_starts_the_program_after_the_gate_is_let_go_of_so_it_can_open_it()
    {
        using var rig = Rig.Create("cli-then", recorded: true);
        CutSwap(rig, TxSteps.SwapOverwrite, TxEdge.After);
        var cli = Runner(rig);
        GateOutcome? seenByTheStartedProgram = null;
        cli.Starter.OnStart = _ =>
        {
            using var entry = InstallGate.EnterStartup(rig.ExePath); // what the started program does first
            seenByTheStartedProgram = entry.Outcome;
        };

        Assert.Equal(SwapExit.RolledBack, cli.Run(SwapCli.RecoverAndStartArguments(1234).ToArray()));

        var start = Assert.Single(cli.Starter.Starts);
        Assert.Equal(Path.GetFullPath(rig.ExePath), start.Exe);
        Assert.Equal(Path.GetFullPath(rig.Install), start.Folder);
        Assert.Empty(start.Arguments);
        Assert.Equal(GateOutcome.Open, seenByTheStartedProgram);
        Assert.Equal((1234, TimeSpan.FromSeconds(30)), Assert.Single(cli.Waits));
        rig.AssertTree(Generation.Old, "recovered, then started");
    }

    [Fact]
    public void The_arguments_the_window_gives_are_exactly_the_ones_the_program_reads()
    {
        Assert.Equal(new[] { "--recover", "--after-pid", "1234", "--then-start" }, SwapCli.RecoverAndStartArguments(1234).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Then_start_starts_the_program_after_nothing_to_do_and_after_a_commit(bool commit)
    {
        using var rig = Rig.Create("cli-then2", recorded: true);
        if (commit)
        {
            CutSwap(rig, TxSteps.JournalCommitIntent, TxEdge.After);
        }

        var cli = Runner(rig);
        cli.Alone = true;

        Assert.Equal(commit ? SwapExit.Committed : SwapExit.NothingToDo, cli.Run("--recover", "--then-start"));

        Assert.Equal(Path.GetFullPath(rig.ExePath), Assert.Single(cli.Starter.Starts).Exe);
        Assert.Empty(cli.Sleeps); // the program is up: no reading pause
    }

    [Fact]
    public void Then_start_does_not_start_after_a_failed_recovery_a_busy_gate_or_a_refused_gate()
    {
        using var failed = Rig.Create("cli-then3", recorded: true);
        CutSwap(failed, TxSteps.SwapOverwrite, TxEdge.After);
        File.WriteAllBytes(failed.Paths.Journal, [1, 2, 3]);
        var cliFailed = Runner(failed);
        Assert.Equal(SwapExit.RecoveryFailed, cliFailed.Run("--recover", "--then-start"));
        Assert.Empty(cliFailed.Starter.Starts);

        using var busy = Rig.Create("cli-then4", recorded: true);
        using var program = InstallGate.EnterStartup(busy.ExePath);
        var cliBusy = Runner(busy);
        Assert.Equal(SwapExit.Busy, cliBusy.Run("--recover", "--then-start"));
        Assert.Empty(cliBusy.Starter.Starts);

        using var refused = Rig.Create("cli-then5", recorded: true);
        File.Delete(Path.Combine(refused.Install, GateFile.FileName));
        var cliRefused = Runner(refused);
        Assert.Equal(SwapExit.GateRefused, cliRefused.Run("--recover", "--then-start"));
        Assert.Empty(cliRefused.Starter.Starts);
    }

    [Fact]
    public void A_program_that_cannot_be_started_is_said_plainly_and_the_exit_code_stays_the_recoverys()
    {
        using var rig = Rig.Create("cli-then6", recorded: true);
        var cli = Runner(rig);
        cli.Starter.Succeeds = false;

        Assert.Equal(SwapExit.NothingToDo, cli.Run("--recover", "--then-start"));

        Assert.Contains("PCMieru.exe could not be started", cli.Err.ToString(), StringComparison.Ordinal);
        Assert.Contains("自分で起動", cli.Out.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------- the reading pause

    [Fact]
    public void A_recovery_in_a_console_of_its_own_leaves_the_result_on_screen_and_a_swap_or_a_shared_console_does_not()
    {
        using var rig = Rig.Create("cli-pause", recorded: true);
        var alone = Runner(rig);
        alone.Alone = true;
        Assert.Equal(SwapExit.NothingToDo, alone.Run("--recover"));
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, alone.Sleeps);

        var shared = Runner(rig);
        Assert.Equal(SwapExit.NothingToDo, shared.Run("--recover"));
        Assert.Empty(shared.Sleeps);

        var swap = Runner(rig);
        swap.Alone = true;
        swap.SwitchOn();
        Assert.Equal(SwapExit.Committed, swap.RunTestBuild(swap.TestSwap("ok")));
        Assert.Empty(swap.Sleeps);
    }

    [Fact]
    public void The_test_variable_has_the_one_spelling_the_trial_script_and_the_documents_use()
    {
        Assert.Equal("PCMIERU_SWAP_TEST", TestEntry.Variable);
        Assert.Equal("--test-swap", TestEntry.Option);
    }
}
