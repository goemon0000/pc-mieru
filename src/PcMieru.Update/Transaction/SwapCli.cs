using System.Diagnostics;
using System.Globalization;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

/// <summary>
/// The exit codes of <c>PCMieru.Swap.exe</c> (and <c>recover.exe</c>, which is a copy of it), one number per outcome. They are part of the interface: the start gate window and the update screen read them, the trial
/// script reads them, and the same table is printed by <c>PCMieru.Swap.exe --help</c>. A code is never reused for another meaning.
/// <code>
///   0  NothingToDo        the recovery found nothing open (it may have tidied leftovers)
///   1  HandedOver         (the installed PCMieru.Swap.exe only) recover.exe was made ready and started; it does the work and shows the result itself
///   2  BadCommandLine     the arguments are not understood
///   3  Crashed            an unexpected exception (nothing in the install is changed by the code that handles it)
///   4  TestSwitchMissing  the test-only entry was asked for without PCMIERU_SWAP_TEST (see <see cref="SwapCli.TestVariable"/>)
///  10  Committed          a swap ended with the new version in place (or a recovery finished it: the commit had been decided)
///  11  RolledBack         the earlier version was put back, verified (the new version is NOT installed)
///  12  Aborted            the swap stopped before its first change (nothing in the install changed)
///  20  RecoveryFailed     the journal and the backup are kept; run recover.exe again or unzip the ZIP into a new folder
///  30  Busy               a program of the install holds the start gate (close PC Mieru and run again)
///  31  GateRefused        the gate cannot be entered (missing, access denied, a link, unreadable)
///  40  WrongPlace         this exe is neither I\PCMieru.Swap.exe nor I\update\recover.exe: it does nothing to an install
///  41  RunnerUnavailable  recover.exe could not be made ready or started (it is not the recorded Swap.exe, or a file operation failed)
/// </code>
/// </summary>
public static class SwapExit
{
    public const int NothingToDo = 0;
    public const int HandedOver = 1;
    public const int BadCommandLine = 2;
    public const int Crashed = 3;
    public const int TestSwitchMissing = 4;
    public const int Committed = 10;
    public const int RolledBack = 11;
    public const int Aborted = 12;
    public const int RecoveryFailed = 20;
    public const int Busy = 30;
    public const int GateRefused = 31;
    public const int WrongPlace = 40;
    public const int RunnerUnavailable = 41;

    /// <summary>The code of an outcome of the engine.</summary>
    public static int For(TxOutcome outcome) => outcome switch
    {
        TxOutcome.NothingToDo => NothingToDo,
        TxOutcome.Committed => Committed,
        TxOutcome.RolledBack => RolledBack,
        TxOutcome.Aborted => Aborted,
        TxOutcome.RecoveryFailed => RecoveryFailed,
        _ => Crashed,
    };

    /// <summary>Whether the code says the install can be started as it is (nothing to do, a commit, a rollback, an abort).</summary>
    public static bool InstallUsable(int code) => code is NothingToDo or Committed or RolledBack or Aborted;
}

/// <summary>Everything <see cref="SwapCli"/> takes from outside. The defaults are the real ones; a test replaces what it needs.</summary>
public sealed class SwapHost
{
    /// <summary>The FINAL path of the exe that is running (the folder through <c>InstallGate.InstallFolderOf</c>, then the file name).</summary>
    public required string SelfPath { get; init; }

    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public Func<string, string?> GetVariable { get; init; } = static name => Environment.GetEnvironmentVariable(name);

    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>What the transaction takes from outside (disk space, the placement, the clock). The launcher of a recovery stays null.</summary>
    public TxSeams Seams { get; init; } = TxSeams.Production();

    public IRunnerStarter Starter { get; init; } = ProcessRunnerStarter.Instance;

    /// <summary>Waits until the process with this id has ended (true), or the time is up (false). A process that is not running counts as ended.</summary>
    public Func<int, TimeSpan, bool> WaitForProcessEnd { get; init; } = SwapProcesses.WaitForEnd;

    public Action<TimeSpan> Sleep { get; init; } = static span => Thread.Sleep(span);

    /// <summary>True when this process is the only one on its console (a double-click on recover.exe): the window would close the moment the work ends, so the result is left on screen for a while.</summary>
    public Func<bool> ConsoleIsAlone { get; init; } = static () => TxNative.ConsoleIsOwnedByThisProcess();

