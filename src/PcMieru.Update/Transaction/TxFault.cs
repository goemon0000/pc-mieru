namespace PcMieru.Update.Transaction;

/// <summary>Where, around one change, a fault can be injected.</summary>
public enum TxEdge
{
    /// <summary>Right before the change is made.</summary>
    Before,

    /// <summary>In the middle of a write: the first half of the bytes is written, the second half and the flush are not (a power cut here leaves a half-written file). Only writes have this edge.</summary>
    Mid,

    /// <summary>Right after the change is done (and flushed).</summary>
    After,
}

/// <summary>
/// The seam that lets a test stop the process at a named step. The code of the transaction calls <see cref="Reach"/> before and after every change it makes to the disk, and in the middle of every write;
/// the production injector (<see cref="NoFaults"/>) does nothing. A test injector may throw <see cref="PowerCutException"/>, and from then on must throw it for every later call too (a power cut is final:
/// a <c>finally</c> that tries to tidy up changes nothing). The injector is also how the tests LEARN the steps: a recording injector sees every step the code really reaches, so the list of cut points
/// is generated from the code and a new step cannot be missed (see <see cref="TxSteps"/>).
/// </summary>
public interface IFaultInjector
{
    /// <summary>Called at <paramref name="step"/> / <paramref name="edge"/>. <paramref name="item"/> names what is being changed (a relative name, a file name), or null.</summary>
    void Reach(TxStep step, TxEdge edge, string? item);
}

/// <summary>The injector of production: nothing happens.</summary>
public sealed class NoFaults : IFaultInjector
{
    public static NoFaults Instance { get; } = new();

    public void Reach(TxStep step, TxEdge edge, string? item)
    {
    }
}

/// <summary>
/// "The power went off here." Thrown by a test injector. The transaction code never catches it (it catches <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> only, and a
/// <c>finally</c> block only disposes handles), so it unwinds to the test without a rollback, a cleanup or a last-result: what is on the disk is what a power cut would have left.
/// </summary>
public sealed class PowerCutException(string message) : Exception(message);

/// <summary>The phase of the journal (DESIGN-B 5). The text in the JSON is the camel-case name.</summary>
public enum TxPhase
{
    /// <summary>The new files and the backup are ready and verified; nothing in the install folder has been changed.</summary>
    Prepared,

    /// <summary>Files are being renamed into place (each with its intent written first).</summary>
    Swapping,

    /// <summary>The new set is in place and installed.json is the new record; the new version is being started and checked (attempts are counted before each launch).</summary>
    VerifyPending,

    /// <summary>The new version passed; the transaction is being finished (the journal is removed after the new set is verified once more).</summary>
    CommitIntent,

    /// <summary>The old set is being brought back.</summary>
    Rollback,
}

/// <summary>What kind of change a step is.</summary>
public enum TxStepKind
{
    /// <summary>Bytes are written to a file (copy, journal, record).</summary>
    Write,

    /// <summary>A file is renamed or replaced.</summary>
    Rename,

    /// <summary>A file or a folder is deleted.</summary>
    Delete,

    /// <summary>A call through a seam (the launch, the runner placement): the change is made by the other side.</summary>
    Seam,
}

/// <summary>One named step of the transaction: a place where the process may be stopped. Instances are made only by <see cref="TxSteps"/>.</summary>
public sealed class TxStep
{
    internal TxStep(string name, TxPhase phase, TxStepKind kind)
    {
        Name = name;
        Phase = phase;
        Kind = kind;
    }

    /// <summary>A stable dotted name ("swap.overwrite").</summary>
    public string Name { get; }

    /// <summary>The journal phase the step belongs to (the steps of cleanup belong to the phase that ended with the journal).</summary>
    public TxPhase Phase { get; }

    public TxStepKind Kind { get; }

    public override string ToString() => Name;
}

/// <summary>
/// Every step of the transaction, defined once. A step REGISTERS ITSELF here by being defined (<see cref="All"/> is the list of what was defined), the code reaches a step only through the object defined
/// here (so there is no step that is not in the list), and the tests learn which steps and which occurrences really run by recording a transaction, so a cut point is generated for each of them.
/// Two more tests keep it honest: every defined step is used by the code of Transaction/ (no dead step), and every file change of that code goes through <see cref="TxIo"/> (no change without a step).
/// </summary>
public static class TxSteps
{
    private static readonly List<TxStep> s_all = [];

