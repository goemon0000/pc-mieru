using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PcMieru.Wire;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// What every exe does at its gate (DESIGN-B 4), with REAL child processes: the exe's build folder is copied next to a gate file written by the test, something holds the gate (or makes it
/// refuse in another way) and the child is started. The expectations are the contract of each exe: pcmi writes to stderr only and exits Failure=1 / Denied=4 (and never writes to the MCP stdout),
/// the SensorHost says one fixed line on stderr and exits 3 without a frame, the Helper exits Error=1 / Denied=4 without running the request or writing the result file, the network meter exits
/// Internal=5 silently, PCMieru.exe (unattended) says the reason on stderr and exits 1, and the party stub does the minimum with the same call.
/// </summary>
public sealed class GateExeTests : IClassFixture<Boxes>
{
    // MeterExit.Internal of PCMieru.NetMeter (an internal type of that exe, not visible here): the value is part of the contract with the app, which reads the exit code.
    private const int NetMeterInternalExit = 5;

    private readonly Boxes _boxes;

    public GateExeTests(Boxes boxes) => _boxes = boxes;

    public static TheoryData<Exe> Everyone()
    {
        var data = new TheoryData<Exe>();
        foreach (var exe in Enum.GetValues<Exe>())
        {
            data.Add(exe);
        }

        return data;
    }

    public static TheoryData<Exe, GateOutcome> Refusals()
    {
        var data = new TheoryData<Exe, GateOutcome>();
        foreach (var exe in Enum.GetValues<Exe>())
        {
            foreach (var outcome in new[] { GateOutcome.Busy, GateOutcome.RecoveryPending, GateOutcome.RecoveryFailed, GateOutcome.MissingGate, GateOutcome.Denied, GateOutcome.ReparseRefused })
            {
                data.Add(exe, outcome);
            }
        }

        return data;
    }

    // ---- the gate is free: every exe starts as it always did ----

    [Theory]
    [MemberData(nameof(Everyone))]
    public void With_the_gate_free_every_exe_starts_normally(Exe exe)
    {
        var box = _boxes.Get(exe);

        var run = ExeContract.Start(box, _boxes.AppData, _boxes.Scratch);

        ExeContract.AssertStartedNormally(exe, run, _boxes.Scratch);
    }

    // ---- the gate does not open: each exe does what its place in DESIGN-B 4 says ----

    [Theory]
    [MemberData(nameof(Refusals))]
    public void With_the_gate_not_open_every_exe_refuses_the_way_its_contract_says(Exe exe, GateOutcome outcome)
    {
        var box = _boxes.Get(exe);

        using var situation = Situation.Apply(_boxes.Scratch, box.ExePath, outcome);
        var run = ExeContract.Start(box, _boxes.AppData, _boxes.Scratch);

        ExeContract.AssertRefused(exe, outcome, run, _boxes.Scratch);
    }

    // ---- the Helper: the request is not run and no result file is written ----