    /// <summary>How long a swap waits for the programs of the install to let go of the gate (the process that asked for the swap is closing).</summary>
    public TimeSpan SwapGateWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The same for a recovery run by a person (they were told to close PC Mieru).</summary>
    public TimeSpan RecoverGateWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a process named by <c>--after-pid</c> may take to end.</summary>
    public TimeSpan AfterPidWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the result stays on screen when <see cref="ConsoleIsAlone"/>.</summary>
    public TimeSpan ReadingPause { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>The one real call of the process helpers: wait for a process to end.</summary>
public static class SwapProcesses
{
    public static bool WaitForEnd(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // not running (any more), or not ours to ask about: nothing to wait for
        }
    }
}

/// <summary>
/// The program of <c>PCMieru.Swap.exe</c> (DESIGN-B 5 and 13 item 3), as a library so that it is tested in-process: the exe is only <c>Program.cs</c> (library search pinned to System32, console encoding) and a call to
/// <see cref="Run"/>. No network code is reachable from here: this file, <see cref="RunnerHandOff"/> and the Transaction folder name no System.Net type (the exe is NativeAOT, so what it cannot reach is not in it;
/// tools/publish.ps1 checks the imports of the built exe and that no network string is in it).
///
/// <b>Two roles, decided from the process's own path (<see cref="RunnerRules"/>).</b>
/// <list type="bullet">
/// <item><b>Runner</b>: the exe is <c>I\update\recover.exe</c>. It takes the gate exclusively, then does what it was asked: <c>--recover</c> (also: no argument at all, which is what a double-click does) runs the
/// recovery; <c>--test-swap</c> (test only) runs a swap of a staged folder with a scripted verdict instead of a launch.</item>
/// <item><b>Launcher</b>: the exe is <c>I\PCMieru.Swap.exe</c>. It does NOT swap or recover (a swap replaces this very file, and a running exe cannot be replaced). It only makes sure a trusted
/// <c>I\update\recover.exe</c> is there (<see cref="RunnerHandOff.Prepare"/>), starts it with the same arguments plus <c>--after-pid</c> (its own id), and ends at once, so that nothing runs from
/// <c>I\PCMieru.Swap.exe</c> while the swap or the recovery replaces it.</item>
/// </list>
/// Anywhere else the exe does nothing to an install (exit 40).
///
/// <b>The test-only entry</b> (<c>--test-swap</c>) puts a SCRIPTED verdict in the place of the launch of the new version, so a swap can end as "committed" without anything having started: it must never be reachable
/// by accident. It is refused (exit 4, before anything is placed or written) unless the environment variable <see cref="TestVariable"/> holds the full path of the install folder the exe belongs to: the
/// switch names ONE folder, so a variable that is left set somewhere else opens nothing, and a person has to type the folder to use it. Nothing in the product sets it.
/// </summary>
public static class SwapCli
{
    /// <summary>The environment variable that switches the test-only entry on for ONE install folder (its full path).</summary>
    public const string TestVariable = "PCMIERU_SWAP_TEST";

    private const string AfterPidOption = "--after-pid";
    private const string ThenStartOption = "--then-start";
    private const string RecoverOption = "--recover";
    private const string TestSwapOption = "--test-swap";
    private static readonly TimeSpan GatePoll = TimeSpan.FromMilliseconds(250);

    public const string Usage =
        "PCMieru.Swap.exe (recover.exe is a copy of it)\n"
        + "  --recover                         recover an interrupted update (no argument does the same)\n"
        + "  --after-pid <n>                   wait for that process to end first (set by whoever starts it)\n"
        + "  --then-start                      (with --recover) start PCMieru.exe afterwards when the install can be used; set by the start gate window's Recover button\n"
        + "  --test-swap <staged> <step|-> <edge> <nth> <marker> <ok|fail>\n"
        + "                                    TEST ONLY: swap a staged folder with a scripted verdict; needs the environment variable PCMIERU_SWAP_TEST = the install folder\n"
        + "exit codes: 0 nothing to do, 1 handed over to recover.exe, 2 bad command line, 3 crashed, 4 test switch missing,\n"
        + "            10 committed, 11 rolled back, 12 aborted (nothing changed), 20 recovery failed, 30 busy, 31 gate refused,\n"
        + "            40 wrong place (not I\\PCMieru.Swap.exe or I\\update\\recover.exe), 41 recover.exe could not be made ready";

