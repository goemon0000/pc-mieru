using System.Text.Json;
using PcMieru.Checks;
using PcMieru.Cli.Commands;

namespace PcMieru.Cli.Tests;

/// <summary>
/// <c>pcmi check system|storage</c> (DESIGN-A 8): the bad forms (exit 2), the exit code of every way a run can end (0, 1, 4), what goes to
/// stdout (data) and to stderr (diagnostics), progress, and Ctrl+C. The runner is always a fake and the data folder a temporary one.
/// </summary>
public class CheckCommandTests
{
    private static string[] Split(string args) => args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static CheckResult ForVerdict(string name) => name switch
    {
        "ok" => CheckSamples.SystemOk(),
        "caution" => CheckSamples.SystemCaution(),
        "check" => CheckSamples.SystemCheck(),
        "unavailable" => CheckSamples.SystemUnavailable(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string[] ErrLines(CliResult r) => r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---- the samples themselves --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_sample_result_is_valid_serializes_and_reads_back_to_the_same_text()
    {
        Assert.Equal(11, CheckSamples.AllShapes().Count);
        foreach (var result in CheckSamples.AllShapes())
        {
            Assert.Empty(CheckResultValidator.Validate(result));
            var json = CheckJson.Serialize(result);
            Assert.Equal(json, CheckJson.Serialize(CheckJson.Deserialize(json)));
        }

        Assert.Equal(CheckVerdict.Ok, CheckSamples.SystemOk().Verdict);
        Assert.Equal(CheckVerdict.Caution, CheckSamples.SystemCaution().Verdict);
        Assert.Equal(CheckVerdict.Check, CheckSamples.SystemCheck().Verdict);
        Assert.Equal(CheckVerdict.Unavailable, CheckSamples.SystemUnavailable().Verdict);
        Assert.Equal(CheckOutcome.Stopped, CheckSamples.SystemStopped().Outcome);
        Assert.Equal(CheckOutcome.Failed, CheckSamples.Failed().Outcome);
    }

    // ---- exit 2: every bad form ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("foo")]
    [InlineData("SYSTEM")]
    [InlineData("Storage")]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("system storage")]
    [InlineData("system extra")]
    [InlineData("history 5")]
    [InlineData("system --elevate")]
    [InlineData("--elevate system")]
    [InlineData("history --elevate")]
    [InlineData("history --elevate --json")]
    [InlineData("system --bad")]
    [InlineData("storage --bad")]
    [InlineData("history --limit 5")]
    [InlineData("history --kind system")]
    [InlineData("system -x")]
    [InlineData("--bad")]
    [InlineData("system --json yes")]
    [InlineData("system --json=yes")]
    [InlineData("storage --elevate=yes")]
    public async Task A_bad_command_line_is_exit_2_with_the_usage_and_nothing_run_or_made(string args)
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.SystemOk());

        var r = await CliRun.CheckAsync(["check", .. Split(args)], data, runner);