    [Theory]
    [InlineData(GateOutcome.Busy, 1)]
    [InlineData(GateOutcome.MissingGate, 1)]
    [InlineData(GateOutcome.RecoveryPending, 1)]
    [InlineData(GateOutcome.Denied, 4)]
    public void The_helper_that_is_refused_runs_no_request_and_leaves_the_result_file_empty(GateOutcome outcome, int exitCode)
    {
        var box = _boxes.Get(Exe.Helper);
        var result = HelperResultFile(_boxes.Scratch, out var nonce);
        var args = new HelperRequest(HelperOperation.ServiceStart, nonce, result) { ServiceName = "PcMieruGateTestNoSuchService" }.ToArgs();

        using var situation = Situation.Apply(_boxes.Scratch, box.ExePath, outcome);
        var ran = Child.Run(box.ExePath, args, _boxes.AppData);

        Assert.Equal(exitCode, ran.ExitCode);
        Assert.Equal(0, new FileInfo(result).Length); // nothing was written: not "denied", not "not found", nothing
        Assert.Equal(string.Empty, ran.Out);
        Assert.StartsWith("PCMieru.Helper: " + InstallGate.ReasonOf(outcome), ran.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_helper_with_the_gate_free_runs_the_request_and_writes_the_result_file()
    {
        var box = _boxes.Get(Exe.Helper);
        var result = HelperResultFile(_boxes.Scratch, out var nonce);
        // A service that does not exist, and no elevation: nothing real can happen. What matters is that the request was run at all and that it said so in the result file.
        var args = new HelperRequest(HelperOperation.ServiceStart, nonce, result) { ServiceName = "PcMieruGateTestNoSuchService" }.ToArgs();

        var ran = Child.Run(box.ExePath, args, _boxes.AppData);

        Assert.NotEqual((int)OperationCode.InvalidArguments, ran.ExitCode);
        Assert.True(new FileInfo(result).Length > 0, "the request ran, so the result file has its answer; exit " + ran.ExitCode + ", stderr: " + ran.Err);
    }

    private static string HelperResultFile(Scratch scratch, out string nonce)
    {
        nonce = Guid.NewGuid().ToString("N");
        var result = Path.Combine(scratch.Sub("helper-result"), nonce + ".json");
        File.WriteAllBytes(result, []);
        return result;
    }

    // ---- the network meter: no pipe, no ETW session, no answer ----

    [Fact]
    public void The_network_meter_that_is_refused_with_valid_arguments_ends_with_internal_before_it_makes_a_pipe_or_a_session()
    {
        var box = _boxes.Get(Exe.NetMeter);
        var token = Guid.NewGuid().ToString("N");
        var pipe = NetMeterArguments.PipePrefix + token;
        var self = Process.GetCurrentProcess();
        var args = new[]
        {
            "--parent", self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--parent-created", self.StartTime.ToFileTime().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pipe", pipe,
            "--user-sid", WindowsIdentity.GetCurrent().User!.Value,
        };
        Assert.True(NetMeterArguments.TryParse(args, out _, out var error), "the arguments of the test are valid: " + error);

        using var situation = Situation.Apply(_boxes.Scratch, box.ExePath, GateOutcome.Busy);
        var ran = Child.Run(box.ExePath, args, _boxes.AppData);

        // Exit 5 (Internal) is the proof that the meter stopped at the gate: with valid arguments it would have made its pipe and its ETW session and gone on to serve (and then ended with another
        // code, or not ended at all). There is no "no pipe" line here on purpose: a named pipe belongs to the process that made it and is gone when that process ends, so looking for it after the exit
        // (as an earlier version did) could never fail, and looking for it while the child lives is a race with a child that ends within milliseconds.
        Assert.Equal(NetMeterInternalExit, ran.ExitCode);
        Assert.Equal(string.Empty, ran.Out);
        Assert.Equal(string.Empty, ran.Err);
    }

    // ---- pcmi: stdout is the MCP channel ----

    [Fact]
    public void The_mcp_server_that_is_refused_writes_nothing_to_stdout_and_never_reads_a_request()
    {
        var box = _boxes.Get(Exe.Cli);

        using var situation = Situation.Apply(_boxes.Scratch, box.ExePath, GateOutcome.Busy);
        using var child = Child.Start(box.ExePath, ["mcp"], _boxes.AppData);
        try
        {
            child.Write("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n");
        }
        catch (IOException)
        {
            // the child may already have left: that is the point
        }

        child.CloseInput();
        var ran = child.Wait();

        Assert.Equal(1, ran.ExitCode);
        Assert.Equal(string.Empty, ran.Out);
        Assert.StartsWith("pcmi: " + InstallGate.ReasonOf(GateOutcome.Busy), ran.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mcp_server_with_the_gate_free_answers_the_handshake_on_stdout_and_holds_the_gate_while_it_runs()
    {
        var box = _boxes.Get(Exe.Cli);
        using var child = Child.Start(box.ExePath, ["mcp"], _boxes.AppData);
        child.Write("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"gate-test\",\"version\":\"1\"}}}\n");

        var first = child.ReadLine(TimeSpan.FromSeconds(60));
        Assert.NotNull(first);
        Assert.Contains("\"id\":1", first.Replace(" ", string.Empty), StringComparison.Ordinal);

        // While the server runs, the swapper cannot get the gate.
        using (var swapper = InstallGate.EnterExclusive(box.ExePath))
        {
            Assert.Equal(GateOutcome.Busy, swapper.Outcome);
        }

        child.CloseInput();
        var ran = child.Wait();
        Assert.Equal(0, ran.ExitCode);

        using var free = InstallGate.EnterExclusive(box.ExePath);
        Assert.Equal(GateOutcome.Open, free.Outcome);
    }
}
