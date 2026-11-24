using System.Diagnostics;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>What the gate costs at every start. Measured and printed, never asserted: a number that a loaded machine can push over a limit would only teach people to ignore the test.</summary>
public sealed class GateCostTests : IDisposable
{
    private readonly Scratch _scratch = new();
    private readonly ITestOutputHelper _output;

    public GateCostTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void The_cost_of_one_start_of_the_gate_is_measured_cold_and_warm_and_printed()
    {
        var exe = _scratch.Install();

        // Cold: the first call of the process (types are loaded, the file system caches are not warmed by this code yet). The real exes pay this once per start.
        var cold = Stopwatch.StartNew();
        using (var first = InstallGate.EnterStartup(exe))
        {
            Assert.True(first.IsOpen, first.Detail);
        }

        cold.Stop();
        _output.WriteLine("gate, no update folder, first call of the process : " + Format(cold.Elapsed));

        const int Rounds = 2000;
        _output.WriteLine("gate, no update folder, mean of " + Rounds + " calls     : " + Format(Mean(exe, Rounds)));

        // The update folder is there (what an install that has updated once looks like: last-result.json and nothing that blocks).
        Directory.CreateDirectory(Scratch.UpdateOf(exe));
        File.WriteAllText(Path.Combine(Scratch.UpdateOf(exe), GateFile.LastResultName), "{ \"result\": \"ok\" }");
        _output.WriteLine("gate, update folder with a result, mean of " + Rounds + " calls: " + Format(Mean(exe, Rounds)));

        // Recovery pending: the cost of a refusal (it opens, looks and closes).
        File.WriteAllText(Path.Combine(Scratch.UpdateOf(exe), GateFile.JournalName), "{}");
        _output.WriteLine("gate, recovery pending (refused), mean of " + Rounds + " calls : " + Format(Mean(exe, Rounds)));
    }

    private static TimeSpan Mean(string exe, int rounds)
    {
        using (InstallGate.EnterStartup(exe))
        {
        }

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
        {
            using var gate = InstallGate.EnterStartup(exe);
        }

        watch.Stop();
        return watch.Elapsed / rounds;
    }

    private static string Format(TimeSpan time) => (time.TotalMilliseconds * 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " us";
}