    /// <summary>
    /// The arguments the start gate window's "Recover" button gives <c>I\update\recover.exe</c> (the one place the option names are spelled for a caller): recover, after the process with this id
    /// (the window's own program, whose image cannot be replaced while it runs) has ended, and start PCMieru.exe once the install can be used.
    /// </summary>
    public static IReadOnlyList<string> RecoverAndStartArguments(int afterPid) =>
        [RecoverOption, AfterPidOption, afterPid.ToString(CultureInfo.InvariantCulture), ThenStartOption];

    private enum CommandKind
    {
        Recover,
        TestSwap,
    }

    private sealed record Command(CommandKind Kind, int? AfterPid, bool ThenStart, IReadOnlyList<string> Tokens, string Staged, TxStep? Step, TxEdge Edge, int Nth, string Marker, bool VerdictOk);

    /// <summary>Runs the program and returns its exit code (<see cref="SwapExit"/>). Never throws, except for a <see cref="PowerCutException"/> that a test injector throws on purpose.</summary>
    public static int Run(string[] args, SwapHost host)
    {
        try
        {
            return RunCore(args, host);
        }
        catch (Exception ex) when (ex is not PowerCutException and not OutOfMemoryException)
        {
            host.Error.WriteLine("swap: unexpected " + ex.GetType().Name);
            return SwapExit.Crashed;
        }
    }

    private static int RunCore(string[] args, SwapHost host)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h" or "/?")
        {
            host.Out.WriteLine(Usage);
            return SwapExit.NothingToDo;
        }

        var command = Parse(args, out var problem);
        if (command is null)
        {
            host.Error.WriteLine("swap: " + problem);
            host.Error.WriteLine(Usage);
            return SwapExit.BadCommandLine;
        }

        var site = RunnerRules.Locate(host.SelfPath);
        if (site.Role == RunnerRole.Refused)
        {
            host.Error.WriteLine("swap: this exe is neither I\\PCMieru.Swap.exe nor I\\update\\recover.exe of an install, so it does nothing");
            Say(host, "この PCMieru.Swap.exe は、PCミエルのフォルダーの中で実行してください。", "Run PCMieru.Swap.exe from inside the PC Mieru folder (recover.exe from its update folder).");
            return SwapExit.WrongPlace;
        }

        if (command.Kind == CommandKind.TestSwap && !TestSwitchOn(host, site.InstallFolder))
        {
            host.Error.WriteLine($"swap: {TestSwapOption} is a test-only entry; it needs the environment variable {TestVariable} set to the full path of the install folder");
            return SwapExit.TestSwitchMissing;
        }

        return site.Role == RunnerRole.Runner ? RunRunner(host, site.InstallFolder, command) : RunLauncher(host, site.InstallFolder, command);
    }

    // ------------------------------------------------------------------------------------------------------------ the command line

    private static Command? Parse(string[] args, out string problem)
    {
        problem = string.Empty;
        int? after = null;
        var thenStart = false;
        var tokens = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == ThenStartOption)
            {
                thenStart = true;
                continue;
            }

            if (args[i] != AfterPidOption)
            {
                tokens.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                problem = AfterPidOption + " needs a process id";
                return null;
            }

