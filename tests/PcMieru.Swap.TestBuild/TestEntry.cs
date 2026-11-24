using System.Globalization;
using PcMieru.Update.Transaction;

namespace PcMieru.Swap.TestBuild;

/// <summary>
/// The test-only entry of the TEST BUILD of PCMieru.Swap.exe: <c>--test-swap &lt;staged&gt; &lt;step|-&gt; &lt;edge&gt; &lt;nth&gt; &lt;marker&gt; &lt;ok|fail&gt;</c>. It runs a swap of a staged folder with a SCRIPTED verdict instead of a
/// launch of the new version (so a swap can end as "committed" without anything having started), and, when a step is named, writes the marker and sleeps at the n-th time that step's edge is reached (the trial kills
/// the process there: a power cut in a real process).
///
/// <b>This file is not part of the product.</b> The shipped PCMieru.Swap.exe has no such entry, no <c>StopHere</c>, no infinite sleep and no marker writer: src/PcMieru.Swap/Program.cs reaches this class only behind
/// the compile symbol <c>PCM_SWAP_TESTBUILD</c>, which only tests/PcMieru.Swap.TestBuild sets (the product's csproj stops the build if it is set there, and tools/publish.ps1 looks for <c>--test-swap</c> and
/// <c>PCMIERU_SWAP_TEST</c> in the published exe, as ASCII and as UTF-16, and fails when either is found).
///
/// The swap runs through <see cref="SwapCli.RunRunner"/> and <see cref="SwapCli.HandOver"/>, which are the product's own: the exclusive gate, the waits, the report lines and the exit codes are the product's, and only
/// the work inside the gate (a swap with a scripted launcher, instead of a recovery) is different. The file is compiled into the test build exe and into tests/PcMieru.Update.Tests (which runs it in-process).
/// </summary>
public static class TestEntry
{
    /// <summary>The environment variable that switches the entry on for ONE install folder (its full path).</summary>
    public const string Variable = "PCMIERU_SWAP_TEST";

    /// <summary>The switch is missing or names another folder. A code of this build only; the product's table calls 4 retired.</summary>
    public const int SwitchMissing = 4;

    public const string Option = "--test-swap";

    public const string Usage =
        "PCMieru.Swap.exe, TEST BUILD (not the product)\n"
        + "  --test-swap <staged> <step|-> <edge> <nth> <marker> <ok|fail>\n"
        + "      swap a staged folder into the install this exe belongs to; the new version's launch is answered by <ok|fail> (nothing is started);\n"
        + "      at the <nth> time <step> reaches <edge> (Before, Mid or After) the process writes <marker> and sleeps (step - means run to the end).\n"
        + "      Refused (exit 4) unless the environment variable " + Variable + " holds the full path of the install folder.\n"
        + "  anything else is the product's own command line.";

    /// <summary>What the test build's Main does: the test entry when the command line is one, and the product's program for everything else.</summary>
    public static int Run(string[] args, SwapHost host, Func<string, string?> getVariable) =>
        TryRun(args, host, getVariable, out var exit) ? exit : SwapCli.Run(args, host);

    /// <summary>
    /// Runs the test entry when <paramref name="args"/> (after <c>--after-pid</c> and <c>--then-start</c> are taken out) start with <see cref="Option"/>; false, doing nothing, when they do not.
    /// Never throws, except for a <see cref="PowerCutException"/> that a test injector throws on purpose.
    /// </summary>
    public static bool TryRun(string[] args, SwapHost host, Func<string, string?> getVariable, out int exit)
    {
        exit = 0;
        if (!SwapCli.TryParseOptions(args, out var options, out _) || options.Rest.Count == 0 || options.Rest[0] != Option)
        {
            return false;
        }

        try
        {
            exit = RunCore(options, host, getVariable);
        }
        catch (Exception ex) when (ex is not PowerCutException and not OutOfMemoryException)
        {
            host.Error.WriteLine("swap: unexpected " + ex.GetType().Name);
            exit = SwapExit.Crashed;
        }

        return true;
    }

