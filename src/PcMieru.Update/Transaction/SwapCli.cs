using System.Diagnostics;
using System.Globalization;
using PcMieru.Update.Launch;
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
///   4  (retired)          was the code of an entry that only a test build has; no code of the product, never reused
///  10  Committed          a swap ended with the new version in place (or a recovery finished it: the commit had been decided)
///  11  RolledBack         the earlier version was put back, verified (the new version is NOT installed)
///  12  Aborted            the swap stopped before its first change (nothing in the install changed)
///  20  RecoveryFailed     the journal and the backup are kept; run recover.exe again or unzip the ZIP into a new folder
///  30  Busy               a program of the install is using PC Mieru's files: it holds the start gate, or a file could not even be moved aside, or the program that asked is still closing.
///                         Nothing is lost, the journal is kept and no failure mark is written: close PC Mieru and run recover.exe again
///  31  GateRefused        the gate cannot be entered (missing, access denied, a link, unreadable)
///  32  Elevated           this exe runs as administrator (an elevated token): it neither swaps nor recovers, it changes nothing and says to open it again with normal rights
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
    public const int Committed = 10;
    public const int RolledBack = 11;
    public const int Aborted = 12;
    public const int RecoveryFailed = 20;
    public const int Busy = 30;
    public const int GateRefused = 31;
    public const int Elevated = 32;
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
        TxOutcome.Busy => Busy,
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

    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>What the transaction takes from outside (disk space, the placement, the clock, the file system of the volume). The launcher of a recovery stays null.</summary>
    public TxSeams Seams { get; init; } = TxSeams.Production();

    public IRunnerStarter Starter { get; init; } = ProcessRunnerStarter.Instance;

    /// <summary>Waits until the process with this id has ended (true), or the time is up (false). A process that is not running counts as ended.</summary>
    public Func<int, TimeSpan, bool> WaitForProcessEnd { get; init; } = SwapProcesses.WaitForEnd;

    public Action<TimeSpan> Sleep { get; init; } = static span => Thread.Sleep(span);

    /// <summary>True when this process is the only one on its console (a double-click on recover.exe): the window would close the moment the work ends, so the result is left on screen for a while.</summary>
    public Func<bool> ConsoleIsAlone { get; init; } = static () => TxNative.ConsoleIsOwnedByThisProcess();

    /// <summary>Whether this process runs with an elevated token (<see cref="ProcessElevation.IsElevated"/>). When it does, nothing is swapped or recovered (<see cref="SwapExit.Elevated"/>).</summary>
    public Func<bool> IsElevated { get; init; } = static () => ProcessElevation.IsElevated();

    /// <summary>How long a swap waits for the programs of the install to let go of the gate (the process that asked for the swap is closing).</summary>
    public TimeSpan SwapGateWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The same for a recovery run by a person (they were told to close PC Mieru).</summary>
    public TimeSpan RecoverGateWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a process named by <c>--after-pid</c> may take to end.</summary>
    public TimeSpan AfterPidWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the result stays on screen when <see cref="ConsoleIsAlone"/>.</summary>
    public TimeSpan ReadingPause { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>%LOCALAPPDATA%\PCMieru</c> (L: gui.ini and the state folders of the installs). Null: asked of the system (SHGetKnownFolderPath, the same call the GUI makes, which the environment variable cannot move). Only the TEST
    /// build of the runner sets it (see tests/PcMieru.Swap.TestBuild); the product never does.
    /// </summary>
    public string? LocalFolder { get; init; }

    /// <summary>
    /// The arguments of the final start of <c>I\PCMieru.exe</c> after a swap, a rollback, an abort or a recovery (<see cref="SwapCli.StartInstalledProgram"/>). Always empty in the product: PC Mieru is started plainly, as a person would
    /// start it. Only the TEST BUILD of the runner fills it (see tests/PcMieru.Swap.TestBuild), so that a trial can start the version that is installed in a way that reads and writes nothing of the owner's.
    /// </summary>
    public IReadOnlyList<string> ProgramArguments { get; init; } = [];

    /// <summary>Takes the two handles of <c>--apply</c> (the pipe, the process of the program that asked) and returns what the runner talks and waits with; null when they are not a pipe and a process.</summary>
    public Func<nint, nint, ApplyEndpoint?> OpenApplyEndpoint { get; init; } = static (pipe, process) => ApplyEndpoint.FromInherited(pipe, process);

    /// <summary>The launch check of a swap, made for the gate this process holds. The real one starts <c>I\PCMieru.exe --verify-update</c>.</summary>
    public Func<GateEntry, ILaunchVerifier> LauncherFor { get; init; } = static gate => new ProductionLaunchVerifier(gate);

    /// <summary>How long the message on the pipe of <c>--apply</c> may take to come.</summary>
    public TimeSpan ApplyMessageWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the accepted hand-off gives the originating GUI to save its work and exit.</summary>
    public TimeSpan ApplyGuiExitWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether a normal GUI is holding the start gate after the originating GUI exited.</summary>
    internal Func<string, bool> HasGuiGateHolder { get; init; } = static gate =>
        RestartManager.ReadHolders(gate)?.Any(holder => holder.Name.Equals("PCMieru", StringComparison.OrdinalIgnoreCase)) == true;
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

/// <summary>The options every command line of the program may carry, taken out of it (see <see cref="SwapCli.TryParseOptions"/>); <see cref="Rest"/> is what is left.</summary>
public sealed record SwapOptions(int? AfterPid, bool ThenStart, IReadOnlyList<string> Rest);

/// <summary>
/// The program of <c>PCMieru.Swap.exe</c> (DESIGN-B 5 and 13 item 3), as a library so that it is tested in-process: the exe is only <c>Program.cs</c> (library search pinned to System32, console encoding) and a call to
/// <see cref="Run"/>. No network code is reachable from here: this file, <see cref="RunnerHandOff"/> and the Transaction folder name no System.Net type (the exe is NativeAOT, so what it cannot reach is not in it;
/// tools/publish.ps1 checks the imports of the built exe and that no network string is in it).
///
/// <b>Two roles, decided from the process's own path (<see cref="RunnerRules"/>).</b>
/// <list type="bullet">
/// <item><b>Runner</b>: the exe is <c>I\update\recover.exe</c>. It takes the gate exclusively, then runs the recovery (<c>--recover</c>; no argument at all does the same, which is what a double-click does).</item>
/// <item><b>Launcher</b>: the exe is <c>I\PCMieru.Swap.exe</c>. It does NOT swap or recover (a swap replaces this very file, and a running exe cannot be replaced). It only makes sure a trusted
/// <c>I\update\recover.exe</c> is there (<see cref="RunnerHandOff.Prepare"/>), starts it with the same arguments plus <c>--after-pid</c> (its own id), and ends at once, so that nothing runs from
/// <c>I\PCMieru.Swap.exe</c> while the swap or the recovery replaces it.</item>
/// </list>
/// Anywhere else the exe does nothing to an install (exit 40). With an elevated token it does nothing at all (exit 32).
///
/// <b>There is no test entry in this program.</b> A swap that ends as "committed" without anything having started needs a scripted verdict, and a program that can take one must never be what is shipped: the entry lives
/// only in the TEST BUILD of the runner (tests/PcMieru.Swap.TestBuild, built from the same Program.cs with a compile symbol the product never sets). The test build runs its swap through <see cref="RunRunner"/> and
/// <see cref="HandOver"/>, which are public for that reason, so the gate, the waits, the reports and the exit codes are the product's own. tools/publish.ps1 proves that the published exe has no trace of the entry.
/// </summary>
public static class SwapCli
{
    private const string AfterPidOption = "--after-pid";
    private const string ThenStartOption = "--then-start";
    private const string RecoverOption = "--recover";
    private static readonly TimeSpan GatePoll = TimeSpan.FromMilliseconds(250);

    public const string Usage =
        "PCMieru.Swap.exe (recover.exe is a copy of it)\n"
        + "  --recover                         recover an interrupted update (no argument does the same)\n"
        + "  --after-pid <n>                   wait for that process to end first (set by whoever starts it)\n"
        + "  --then-start                      (with --recover) start PCMieru.exe afterwards when the install can be used; set by the start gate window's Recover button\n"
        + "  --apply <pipe> <process>          (recover.exe only; PC Mieru starts it, a person does not) install the update that PC Mieru names on the pipe. It needs the two handles\n"
        + "                                    that PC Mieru passes down and one valid message on the pipe; without them it changes nothing (exit 2). It answers \"accepted\" or a refusal\n"
        + "                                    (exit 12, nothing changed), waits for PC Mieru to close (exit 30 if it does not), swaps, checks the new version, and starts PC Mieru again\n"
        + "exit codes: 0 nothing to do, 1 handed over to recover.exe, 2 bad command line, 3 crashed,\n"
        + "            10 committed, 11 rolled back, 12 aborted or refused (nothing changed), 20 recovery failed, 30 busy (a program is using PC Mieru's files), 31 gate refused,\n"
        + "            32 refused: running as administrator (open it again with normal rights),\n"
        + "            40 wrong place (not I\\PCMieru.Swap.exe or I\\update\\recover.exe), 41 recover.exe could not be made ready";

    /// <summary>
    /// The arguments the start gate window's "Recover" button gives <c>I\update\recover.exe</c> (the one place the option names are spelled for a caller): recover, after the process with this id
    /// (the window's own program, whose image cannot be replaced while it runs) has ended, and start PCMieru.exe once the install can be used.
    /// </summary>
    public static IReadOnlyList<string> RecoverAndStartArguments(int afterPid) =>
        [RecoverOption, AfterPidOption, afterPid.ToString(CultureInfo.InvariantCulture), ThenStartOption];

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

        if (!TryParseOptions(args, out var options, out var problem))
        {
            return BadCommandLine(host, problem);
        }

        var rest = options.Rest;
        var applying = rest.Count > 0 && rest[0] == SwapApply.Option;
        if (applying)
        {
            if (rest.Count != 3 || options.AfterPid is not null || options.ThenStart || !TryHandle(rest[1], out var pipe) || !TryHandle(rest[2], out var process) || pipe == process)
            {
                return BadCommandLine(host, SwapApply.Option + " takes exactly a pipe handle and a process handle (numbers), and no other option");
            }

            var runnerSite = RunnerRules.Locate(host.SelfPath);
            if (runnerSite.Role != RunnerRole.Runner)
            {
                return runnerSite.Role == RunnerRole.Refused ? WrongPlace(host) : BadCommandLine(host, SwapApply.Option + " is for recover.exe only (PC Mieru starts it; the launcher PCMieru.Swap.exe does not apply updates)");
            }

            return SwapApply.Run(host, runnerSite.InstallFolder, pipe, process);
        }

        if (!(rest.Count == 0 || (rest.Count == 1 && rest[0] == RecoverOption)))
        {
            return BadCommandLine(host, "the arguments are not understood");
        }

        var site = RunnerRules.Locate(host.SelfPath);
        if (site.Role == RunnerRole.Refused)
        {
            return WrongPlace(host);
        }

        if (RefuseIfElevated(host) is { } refused)
        {
            return refused;
        }

        return site.Role == RunnerRole.Runner
            ? RunRunner(host, site.InstallFolder, recovery: true, options.AfterPid, options.ThenStart, gate => TxRecovery.Recover(gate, host.Seams, automatic: false))
            : HandOver(host, site.InstallFolder, options.ThenStart, rest);
    }

    private static int BadCommandLine(SwapHost host, string problem)
    {
        host.Error.WriteLine("swap: " + problem);
        host.Error.WriteLine(Usage);
        return SwapExit.BadCommandLine;
    }

    private static int WrongPlace(SwapHost host)
    {
        host.Error.WriteLine("swap: this exe is neither I\\PCMieru.Swap.exe nor I\\update\\recover.exe of an install, so it does nothing");
        Say(host, "この PCMieru.Swap.exe は、PCミエルのフォルダーの中で実行してください。", "Run PCMieru.Swap.exe from inside the PC Mieru folder (recover.exe from its update folder).");
        return SwapExit.WrongPlace;
    }

    /// <summary>A handle value on a command line: decimal digits only, not zero, and one that fits the 32 bits Windows gives handle values that cross processes.</summary>
    private static bool TryHandle(string text, out nint value)
    {
        value = 0;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0 || number > uint.MaxValue)
        {
            return false;
        }

        value = (nint)number;
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ the command line

    /// <summary>
    /// Takes <c>--after-pid &lt;n&gt;</c> and <c>--then-start</c> out of the arguments (wherever they stand); what is left is <see cref="SwapOptions.Rest"/>. False, with the problem in words, when
    /// <c>--after-pid</c> has no process id after it.
    /// </summary>
    public static bool TryParseOptions(string[] args, out SwapOptions options, out string problem)
    {
        problem = string.Empty;
        options = new SwapOptions(null, false, []);
        int? after = null;
        var thenStart = false;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == ThenStartOption)
            {
                thenStart = true;
                continue;
            }

            if (args[i] != AfterPidOption)
            {
                rest.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                problem = AfterPidOption + " needs a process id";
                return false;
            }

            after = pid;
            i++;
        }

        options = new SwapOptions(after, thenStart, rest);
        return true;
    }

    /// <summary>
    /// With an elevated token nothing is swapped and nothing is recovered: what an elevated program writes is owned by the administrators group and carries the ACL of an elevated creator, so the normal start of PC Mieru
    /// could no longer replace it, and a recovery would reach files that a person did not expect an administrator's program to touch. Returns <see cref="SwapExit.Elevated"/> (after saying so, in plain words) or null.
    /// It is asked before anything is placed, entered or written.
    /// </summary>
    public static int? RefuseIfElevated(SwapHost host)
    {
        if (!host.IsElevated())
        {
            return null;
        }

        host.Out.WriteLine("result Refused Elevated exit=" + SwapExit.Elevated.ToString(CultureInfo.InvariantCulture));
        host.Error.WriteLine("swap: running as administrator (an elevated token); nothing was changed");
        Say(host, TxReasonText.Ja(TxReason.Elevated), TxReasonText.En(TxReason.Elevated));
        return SwapExit.Elevated;
    }

    // ------------------------------------------------------------------------------------------------------------ the Launcher

    /// <summary>
    /// The Launcher's work (<c>I\PCMieru.Swap.exe</c>): make a trusted recover.exe ready, start it with <paramref name="tokens"/> (and <c>--then-start</c> when asked) and its own process id after <c>--after-pid</c>, and
    /// end at once. Public because the test build of the runner hands over its own arguments the same way.
    /// </summary>
    public static int HandOver(SwapHost host, string install, bool thenStart, IReadOnlyList<string> tokens)
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
        if (thenStart)
        {
            arguments.Add(ThenStartOption);
        }

        arguments.AddRange(tokens);
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

    /// <summary>
    /// The Runner's work (<c>I\update\recover.exe</c>): wait for the process that handed over, take the gate EXCLUSIVELY (waiting for the programs of the install to let go of it), run <paramref name="work"/> with it,
    /// say the result (a machine line, then two plain sentences) and return the exit code. For a <paramref name="recovery"/> the gate is waited for the recovery's own time and the result is left on screen in a console of
    /// its own. Public for the test build of the runner, which gives a swap as the work; the product gives the recovery.
    ///
    /// <b>The gate stays held for the whole of <paramref name="work"/>, the verification of a new version included.</b> The program that is started to verify a new version does not enter the gate: it gets the exclusive
    /// handle passed down from here (DESIGN-B 5, "設計上の実行条件": limited handle inheritance, an anonymous pipe and the transaction id; step 5 builds that), so no other program can enter while the files are being judged.
    /// The gate is let go of only when the work has ended; only after that, with <paramref name="thenStart"/>, is PCMieru.exe started, and it enters the gate on its own.
    /// </summary>
    public static int RunRunner(SwapHost host, string install, bool recovery, int? afterPid, bool thenStart, Func<GateEntry, TxResult> work)
    {
        var code = RunnerWork(host, install, recovery, afterPid, thenStart, work); // the gate is held until this returns (the whole work, a verification included) and is released then, never earlier
        var started = false;
        if (thenStart && SwapExit.InstallUsable(code))
        {
            // The start gate window's "Recover" button: it handed over and ended (its own exe cannot be replaced while it runs), so the program is started again here, once the install is in order.
            started = StartInstalledProgram(host, install);
        }

        if (recovery && !started && host.ConsoleIsAlone())
        {
            host.Sleep(host.ReadingPause); // a double-click: the window is this process's own and goes away when it ends, so the result is left to be read
        }

        return code;
    }

    private static int RunnerWork(SwapHost host, string install, bool recovery, int? afterPid, bool thenStart, Func<GateEntry, TxResult> work)
    {
        if (afterPid is { } pid && !host.WaitForProcessEnd(pid, host.AfterPidWait) && thenStart)
        {
            // The program that handed over (the start gate window) was told to close and is still there after the wait. Its image is a file of the install that the recovery may have to replace, and the program that
            // is started afterwards would run beside it: do not go on, say so, and start nothing. Nothing was changed; the person closes it and presses Recover again.
            host.Out.WriteLine("result AfterPid Busy exit=" + SwapExit.Busy.ToString(CultureInfo.InvariantCulture));
            Say(host, "PCミエルがまだ終了していません。PCミエルをすべて終了してから、もう一度実行してください。", "PC Mieru has not closed yet. Close PC Mieru completely, then run this again.");
            return SwapExit.Busy;
        }

        var wait = recovery ? host.RecoverGateWait : host.SwapGateWait;
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

        var result = work(gate);
        var exit = SwapExit.For(result.Outcome);
        host.Out.WriteLine($"result {result.Outcome} {result.Reason} exit={exit.ToString(CultureInfo.InvariantCulture)}");
        Say(host, SwapSentences.Ja(result), SwapSentences.En(result));
        return exit;
    }

    /// <summary>Starts <c>I\PCMieru.exe</c> normally (no argument; <see cref="SwapHost.ProgramArguments"/> is empty in the product), after the gate was let go of; says so (or says that it could not). True when it was started.</summary>
    internal static bool StartInstalledProgram(SwapHost host, string install)
    {
        var exe = Path.Combine(install, "PCMieru.exe");
        var started = host.Starter.TryStart(exe, install, host.ProgramArguments, out var detail);
        if (started)
        {
            Say(host, "PCミエルを起動しました。", "Starting PC Mieru.");
        }
        else
        {
            host.Error.WriteLine("swap: PCMieru.exe could not be started: " + detail);
            Say(host, "PCミエルを起動できませんでした。いつもどおり、自分で起動してください。", "PC Mieru could not be started from here. Please start it as usual.");
        }

        return started;
    }

    internal static GateEntry EnterGate(SwapHost host, string install, TimeSpan wait)
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

    internal static void Say(SwapHost host, string ja, string en)
    {
        host.Out.WriteLine(ja);
        host.Out.WriteLine(en);
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
        TxOutcome.Busy => TxReasonText.Ja(result.Reason) is { Length: > 0 } busy ? busy : TxReasonText.Ja(TxReason.FilesInUse),
        _ => TxReasonText.Ja(result.Reason) is { Length: > 0 } text ? text : "元に戻す作業が終わりませんでした。ZIP を新しいフォルダーに展開して使ってください。",
    };

    public static string En(TxResult result) => result.Outcome switch
    {
        TxOutcome.NothingToDo => "Nothing needed to be recovered. You can start PC Mieru as usual.",
        TxOutcome.Committed => $"Updated to the new version ({result.ToVersion}). You can start PC Mieru as usual.",
        TxOutcome.RolledBack => TxReasonText.En(result.Reason) + " You can start PC Mieru as usual.",
        TxOutcome.Aborted => TxReasonText.En(result.Reason),
        TxOutcome.Busy => TxReasonText.En(result.Reason) is { Length: > 0 } busy ? busy : TxReasonText.En(TxReason.FilesInUse),
        _ => TxReasonText.En(result.Reason) is { Length: > 0 } text ? text : "Putting the earlier version back did not finish. Unzip the ZIP into a new folder and use that copy.",
    };
}
