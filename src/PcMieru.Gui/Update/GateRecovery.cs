using PcMieru.Update.Transaction;
using PcMieru.Wire.Gate;

namespace PcMieru.Gui;

/// <summary>
/// What the start gate window's 「回復する」 button needs from outside (DESIGN-B 13 item 4): whether recovery can be offered at all, and the start of it. The window never touches a file or a process itself;
/// a test gives it a fake, so no process is started by a test.
/// </summary>
internal interface IGateRecovery
{
    /// <summary>True when the button is to be shown: the entry is an interrupted update (pending or failed) and <c>I\update\recover.exe</c> is a file the installed record or the journal vouches for.</summary>
    bool IsAvailable(GateEntry entry);

    /// <summary>
    /// Starts the recovery and does not wait for it (see <see cref="RunnerGateRecovery"/>). False, with a short English line for a log, when it could not be started: the window then says so and takes
    /// the button away.
    /// </summary>
    bool Start(GateEntry entry, out string detail);
}

/// <summary>
/// The real recovery start. <b>It hands over; it does not wait.</b> The window runs inside <c>PCMieru.exe</c>, and a running exe cannot be replaced, so a recovery that waited here would fail on exactly the
/// file it has to put back. So the button starts <c>I\update\recover.exe --recover --after-pid &lt;this process&gt; --then-start</c> and the window ends the app; recover.exe waits for this process to be gone,
/// takes the gate, recovers, and, when the install can be used, starts PCMieru.exe again (the gate is let go of by then). recover.exe has its own console window, where it says what it did, in plain words.
///
/// It starts recover.exe only when <see cref="RunnerHandOff.Check"/> says it is the Swap.exe that installed.json or the journal records (checked again right before the start). Anything else (no file, another
/// file, a link in the update folder) and there is no button: the text under the window is then the plain advice of a new folder from the downloaded ZIP.
/// </summary>
internal sealed class RunnerGateRecovery(IRunnerStarter? starter = null, int? processId = null) : IGateRecovery
{
    private readonly IRunnerStarter _starter = starter ?? ProcessRunnerStarter.Instance;
    private readonly int _processId = processId ?? Environment.ProcessId;

    public static RunnerGateRecovery Instance { get; } = new();

    public bool IsAvailable(GateEntry entry) => InstallOf(entry) is { } install && RunnerHandOff.Check(install).IsReady;

    public bool Start(GateEntry entry, out string detail)
    {
        if (InstallOf(entry) is not { } install)
        {
            detail = "the install folder is not known";
            return false;
        }

        var ready = RunnerHandOff.Check(install);
        if (!ready.IsReady)
        {
            detail = ready.Status + ": " + ready.Detail;
            return false;
        }

        return _starter.TryStart(new TxPaths(install).Recover, install, SwapCli.RecoverAndStartArguments(_processId), out detail);
    }

    private static string? InstallOf(GateEntry entry)
    {
        if (entry.Outcome is not (GateOutcome.RecoveryPending or GateOutcome.RecoveryFailed))
        {
            return null; // only an interrupted update has anything to recover
        }

        var folder = Path.GetDirectoryName(entry.GatePath);
        return string.IsNullOrEmpty(folder) ? null : folder;
    }
}