    // Without the static constructor the runtime may initialize the fields late; the list must be complete the first time anything asks for it.
    static TxSteps()
    {
    }

    /// <summary>All steps, in the order they are defined (which is the order of a transaction).</summary>
    public static IReadOnlyList<TxStep> All => s_all;

    private static TxStep Define(string name, TxPhase phase, TxStepKind kind)
    {
        var step = new TxStep(name, phase, kind);
        s_all.Add(step);
        return step;
    }

    // ---------------------------------------------------------------------------------------------------------------- before the journal exists (Prepared)

    /// <summary>Removes what an earlier, never finished preparation left in work/ and prev-*/ (nothing in the install folder).</summary>
    public static readonly TxStep StaleClean = Define("prepare.stale-clean", TxPhase.Prepared, TxStepKind.Delete);

    /// <summary>Writes the first installed.json (a manually unzipped folder, after every file was checked against its manifest).</summary>
    public static readonly TxStep FirstRecord = Define("prepare.first-record", TxPhase.Prepared, TxStepKind.Write);

    /// <summary>Copies one new or changed file from the staged folder to work/&lt;tx&gt;/next and checks it.</summary>
    public static readonly TxStep NextCopy = Define("prepare.next-copy", TxPhase.Prepared, TxStepKind.Write);

    /// <summary>Copies one file of the old set to prev-&lt;old&gt;/&lt;tx&gt;/files and checks it.</summary>
    public static readonly TxStep BackupCopy = Define("prepare.backup-copy", TxPhase.Prepared, TxStepKind.Write);

    /// <summary>Copies the old installed.json to prev-&lt;old&gt;/&lt;tx&gt;.</summary>
    public static readonly TxStep BackupRecord = Define("prepare.backup-record", TxPhase.Prepared, TxStepKind.Write);

    /// <summary>The runner placement seam (recover.exe is copied and checked, RECOVER.txt is written).</summary>
    public static readonly TxStep RunnerPlace = Define("prepare.runner", TxPhase.Prepared, TxStepKind.Seam);

    /// <summary>recover.exe is copied from PCMieru.Swap.exe (inside the real placement).</summary>
    public static readonly TxStep RunnerCopy = Define("prepare.runner-copy", TxPhase.Prepared, TxStepKind.Write);

    /// <summary>RECOVER.txt is written (inside the real placement).</summary>
    public static readonly TxStep RecoverNote = Define("prepare.recover-note", TxPhase.Prepared, TxStepKind.Write);

    /// <summary>The first journal (phase Prepared): the commit of the preparation.</summary>
    public static readonly TxStep JournalPrepared = Define("journal.prepared", TxPhase.Prepared, TxStepKind.Write);

    // ---------------------------------------------------------------------------------------------------------------- Swapping

    /// <summary>The journal says Swapping (every operation still pending).</summary>
    public static readonly TxStep JournalSwapping = Define("journal.swapping", TxPhase.Swapping, TxStepKind.Write);

    /// <summary>The intent of the next operation is written (and the completion of the one before it).</summary>
    public static readonly TxStep JournalIntent = Define("journal.intent", TxPhase.Swapping, TxStepKind.Write);

    /// <summary>A name the old version did not have is renamed into place.</summary>
    public static readonly TxStep SwapAdd = Define("swap.add", TxPhase.Swapping, TxStepKind.Rename);

    /// <summary>A file is replaced by the new one.</summary>
    public static readonly TxStep SwapOverwrite = Define("swap.overwrite", TxPhase.Swapping, TxStepKind.Rename);

    /// <summary>A file the new version does not have goes to displaced/.</summary>
    public static readonly TxStep SwapDisplace = Define("swap.displace", TxPhase.Swapping, TxStepKind.Rename);

    /// <summary>installed.json becomes the new record.</summary>
    public static readonly TxStep SwapInstalled = Define("swap.installed", TxPhase.Swapping, TxStepKind.Write);

    // ---------------------------------------------------------------------------------------------------------------- VerifyPending

    /// <summary>The journal says VerifyPending.</summary>
    public static readonly TxStep JournalVerifyPending = Define("journal.verify-pending", TxPhase.VerifyPending, TxStepKind.Write);