        Assert.Equal(2, r.ExitCode);
        Assert.Equal("", r.Out);
        Assert.StartsWith("pcmi check: ", r.Err, StringComparison.Ordinal);
        Assert.Contains("pcmi check system [--json] | storage [--elevate] [--json] | history [--json]", r.Err, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(data.Path), "an argument error must not make the data folder");
    }

    [Fact]
    public async Task The_messages_of_the_bad_forms_say_what_is_wrong()
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.SystemOk());

        var none = await CliRun.CheckAsync(["check"], data, runner);
        Assert.Contains("missing verb (system, storage or history)", none.Err, StringComparison.Ordinal);

        var unknown = await CliRun.CheckAsync(["check", "foo"], data, runner);
        Assert.Contains("unknown verb 'foo' (system, storage or history)", unknown.Err, StringComparison.Ordinal);

        var wrongCase = await CliRun.CheckAsync(["check", "SYSTEM"], data, runner);
        Assert.Contains("unknown verb 'SYSTEM'", wrongCase.Err, StringComparison.Ordinal);

        var elevateOnSystem = await CliRun.CheckAsync(["check", "system", "--elevate"], data, runner);
        Assert.Contains("--elevate belongs to 'check storage' only, not 'check system'", elevateOnSystem.Err, StringComparison.Ordinal);

        var elevateOnHistory = await CliRun.CheckAsync(["check", "history", "--elevate"], data, runner);
        Assert.Contains("--elevate belongs to 'check storage' only, not 'check history'", elevateOnHistory.Err, StringComparison.Ordinal);

        var two = await CliRun.CheckAsync(["check", "system", "storage"], data, runner);
        Assert.Contains("unexpected argument 'storage'", two.Err, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Help_is_exit_0_on_stdout_and_runs_nothing()
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.SystemOk());

        var r = await CliRun.CheckAsync(["check", "system", "--help"], data, runner);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("pcmi check system", r.Out, StringComparison.Ordinal);
        Assert.Equal("", r.Err);
        Assert.Empty(runner.Requests);
    }

    // ---- exit 0: completed and saved, whatever the verdict -----------------------------------------------------------------------------

    [Theory]
    [InlineData("ok", "OK")]
    [InlineData("caution", "Caution")]
    [InlineData("check", "Needs review")]
    [InlineData("unavailable", "Not measured")]
    public async Task A_completed_and_saved_run_is_exit_0_whatever_the_verdict(string name, string word)
    {
        using var data = new TempFolder();
        var result = ForVerdict(name);
        var runner = FakeCheckRunner.Returning(result);

        var r = await CliRun.CheckAsync(["check", "system"], data, runner);

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith($"System check: {word}\n\n", r.Out, StringComparison.Ordinal);
        Assert.Equal("", r.Err);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(CheckKind.System, request.Kind);
        Assert.Null(request.Duration);
        Assert.False(request.AllowElevation);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("caution")]
    [InlineData("check")]
    [InlineData("unavailable")]
    public async Task Json_is_one_line_of_the_library_serializer_and_nothing_else(string name)
    {
        using var data = new TempFolder();
        var result = ForVerdict(name);

        var r = await CliRun.CheckAsync(["check", "system", "--json"], data, FakeCheckRunner.Returning(result));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(CheckJson.Serialize(result) + "\n", r.Out);
        Assert.Equal("", r.Err);
        Assert.Single(r.OutLines);
        var root = JsonDocument.Parse(r.Out).RootElement;
        Assert.Equal("pcm-check/1", root.GetProperty("format").GetString());
        Assert.Equal("system", root.GetProperty("kind").GetString());
        Assert.Equal(name, root.GetProperty("verdict").GetString());
        Assert.Equal(CheckSamples.Id(1).ToString("D"), root.GetProperty("runId").GetString());
        Assert.Equal(CheckJson.Serialize(result), CheckJson.Serialize(CheckJson.Deserialize(r.Out.TrimEnd('\n'))));
    }

    [Fact]
    public async Task Storage_asks_for_a_storage_check_without_elevation_by_default()
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.StorageRead());

        var r = await CliRun.CheckAsync(["check", "storage"], data, runner);

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("Storage check: OK\n\n", r.Out, StringComparison.Ordinal);
        Assert.Equal("", r.Err);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(CheckKind.Storage, request.Kind);
        Assert.Null(request.Duration);
        Assert.False(request.AllowElevation);
    }

    [Fact]
    public async Task A_denied_drive_without_elevate_is_still_exit_0_and_the_hint_is_on_stderr()
    {
        using var data = new TempFolder();
        var result = CheckSamples.StorageDenied();
        var runner = FakeCheckRunner.Returning(result);

        var r = await CliRun.CheckAsync(["check", "storage", "--json"], data, runner);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(CheckJson.Serialize(result) + "\n", r.Out);
        Assert.Equal("pcmi check: " + CliWords.En("cli.hint.elevate") + "\n", r.Err);
        Assert.False(Assert.Single(runner.Requests).AllowElevation);
    }

    // ---- exit 1: failed, stopped, not saved ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("inspection_failed")]
    [InlineData("result_invalid")]
    [InlineData("internal_error")]
    public async Task A_failed_run_is_exit_1_with_the_reason_on_stderr_and_the_result_on_stdout(string code)
    {
        using var data = new TempFolder();
        var failed = CheckSamples.Failed();
        var run = new CheckRun(failed, CheckSaveState.Saved(CheckSamples.FileName(failed)), FailureCode: code, FailureDetail: "disk 0 not readable");

        var human = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(run));
        var json = await CliRun.CheckAsync(["check", "system", "--json"], data, FakeCheckRunner.Returning(run));

        var expected = "pcmi check: " + CliWords.En("cli.check.fail." + code) + " [" + code + ": disk 0 not readable]\n";
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(expected, human.Err);
        Assert.Contains("Outcome", human.Out, StringComparison.Ordinal);
        Assert.Contains("Failed", human.Out, StringComparison.Ordinal);
        Assert.Equal(1, json.ExitCode);
        Assert.Equal(expected, json.Err);
        Assert.Equal(CheckJson.Serialize(failed) + "\n", json.Out);
        Assert.Equal("failed", JsonDocument.Parse(json.Out).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_failure_code_this_version_does_not_know_is_still_exit_1_and_is_named()
    {
        using var data = new TempFolder();
        var failed = CheckSamples.Failed();
        var run = new CheckRun(failed, CheckSaveState.Saved(CheckSamples.FileName(failed)), FailureCode: "from_the_future");

        var r = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(run));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("pcmi check: " + CliWords.Fill(CliWords.En("cli.check.fail.unknown"), ("code", "from_the_future")) + "\n", r.Err);
    }

    [Fact]
    public async Task A_stopped_run_is_exit_1_and_its_saved_result_is_still_printed()
    {
        using var data = new TempFolder();
        var stopped = CheckSamples.SystemStopped();

        var human = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(stopped));
        var json = await CliRun.CheckAsync(["check", "system", "--json"], data, FakeCheckRunner.Returning(stopped));

        Assert.Equal(1, human.ExitCode);
        Assert.Contains("Stopped", human.Out, StringComparison.Ordinal);
        Assert.Equal("pcmi check: " + CliWords.En("cli.check.stopped") + "\n", human.Err);
        Assert.Equal(1, json.ExitCode);
        Assert.Equal(CheckJson.Serialize(stopped) + "\n", json.Out);
        Assert.Equal("stopped", JsonDocument.Parse(json.Out).RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("invalid_result")]
    [InlineData("already_exists")]
    [InlineData("access_denied")]
    [InlineData("disk_full")]
    [InlineData("io_error")]
    public async Task A_result_that_could_not_be_saved_is_exit_1_with_the_reason_and_the_result_still_printed(string code)
    {
        using var data = new TempFolder();
        var result = CheckSamples.SystemOk();
        var run = new CheckRun(result, CheckSaveState.Failed(code, "detail"));

        var human = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(run));
        var json = await CliRun.CheckAsync(["check", "system", "--json"], data, FakeCheckRunner.Returning(run));

        var expected = "pcmi check: " + CliWords.En("cli.check.savefail." + code) + "\n";
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(expected, human.Err);
        Assert.StartsWith("System check: OK\n", human.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved", human.Out, StringComparison.Ordinal);
        Assert.Equal(1, json.ExitCode);
        Assert.Equal(expected, json.Err);
        Assert.Equal(CheckJson.Serialize(result) + "\n", json.Out);
    }

    [Fact]
    public async Task A_result_that_was_never_saved_and_a_save_code_not_known_are_exit_1()
    {
        using var data = new TempFolder();

        var notSaved = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(new CheckRun(CheckSamples.SystemOk(), CheckSaveState.NotSaved)));
        var unknown = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(new CheckRun(CheckSamples.SystemOk(), CheckSaveState.Failed("from_the_future"))));

        Assert.Equal(1, notSaved.ExitCode);
        Assert.Equal("pcmi check: " + CliWords.En("cli.check.savefail.notSaved") + "\n", notSaved.Err);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Equal("pcmi check: " + CliWords.Fill(CliWords.En("cli.check.savefail.unknown"), ("code", "from_the_future")) + "\n", unknown.Err);
    }

    [Fact]
    public async Task A_stopped_run_that_could_not_be_saved_says_both()
    {
        using var data = new TempFolder();
        var run = new CheckRun(CheckSamples.SystemStopped(), CheckSaveState.Failed("disk_full"));

        var r = await CliRun.CheckAsync(["check", "system"], data, FakeCheckRunner.Returning(run));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(
            ["pcmi check: " + CliWords.En("cli.check.stopped"), "pcmi check: " + CliWords.En("cli.check.savefail.disk_full")],
            ErrLines(r));
    }

    [Fact]
    public async Task A_run_that_threw_a_cancellation_before_it_began_is_exit_1_with_nothing_on_stdout()
    {
        using var data = new TempFolder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = new FakeCheckRunner((_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FakeCheckRunner.Saved(CheckSamples.SystemOk()));
        });

        var r = await CliRun.CheckAsync(["check", "system"], data, runner, ct: cts.Token);

        Assert.Equal(1, r.ExitCode);
        Assert.Equal("", r.Out);
        Assert.Equal("pcmi check: " + CliWords.En("cli.result.cancelled") + "\n", r.Err);
        Assert.False(CliShutdown.IsHeld);
    }

    [Fact]
    public async Task A_failed_run_with_elevate_is_exit_1_not_4_even_when_the_fact_says_nothing_was_read()
    {
        using var data = new TempFolder();
        var failed = CheckSamples.Failed(CheckKind.Storage);
        var run = new CheckRun(failed, CheckSaveState.Saved(CheckSamples.FileName(failed)), FailureCode: "inspection_failed", Elevation: new CheckElevation(2, 0));

        var runner = FakeCheckRunner.Returning(run);

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate"], data, runner);

        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain(CliWords.En("cli.check.noElevatedRead"), r.Err, StringComparison.Ordinal);
        Assert.True(Assert.Single(runner.Requests).AllowElevation);
    }

    // ---- exit 4: --elevate refused (a fact of the inspection: CheckRun.Elevation) ----------------------------------------------------------

    // The rows are the same in every case (a denied temperature): the exit code follows the fact, not the rows.
    [Theory]
    [InlineData(0, 0, true, 0)]
    [InlineData(2, 0, true, 4)]
    [InlineData(1, 0, true, 4)]
    [InlineData(2, 1, true, 0)]
    [InlineData(2, 2, true, 0)]
    [InlineData(2, 0, false, 0)]
    public async Task The_exit_code_with_elevate_follows_the_fact_the_run_reports_not_the_rows(int needed, int reRead, bool elevate, int exit)
    {
        using var data = new TempFolder();
        var result = CheckSamples.StorageDenied();
        var runner = FakeCheckRunner.Returning(result, new CheckElevation(needed, reRead));
        var args = elevate ? new[] { "check", "storage", "--elevate", "--json" } : ["check", "storage", "--json"];

        var r = await CliRun.CheckAsync(args, data, runner);

        Assert.Equal(exit, r.ExitCode);

        // Whatever the code, the result is on stdout whole and parses.
        Assert.Equal(CheckJson.Serialize(result) + "\n", r.Out);
        Assert.Equal(CheckSamples.Id(1), CheckJson.Deserialize(r.Out.TrimEnd('\n')).RunId);
        var expectedErr = new List<string>();
        if (elevate)
        {
            expectedErr.Add("pcmi: " + CliWords.En("cli.check.elevateMaybe"));
            if (exit == 4)
            {
                expectedErr.Add("pcmi check: " + CliWords.En("cli.check.noElevatedRead"));
            }
        }
        else
        {
            expectedErr.Add("pcmi check: " + CliWords.En("cli.hint.elevate"));
        }

        Assert.Equal(expectedErr, ErrLines(r));
        Assert.Equal(elevate, Assert.Single(runner.Requests).AllowElevation);
    }

    [Fact]
    public async Task Elevate_refused_is_exit_4_with_the_human_table_and_the_reason_still_shown()
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.StorageDenied(), new CheckElevation(1, 0));

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate"], data, runner);

        Assert.Equal(4, r.ExitCode);
        Assert.Equal(["pcmi: " + CliWords.En("cli.check.elevateMaybe"), "pcmi check: " + CliWords.En("cli.check.noElevatedRead")], ErrLines(r));
        Assert.StartsWith("Storage check: ", r.Out, StringComparison.Ordinal);
        Assert.Contains("storage.temperature", r.Out, StringComparison.Ordinal);
        Assert.Contains("Denied", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Elevate_that_read_the_drive_is_exit_0_and_only_the_note_is_on_stderr()
    {
        using var data = new TempFolder();
        var runner = FakeCheckRunner.Returning(CheckSamples.StorageRead(), new CheckElevation(1, 1));

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate"], data, runner);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("pcmi: " + CliWords.En("cli.check.elevateMaybe") + "\n", r.Err);
        Assert.True(Assert.Single(runner.Requests).AllowElevation);
    }

    [Fact]
    public async Task Elevate_with_one_drive_read_and_one_not_stays_exit_0()
    {
        using var data = new TempFolder();

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate"], data, FakeCheckRunner.Returning(CheckSamples.StorageOneReadOneDenied(), new CheckElevation(2, 1)));

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain(CliWords.En("cli.check.noElevatedRead"), r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_without_the_fact_has_nothing_to_refuse_whatever_the_rows_say()
    {
        using var data = new TempFolder();

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate"], data, FakeCheckRunner.Returning(CheckSamples.StorageDenied()));

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain(CliWords.En("cli.check.noElevatedRead"), r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_run_is_exit_1_even_when_the_fact_says_nothing_was_read()
    {
        using var data = new TempFolder();
        var stopped = CheckSamples.StorageStopped();
        Assert.Empty(CheckResultValidator.Validate(stopped));

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate", "--json"], data, FakeCheckRunner.Returning(stopped, new CheckElevation(2, 0)));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(CheckJson.Serialize(stopped) + "\n", r.Out);
        Assert.DoesNotContain(CliWords.En("cli.check.noElevatedRead"), r.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_that_was_not_saved_is_exit_1_not_4_with_elevate_refused()
    {
        using var data = new TempFolder();
        var result = CheckSamples.StorageDenied();
        var run = new CheckRun(result, CheckSaveState.Failed("io_error", "disk full"), Elevation: new CheckElevation(2, 0));

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate"], data, FakeCheckRunner.Returning(run));

        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain(CliWords.En("cli.check.noElevatedRead"), r.Err, StringComparison.Ordinal);
    }

    // ---- stdout is data, stderr is diagnostics -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_but_the_result_is_on_stdout_and_nothing_but_diagnostics_on_stderr()
    {
        using var data = new TempFolder();
        var denied = CheckSamples.StorageDenied();

        var r = await CliRun.CheckAsync(["check", "storage", "--elevate", "--json"], data, FakeCheckRunner.Returning(denied), errorIsTerminal: true);

        Assert.Equal(CheckJson.Serialize(denied) + "\n", r.Out);
        Assert.DoesNotContain('{', r.Err);
        Assert.All(ErrLines(r), l => Assert.StartsWith("pcmi", l, StringComparison.Ordinal));
        Assert.DoesNotContain("pcmi", r.Out, StringComparison.Ordinal);
    }

    // ---- progress ----------------------------------------------------------------------------------------------------------------------

    private static FakeCheckRunner Reporting(Action<bool> sawProgress)
    {
        return new FakeCheckRunner((_, progress, _) =>
        {
            sawProgress(progress is not null);
            var id = CheckSamples.Id(1);
            foreach (var stage in new[] { CheckStage.Starting, CheckStage.Collecting, CheckStage.Collecting, CheckStage.Judging, CheckStage.Saving, CheckStage.Done })
            {
                progress?.Report(new CheckProgress(id, stage, 0.5, 0));
            }

            return Task.FromResult(FakeCheckRunner.Saved(CheckSamples.SystemOk()));
        });
    }

    [Fact]
    public async Task On_a_terminal_each_stage_is_said_once_on_stderr_and_done_is_not()
    {
        using var data = new TempFolder();
        var seen = false;

        var r = await CliRun.CheckAsync(["check", "system"], data, Reporting(s => seen = s), errorIsTerminal: true);

        Assert.True(seen);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(
            [
                "pcmi: " + CliWords.En("cli.check.stage.starting"),
                "pcmi: " + CliWords.En("cli.check.stage.collecting"),
                "pcmi: " + CliWords.En("cli.check.stage.judging"),
                "pcmi: " + CliWords.En("cli.check.stage.saving"),
            ],
            ErrLines(r));
        Assert.DoesNotContain(CliWords.En("cli.check.stage.done"), r.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("pcmi:", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_stderr_is_not_a_terminal_no_progress_is_asked_for_and_stderr_is_empty()
    {
        using var data = new TempFolder();
        bool? seen = null;

        var r = await CliRun.CheckAsync(["check", "system"], data, Reporting(s => seen = s), errorIsTerminal: false);

        Assert.False(seen);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public async Task Progress_in_japanese_uses_the_japanese_words()
    {
        using var data = new TempFolder();

        var r = await CliRun.CheckAsync(["check", "system"], data, Reporting(_ => { }), language: Language.Ja, errorIsTerminal: true);

        Assert.Contains("pcmi: " + CliWords.Ja("cli.check.stage.collecting"), ErrLines(r));
    }

    // ---- Ctrl+C ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ctrl_C_in_the_middle_of_a_run_keeps_the_shutdown_held_prints_the_stopped_saved_result_and_is_exit_1()
    {
        using var data = new TempFolder();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldInside = false;
        var runner = new FakeCheckRunner(async (_, _, ct) =>
        {
            heldInside = CliShutdown.IsHeld;
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // A stopped run is not thrown: the runner finishes its chunk, saves the stopped result and returns it.
            }

            return FakeCheckRunner.Saved(CheckSamples.SystemStopped());
        });

        var running = CliRun.CheckAsync(["check", "system", "--json"], data, runner, ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        var r = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(heldInside, "the shutdown must be held while the run is going, so that Ctrl+C does not cut the save short");
        Assert.False(CliShutdown.IsHeld, "and let go when the command is over");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(CheckJson.Serialize(CheckSamples.SystemStopped()) + "\n", r.Out);
        Assert.Equal("pcmi check: " + CliWords.En("cli.check.stopped") + "\n", r.Err);
    }

    [Fact]
    public async Task The_shutdown_is_let_go_when_the_runner_throws()
    {
        using var data = new TempFolder();
        var runner = new FakeCheckRunner((_, _, _) => throw new InvalidOperationException("boom"));

        var r = await CliRun.CheckAsync(["check", "system"], data, runner);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("boom", r.Err, StringComparison.Ordinal);
        Assert.False(CliShutdown.IsHeld);
    }

    // ---- the real machine (not run by the step's own test runs) ----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Real_machine_check_system_json_writes_a_valid_result_into_a_temporary_checks_root()
    {
        using var data = new TempFolder();

        var r = await CliRun.RunAsync(["check", "system", "--json"], checks: new CliChecks(data.Path, LegacyDirectory: null));

        Assert.Equal(0, r.ExitCode);
        var result = CheckJson.Deserialize(r.Out.TrimEnd('\n'));
        Assert.Equal(CheckKind.System, result.Kind);
        Assert.Equal(CheckOutcome.Completed, result.Outcome);
        Assert.Single(Directory.GetFiles(data.Checks, "system-*.json"));
    }
}