    private static int RunCore(SwapOptions options, SwapHost host, Func<string, string?> getVariable)
    {
        var tokens = options.Rest;
        if (tokens.Count != 7)
        {
            return Bad(host, "the arguments are not understood");
        }

        if (options.ThenStart)
        {
            return Bad(host, "--then-start goes with --recover only");
        }

        TxStep? step = null;
        if (tokens[2] != "-")
        {
            step = TxSteps.All.FirstOrDefault(s => s.Name == tokens[2]);
            if (step is null)
            {
                return Bad(host, "unknown step '" + tokens[2] + "'");
            }
        }

        if (!Enum.TryParse<TxEdge>(tokens[3], ignoreCase: false, out var edge) || !Enum.IsDefined(edge))
        {
            return Bad(host, "unknown edge '" + tokens[3] + "' (Before, Mid or After)");
        }

        if (!int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out var nth) || nth < 1)
        {
            return Bad(host, "the count must be a number from 1");
        }

        if (tokens[6] is not ("ok" or "fail"))
        {
            return Bad(host, "the verdict is ok or fail");
        }

        var site = RunnerRules.Locate(host.SelfPath);
        if (site.Role == RunnerRole.Refused)
        {
            host.Error.WriteLine("swap: this exe is neither I\\PCMieru.Swap.exe nor I\\update\\recover.exe of an install, so it does nothing");
            return SwapExit.WrongPlace;
        }

        if (SwapCli.RefuseIfElevated(host) is { } refused)
        {
            return refused;
        }

        if (!SwitchOn(getVariable, site.InstallFolder))
        {
            host.Error.WriteLine($"swap: {Option} is a test-only entry; it needs the environment variable {Variable} set to the full path of the install folder");
            return SwitchMissing;
        }

        if (site.Role == RunnerRole.Launcher)
        {
            // Like the product's launcher: it never swaps. It makes recover.exe ready and hands the whole command line (the test entry included) over to it.
            return SwapCli.HandOver(host, site.InstallFolder, thenStart: false, tokens);
        }

        var staged = tokens[1];
        var marker = tokens[5];
        var seams = host.Seams.With(
            fault: step is null ? null : new StopHere(step, edge, nth, marker),
            launcher: new ScriptedLauncher(tokens[6] == "ok" ? LaunchVerdict.Ok : LaunchVerdict.Failed));
        return SwapCli.RunRunner(host, site.InstallFolder, recovery: false, options.AfterPid, thenStart: false, gate => SwapTransaction.Run(gate, new SwapRequest(staged), seams));
    }

    /// <summary>
    /// The environment variable that names another L for the install this exe belongs to (the same switch as <see cref="Variable"/>: it works only when that one names the same install folder). The trial runs the
    /// runner's <c>--apply</c> against a scratch L that way; the product's runner always asks the system.
    /// </summary>
    public const string LocalVariable = "PCMIERU_SWAP_TEST_LOCAL";

    /// <summary>The scratch L the test switch names for the install of <paramref name="selfPath"/>; null (the system's L) when the switch is off, names another install, or the value is not a full path.</summary>
    public static string? LocalFolder(string selfPath, Func<string, string?> getVariable)
    {
        var site = RunnerRules.Locate(selfPath);
        if (site.Role == RunnerRole.Refused || !SwitchOn(getVariable, site.InstallFolder))
        {
            return null;
        }

        var value = getVariable(LocalVariable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? Path.TrimEndingDirectorySeparator(value) : null;
    }

    private static int Bad(SwapHost host, string problem)
    {
        host.Error.WriteLine("swap: " + problem);
        host.Error.WriteLine(Usage);
        return SwapExit.BadCommandLine;
    }

    private static bool SwitchOn(Func<string, string?> getVariable, string installFolder)
    {
        var value = getVariable(Variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Writes the marker and then never returns: the trial kills the process there (a power cut in a real process).</summary>
    private sealed class StopHere(TxStep step, TxEdge edge, int nth, string marker) : IFaultInjector
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

    /// <summary>The verdict is given by the command line: nothing is started.</summary>
    private sealed class ScriptedLauncher(LaunchVerdict verdict) : ILaunchVerifier
    {
        public LaunchVerdict LaunchAndVerify(LaunchRequest request) => verdict;
    }
}