    /// <summary>The launch attempt is counted in the journal, before the launch.</summary>
    public static readonly TxStep JournalAttempt = Define("journal.attempt", TxPhase.VerifyPending, TxStepKind.Write);

    /// <summary>The launch-and-verify seam.</summary>
    public static readonly TxStep Launch = Define("verify.launch", TxPhase.VerifyPending, TxStepKind.Seam);

    /// <summary>The failure tag of a version that did not start (or whose set did not hold) is written to blocked.json.</summary>
    public static readonly TxStep BlockedTag = Define("verify.blocked-tag", TxPhase.VerifyPending, TxStepKind.Write);

    // ---------------------------------------------------------------------------------------------------------------- CommitIntent

    /// <summary>The journal says CommitIntent.</summary>
    public static readonly TxStep JournalCommitIntent = Define("journal.commit-intent", TxPhase.CommitIntent, TxStepKind.Write);

    /// <summary>The journal is removed: the transaction is over and the new set is the install.</summary>
    public static readonly TxStep CommitJournalDelete = Define("commit.journal-delete", TxPhase.CommitIntent, TxStepKind.Delete);

    /// <summary>last-result.json is written.</summary>
    public static readonly TxStep LastResult = Define("result.last-result", TxPhase.CommitIntent, TxStepKind.Write);

    /// <summary>prev-&lt;old&gt;/ is removed (retryable; a failure never undoes the transaction).</summary>
    public static readonly TxStep CleanupPrev = Define("cleanup.prev", TxPhase.CommitIntent, TxStepKind.Delete);

    /// <summary>work/ is removed (retryable).</summary>
    public static readonly TxStep CleanupWork = Define("cleanup.work", TxPhase.CommitIntent, TxStepKind.Delete);

    /// <summary>RECOVER.txt is removed (retryable).</summary>
    public static readonly TxStep CleanupNote = Define("cleanup.note", TxPhase.CommitIntent, TxStepKind.Delete);

    // ---------------------------------------------------------------------------------------------------------------- Rollback

    /// <summary>The journal says Rollback.</summary>
    public static readonly TxStep JournalRollback = Define("journal.rollback", TxPhase.Rollback, TxStepKind.Write);

    /// <summary>The recovery attempt is counted in the journal, before anything is changed.</summary>
    public static readonly TxStep JournalRecoveryAttempt = Define("journal.recovery-attempt", TxPhase.Rollback, TxStepKind.Write);

    /// <summary>An old file is copied from the backup to work/&lt;tx&gt;/restore and checked.</summary>
    public static readonly TxStep RestoreCopy = Define("rollback.restore-copy", TxPhase.Rollback, TxStepKind.Write);

    /// <summary>The restored copy is renamed over the file (or into its place).</summary>
    public static readonly TxStep RestorePlace = Define("rollback.restore-place", TxPhase.Rollback, TxStepKind.Rename);

    /// <summary>A file the new version added (and whose hash is the new one) is removed.</summary>
    public static readonly TxStep RemoveNew = Define("rollback.remove-new", TxPhase.Rollback, TxStepKind.Delete);

    /// <summary>A displaced file is moved back.</summary>
    public static readonly TxStep Undisplace = Define("rollback.undisplace", TxPhase.Rollback, TxStepKind.Rename);

    /// <summary>The old installed.json is put back (from prev-&lt;old&gt;).</summary>
    public static readonly TxStep RestoreInstalled = Define("rollback.installed", TxPhase.Rollback, TxStepKind.Write);

    /// <summary>The journal is removed after the old set was verified: the rollback is over.</summary>
    public static readonly TxStep RollbackJournalDelete = Define("rollback.journal-delete", TxPhase.Rollback, TxStepKind.Delete);

    /// <summary>The sentinel of a failed recovery is written (the journal and the backup stay).</summary>
    public static readonly TxStep SentinelWrite = Define("recovery.sentinel-write", TxPhase.Rollback, TxStepKind.Write);

    /// <summary>The sentinel is removed after a successful recovery.</summary>
    public static readonly TxStep SentinelDelete = Define("recovery.sentinel-delete", TxPhase.Rollback, TxStepKind.Delete);

    /// <summary>The stale temporary of a journal write is removed (a journal write was cut off).</summary>
    public static readonly TxStep JournalTmpDelete = Define("recovery.journal-tmp-delete", TxPhase.Rollback, TxStepKind.Delete);
}