            after = pid;
            i++;
        }

        if (tokens.Count == 0 || (tokens.Count == 1 && tokens[0] == RecoverOption))
        {
            return new Command(CommandKind.Recover, after, thenStart, tokens, string.Empty, null, TxEdge.Before, 0, string.Empty, false);
        }

        if (tokens[0] != TestSwapOption || tokens.Count != 7)
        {
            problem = "the arguments are not understood";
            return null;
        }

        if (thenStart)
        {
            problem = ThenStartOption + " goes with --recover only";
            return null;
        }

        TxStep? step = null;
        if (tokens[2] != "-")
        {
            step = TxSteps.All.FirstOrDefault(s => s.Name == tokens[2]);
            if (step is null)
            {
                problem = "unknown step '" + tokens[2] + "'";
                return null;
            }
        }

        if (!Enum.TryParse<TxEdge>(tokens[3], ignoreCase: false, out var edge) || !Enum.IsDefined(edge))
        {
            problem = "unknown edge '" + tokens[3] + "' (Before, Mid or After)";
            return null;
        }

        if (!int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out var nth) || nth < 1)
        {
            problem = "the count must be a number from 1";
            return null;
        }

        if (tokens[6] is not ("ok" or "fail"))
        {
            problem = "the verdict is ok or fail";
            return null;
        }

        return new Command(CommandKind.TestSwap, after, false, tokens, tokens[1], step, edge, nth, tokens[5], tokens[6] == "ok");
    }

    private static bool TestSwitchOn(SwapHost host, string installFolder)
    {
        var value = host.GetVariable(TestVariable);
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

    // ------------------------------------------------------------------------------------------------------------ the Launcher

    private static int RunLauncher(SwapHost host, string install, Command command)
    {
        var paths = new TxPaths(install);
        var ready = RunnerHandOff.Prepare(install);
        if (!ready.IsReady)
        {
            host.Error.WriteLine($"swap: recover.exe is not ready: {ready.Status}: {ready.Detail}");
            Say(host, "回復用のプログラム（recover.exe）を用意できませんでした。ZIP を新しいフォルダーに展開して使ってください。", "recover.exe could not be made ready. Unzip the ZIP into a new folder and use that copy.");
            return SwapExit.RunnerUnavailable;
        }

        var arguments = new List<string> { AfterPidOption, host.ProcessId.ToString(CultureInfo.InvariantCulture) };
        if (command.ThenStart)
        {
            arguments.Add(ThenStartOption);
        }

        arguments.AddRange(command.Tokens);
        if (!host.Starter.TryStart(paths.Recover, install, arguments, out var detail))
        {
            host.Error.WriteLine("swap: recover.exe could not be started: " + detail);
            Say(host, "回復用のプログラム（recover.exe）を起動できませんでした。ZIP を新しいフォルダーに展開して使ってください。", "recover.exe could not be started. Unzip the ZIP into a new folder and use that copy.");
            return SwapExit.RunnerUnavailable;
        }

        host.Out.WriteLine("result HandedOver exit=" + SwapExit.HandedOver.ToString(CultureInfo.InvariantCulture));
        Say(host, "recover.exe に引き渡しました。結果はそちらの画面に出ます。", "Handed over to recover.exe. It shows the result itself.");
        return SwapExit.HandedOver;
    }

    // ------------------------------------------------------------------------------------------------------------ the Runner

    private static int RunRunner(SwapHost host, string install, Command command)
    {
        var code = RunnerWork(host, install, command); // the gate is let go of when this returns: a program that is started below can enter it
        var started = false;
        if (command.ThenStart && SwapExit.InstallUsable(code))
        {
            // The start gate window's "Recover" button: it handed over and ended (its own exe cannot be replaced while it runs), so the program is started again here, once the install is in order.
            var exe = Path.Combine(install, "PCMieru.exe");
            started = host.Starter.TryStart(exe, install, [], out var detail);
            if (started)
            {
                Say(host, "PCミエルを起動しました。", "Starting PC Mieru.");
            }
            else
            {
                host.Error.WriteLine("swap: PCMieru.exe could not be started: " + detail);
                Say(host, "PCミエルを起動できませんでした。いつもどおり、自分で起動してください。", "PC Mieru could not be started from here. Please start it as usual.");
            }
        }

        if (command.Kind == CommandKind.Recover && !started && host.ConsoleIsAlone())
        {
            host.Sleep(host.ReadingPause); // a double-click: the window is this process's own and goes away when it ends, so the result is left to be read
        }

        return code;
    }

    private static int RunnerWork(SwapHost host, string install, Command command)
    {
        if (command.AfterPid is { } pid)
        {
            host.WaitForProcessEnd(pid, host.AfterPidWait); // the process that handed over (the installed Swap.exe, or the program that is closing) must be gone before anything is replaced
        }

        var wait = command.Kind == CommandKind.Recover ? host.RecoverGateWait : host.SwapGateWait;
        using var gate = EnterGate(host, install, wait);
        if (!gate.IsOpen)
        {
            if (gate.Outcome == GateOutcome.Busy)
            {
                host.Out.WriteLine("result Gate Busy exit=" + SwapExit.Busy.ToString(CultureInfo.InvariantCulture));
                Say(host, "PCミエルのファイルが使用中です。PCミエルをすべて終了してから、もう一度実行してください。", "PC Mieru's files are in use. Close PC Mieru completely, then run this again.");
                return SwapExit.Busy;
            }

            host.Out.WriteLine($"result Gate {gate.Outcome} exit={SwapExit.GateRefused.ToString(CultureInfo.InvariantCulture)}");
            host.Error.WriteLine("swap: " + gate.Reason + " (" + gate.Detail + ")");
            Say(host, "PCミエルの起動の門を開けませんでした。ZIP を新しいフォルダーに展開して使ってください。", "The start gate could not be opened. Unzip the ZIP into a new folder and use that copy. (" + gate.Reason + ")");
            return SwapExit.GateRefused;
        }

        TxResult result;
        if (command.Kind == CommandKind.Recover)
        {
            result = TxRecovery.Recover(gate, host.Seams, automatic: false);
        }
        else
        {
            var seams = new TxSeams
            {
                Fault = command.Step is { } step ? new StopHere(step, command.Edge, command.Nth, command.Marker) : host.Seams.Fault,
                Disk = host.Seams.Disk,
                Launcher = new ScriptedLauncher(command.VerdictOk ? LaunchVerdict.Ok : LaunchVerdict.Failed),
                Placement = host.Seams.Placement,
                UtcNow = host.Seams.UtcNow,
            };
            result = SwapTransaction.Run(gate, new SwapRequest(command.Staged), seams);
        }

        var exit = SwapExit.For(result.Outcome);
        host.Out.WriteLine($"result {result.Outcome} {result.Reason} exit={exit.ToString(CultureInfo.InvariantCulture)}");
        Say(host, SwapSentences.Ja(result), SwapSentences.En(result));
        return exit;
    }

    private static GateEntry EnterGate(SwapHost host, string install, TimeSpan wait)
    {
        var exe = Path.Combine(install, "PCMieru.exe");
        var waited = TimeSpan.Zero;
        var said = false;
        while (true)
        {
            var gate = InstallGate.EnterExclusive(exe);
            if (gate.Outcome != GateOutcome.Busy || waited >= wait)
            {
                return gate;
            }

            if (!said)
            {
                said = true;
                Say(host, "PCミエルが終わるのを待っています。", "Waiting for PC Mieru to close.");
            }

            host.Sleep(GatePoll);
            waited += GatePoll;
        }
    }

    private static void Say(SwapHost host, string ja, string en)
    {
        host.Out.WriteLine(ja);
        host.Out.WriteLine(en);
    }

    // ------------------------------------------------------------------------------------------------------------ the test-only seams

    /// <summary>Writes the marker and then never returns: the test kills the process there (a power cut in a real process).</summary>
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
                TxNative.WriteTestMarker(marker, $"{step.Name}/{edge}#{nth} {item}");
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

/// <summary>The plain sentences of what <see cref="SwapCli"/> prints after the machine line (Japanese, then English).</summary>
public static class SwapSentences
{
    public static string Ja(TxResult result) => result.Outcome switch
    {
        TxOutcome.NothingToDo => "回復が必要なものはありませんでした。PCミエルをいつもどおり起動できます。",
        TxOutcome.Committed => $"新しい版（{result.ToVersion}）に入れ替えました。PCミエルをいつもどおり起動できます。",
        TxOutcome.RolledBack => TxReasonText.Ja(result.Reason) + " PCミエルをいつもどおり起動できます。",
        TxOutcome.Aborted => TxReasonText.Ja(result.Reason),
        _ => TxReasonText.Ja(result.Reason) is { Length: > 0 } text ? text : "元に戻す作業が終わりませんでした。ZIP を新しいフォルダーに展開して使ってください。",
    };

    public static string En(TxResult result) => result.Outcome switch
    {
        TxOutcome.NothingToDo => "Nothing needed to be recovered. You can start PC Mieru as usual.",
        TxOutcome.Committed => $"Updated to the new version ({result.ToVersion}). You can start PC Mieru as usual.",
        TxOutcome.RolledBack => TxReasonText.En(result.Reason) + " You can start PC Mieru as usual.",
        TxOutcome.Aborted => TxReasonText.En(result.Reason),
        _ => TxReasonText.En(result.Reason) is { Length: > 0 } text ? text : "Putting the earlier version back did not finish. Unzip the ZIP into a new folder and use that copy.",
    };
}
