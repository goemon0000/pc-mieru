using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

// TEST ONLY (see the csproj). Usage:
//   PCMieru.TxChild swap    <install> <staged> <step|-> <edge> <nth> <marker> <ok|fail>
//   PCMieru.TxChild recover <install>          <step|-> <edge> <nth> <marker> <ok|none>
// It enters the install folder's gate exclusively (like the runner), runs the transaction or the recovery, and when the n-th time it reaches <edge> of <step> it writes <marker> and sleeps forever: the
// test kills the process there. With step "-" it runs to the end. On a normal end it prints "result <outcome> <reason>" and exits 0; exit 2 is a bad command line, 3 an unexpected exception.
const int BadUsage = 2;
const int Crashed = 3;

// The one call every exe of the solution makes (tests/PcMieru.Gate.Tests checks it): the start gate of THIS program's own folder, the dev copy of the gate file the build puts beside the exe.
// The transaction below enters a different gate: the one of the install folder the test made (its first argument).
using var startGate = InstallGate.EnterStartup();
if (!startGate.IsOpen)
{
    Console.Error.WriteLine("start gate " + startGate.Reason);
    return Crashed;
}

try
{
    var swap = args.Length > 0 && args[0] == "swap";
    var recover = args.Length > 0 && args[0] == "recover";
    if (!(swap && args.Length == 8) && !(recover && args.Length == 7))
    {
        Console.Error.WriteLine("bad command line");
        return BadUsage;
    }

    var at = 1;
    var install = args[at++];
    var staged = swap ? args[at++] : null;
    var stepName = args[at++];
    var edge = Enum.Parse<TxEdge>(args[at++]);
    var nth = int.Parse(args[at++], System.Globalization.CultureInfo.InvariantCulture);
    var marker = args[at++];
    var launcherKind = args[at];

    IFaultInjector fault = NoFaults.Instance;
    if (stepName != "-")
    {
        var step = TxSteps.All.Single(s => s.Name == stepName);
        fault = new StopHere(step, edge, nth, marker);
    }

    ILaunchVerifier? launcher = launcherKind switch
    {
        "ok" => new Scripted(LaunchVerdict.Ok),
        "fail" => new Scripted(LaunchVerdict.Failed),
        _ => null,
    };

    var seams = new TxSeams { Fault = fault, Launcher = launcher, Durable = false }; // a kill loses nothing the operating system holds: no flush to the disk
    using var gate = InstallGate.EnterExclusive(Path.Combine(install, "PCMieru.exe"));
    if (!gate.IsOpen)
    {
        Console.Error.WriteLine("gate " + gate.Reason);
        return Crashed;
    }

    var result = swap
        ? SwapTransaction.Run(gate, new SwapRequest(staged!), seams)
        : TxRecovery.Recover(gate, seams, automatic: false);
    Console.Out.WriteLine($"result {result.Outcome} {result.Reason}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("crashed: " + ex);
    return Crashed;
}

/// <summary>Writes the marker and then never returns: the process is killed from outside.</summary>
internal sealed class StopHere(TxStep step, TxEdge edge, int nth, string marker) : IFaultInjector
{
    private readonly Dictionary<(string, TxEdge), int> _counts = [];

    public void Reach(TxStep reached, TxEdge reachedEdge, string? item)
    {
        var key = (reached.Name, reachedEdge);
        _counts.TryGetValue(key, out var n);
        _counts[key] = ++n;
        if (reached.Name == step.Name && reachedEdge == edge && n == nth)
        {
            File.WriteAllText(marker, $"{step.Name}/{edge}#{nth} {item}");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}

internal sealed class Scripted(LaunchVerdict verdict) : ILaunchVerifier
{
    public LaunchVerdict LaunchAndVerify(LaunchRequest request) => verdict;
}
