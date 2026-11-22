using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// What each exe does at its gate, as one table: how it is started so that it ends by itself, what "started normally" looks like, and what a refusal looks like (DESIGN-B 4).
/// The refusals are the contract the other side relies on: pcmi's stdout is the MCP channel (never written), the Helper's result file stays empty, the network meter is silent.
/// </summary>
internal static class ExeContract
{
    // The exit codes of the exes. Their enums are internal to the exes, so the numbers are repeated here on purpose: they are a contract with the app and with the AI apps that run pcmi.
    private const int PcmiFailure = 1;
    private const int PcmiDenied = 4;
    private const int SensorHostRefused = 3;
    private const int HelperError = 1;
    private const int HelperDenied = 4;
    private const int HelperInvalidArguments = 2;
    private const int NetMeterInvalidArguments = 2;
    private const int NetMeterInternal = 5;
    private const int GuiRefused = 1;
    private const int PartyRefused = 1;

    /// <summary>The smoke file that <see cref="Start"/> asks PCMieru.exe for: the same name every time, so a test can see whether the run wrote it.</summary>
    public static string SmokeFileOf(Scratch scratch) => Path.Combine(scratch.Sub("smoke"), "smoke.json");

    /// <summary>Starts the exe the way a test can: PCMieru.exe as a smoke run on the fakes (no real data, no settings file), pcmi's version, one frame of the sensor host, the Helper and the meter without arguments.</summary>
    public static Ran Start(Box box, string appData, Scratch scratch)
    {
        if (box.Exe == Exe.Gui)
        {
            var smoke = SmokeFileOf(scratch);
            File.Delete(smoke);
            return Child.Run(box.ExePath, ["--smoke", smoke, "--fakes"], appData, timeoutMs: 300_000);
        }

        return Child.Run(box.ExePath, ArgumentsOf(box.Exe), appData);
    }

    private static string[] ArgumentsOf(Exe exe) => exe switch
    {
        Exe.Cli => ["version"],
        Exe.SensorHost => ["--once"],
        _ => [],
    };

    public static void AssertStartedNormally(Exe exe, Ran run, Scratch scratch)
    {
        var where = exe + " (exit " + run.ExitCode + ", stdout: " + Short(run.Out) + ", stderr: " + Short(run.Err) + ")";
        switch (exe)
        {
            case Exe.Gui:
                Assert.True(run.ExitCode == 0, "the smoke run of the gui passes with the gate free: " + where);
                Assert.True(File.Exists(SmokeFileOf(scratch)), "the smoke run wrote its verdict file: " + where);
                break;

            case Exe.Cli:
                Assert.True(run.ExitCode == 0, where);
                Assert.False(string.IsNullOrWhiteSpace(run.Out), where);
                Assert.Equal(string.Empty, run.Err);
                break;

            case Exe.SensorHost:
                Assert.True(run.ExitCode == 0, where);
                var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.True(lines.Length == 1 && lines[0].StartsWith('{'), "one frame (a JSON line): " + where);
                break;

            case Exe.Helper:
                // No arguments is an invalid request: the exe got past its gate and ran its own argument check, which is all this test is about.
                Assert.True(run.ExitCode == HelperInvalidArguments, where);
                break;

            case Exe.NetMeter:
                Assert.True(run.ExitCode == NetMeterInvalidArguments, where);
                Assert.Equal(string.Empty, run.Out);
                break;

            default:
                Assert.True(run.ExitCode == 0, where);
                Assert.Equal("party ok\n", run.Out);
                Assert.Equal(string.Empty, run.Err);
                break;
        }
    }

    public static void AssertRefused(Exe exe, GateOutcome outcome, Ran run, Scratch scratch)
    {
        var reason = InstallGate.ReasonOf(outcome);
        var where = exe + " / " + outcome + " (exit " + run.ExitCode + ", stdout: " + Short(run.Out) + ", stderr: " + Short(run.Err) + ")";
        var recovery = outcome is GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed;

        // Nobody writes to stdout when the gate is closed: for pcmi it is the MCP channel, for the sensor host it is the frame channel.
        Assert.True(run.Out.Length == 0, "nothing on stdout: " + where);

        switch (exe)
        {
            case Exe.Gui:
                Assert.True(run.ExitCode == GuiRefused, where);
                Assert.StartsWith("PCMieru: " + reason, run.Err, StringComparison.Ordinal);
                Assert.False(File.Exists(SmokeFileOf(scratch)), "an unattended run that was refused made no smoke file: " + where);
                break;

            case Exe.Cli:
                Assert.True(run.ExitCode == (outcome == GateOutcome.Denied ? PcmiDenied : PcmiFailure), where);
                Assert.StartsWith("pcmi: " + reason, run.Err, StringComparison.Ordinal);
                Assert.Equal(recovery, run.Err.Contains(GateFile.RecoverNoteName, StringComparison.Ordinal));
                break;

            case Exe.SensorHost:
                Assert.True(run.ExitCode == SensorHostRefused, where);
                Assert.StartsWith("sensorhost " + reason, run.Err, StringComparison.Ordinal);
                break;

            case Exe.Helper:
                Assert.True(run.ExitCode == (outcome == GateOutcome.Denied ? HelperDenied : HelperError), where);
                Assert.StartsWith("PCMieru.Helper: " + reason, run.Err, StringComparison.Ordinal);
                break;

            case Exe.NetMeter:
                Assert.True(run.ExitCode == NetMeterInternal, where);
                Assert.True(run.Err.Length == 0, "the meter is silent: " + where);
                break;

            default:
                Assert.True(run.ExitCode == PartyRefused, where);
                Assert.StartsWith("party " + reason, run.Err, StringComparison.Ordinal);
                break;
        }
    }

    private static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? "\"" + line + "\"" : "\"" + line[..200] + "...\"";
    }
}
