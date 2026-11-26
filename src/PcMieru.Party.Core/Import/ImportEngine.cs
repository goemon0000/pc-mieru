using System.Globalization;
using System.Text;
using PcMieru.Party.Game;

namespace PcMieru.Party.Import;

/// <summary>What a call of <see cref="ImportEngine"/> came to. Always with a stable reason code (<see cref="ImportReason"/>).</summary>
public enum ImportOutcome
{
    /// <summary>Plan: the source can be imported as it is. Commit it.</summary>
    Ready,

    /// <summary>Plan: the source can be imported after repairs. Show <see cref="ImportPlan.RepairedFields"/>, get consent, then commit.</summary>
    NeedsRepair,

    /// <summary>Result: the converted save is at the destination (<see cref="ImportResult.Repaired"/> says whether it was repaired).</summary>
    Imported,

    /// <summary>Result: a fresh save was written to the destination.</summary>
    StartedFresh,

    /// <summary>The destination holds a save that can be read. Nothing was written.</summary>
    AlreadyHave,

    /// <summary>No old save exists to continue from. The new window may start fresh.</summary>
    NoCandidate,

    /// <summary>The destination exists but cannot be read as a save. It is kept as it is; the reason says why.</summary>
    DestinationBlocked,

    /// <summary>The old product runs, or it could not be told. Hold: ask the user to close it and try again.</summary>
    OldAppRunning,

    /// <summary>The first candidate exists but cannot be read (deny, sharing violation, I/O error). The next candidate is not tried.</summary>
    SourceUnreadable,

    /// <summary>The source is not the version that was inspected. Nothing was written.</summary>
    SourceChanged,

    /// <summary>Commit: the source needs repairs and the user has not consented.</summary>
    NeedsConsent,

    /// <summary>The source must not be imported (<see cref="StateImport"/> held it); the reason is the <see cref="ImportReason"/> of the hold.</summary>
    Hold,

    /// <summary>Result of <see cref="ImportEngine.StartFreshIfStillNone"/>: an old save appeared after the plan saw none. Nothing was written; prepare again.</summary>
    CandidateAppeared,

    /// <summary>The import lock is kept by somebody else.</summary>
    Busy,

    /// <summary>Writing failed or did not verify; the temp files of this call are gone.</summary>
    Failed,
}

/// <summary>The answer of <see cref="ImportEngine.Prepare"/>. Plain data; preparing wrote nothing.</summary>
public sealed record ImportPlan
{
    public required ImportOutcome Status { get; init; }

    /// <summary>An <see cref="ImportReason"/> code.</summary>
    public required string Reason { get; init; }

    /// <summary>Short English text for logs and tests. Not for parsing.</summary>
    public string? Detail { get; init; }

    /// <summary>The candidate that was found (set once one was found, also when it then could not be read or must be held).</summary>
    public ImportCandidate? Candidate { get; init; }

    /// <summary>Identity, size and SHA-256 of the source as it was inspected. Commit reads the source again and insists on the same.</summary>
    public SourceFingerprint? Source { get; init; }

    public int? SourceVersion { get; init; }

    /// <summary>Known fields that would be replaced by defaults (<see cref="ImportOutcome.NeedsRepair"/>).</summary>
    public IReadOnlyList<string> RepairedFields { get; init; } = [];

    /// <summary>Known fields that are deliberately not carried (today only <c>UpdateMode</c>).</summary>
    public IReadOnlyList<string> DroppedFields { get; init; } = [];

    /// <summary>True for <see cref="ImportOutcome.Ready"/> and <see cref="ImportOutcome.NeedsRepair"/>.</summary>
    public bool CanCommit => Status is ImportOutcome.Ready or ImportOutcome.NeedsRepair;
}

/// <summary>The answer of <see cref="ImportEngine.Commit"/> and <see cref="ImportEngine.StartFresh"/>.</summary>
public sealed record ImportResult
{
    public required ImportOutcome Outcome { get; init; }

    /// <summary>An <see cref="ImportReason"/> code.</summary>
    public required string Reason { get; init; }

    public string? Detail { get; init; }

    public ImportCandidate? Candidate { get; init; }

    /// <summary>For <see cref="ImportOutcome.Imported"/>: whether known fields were repaired on the way.</summary>
    public bool Repaired { get; init; }

    public IReadOnlyList<string> RepairedFields { get; init; } = [];

    /// <summary>For a repaired import: the untouched original, kept next to the new save (<c>imported-original-&lt;UTC&gt;.json</c>).</summary>
    public string? OriginalCopy { get; init; }

    public bool Succeeded => Outcome is ImportOutcome.Imported or ImportOutcome.StartedFresh;
}

/// <summary>
/// The import of the old Parts Life save, with no UI (DESIGN-C 4.1 items 1 to 5). The bytes are converted by the pure
/// <see cref="StateImport.Inspect"/>; this finds the file, reads it so that a change is seen, asks whether the old product runs, and writes
/// the result with the tmp-then-move rule. In two phases, because the user decides in between:
///  - <see cref="Prepare"/> writes nothing;
///  - <see cref="Commit"/> checks everything again and then writes;
///  - <see cref="StartFresh"/> writes a new save only when there is none.
/// Every call takes the per-user <see cref="ImportLock"/> and gives it back before it returns (on the calling thread; the calls are synchronous).
/// The old save is never written, moved or deleted: it is opened with <c>FileAccess.Read</c> and <c>FileShare.ReadWrite | FileShare.Delete</c>.
/// The destination is never overwritten: an existing one wins, whatever its state.
/// </summary>
public sealed class ImportEngine
{
    /// <summary>How long a call waits for the import lock.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    private readonly ImportPaths _paths;
    private readonly ILegacyRunningProbe _probe;
    private readonly string _lockName;
    private readonly TimeSpan _lockTimeout;

    /// <summary>Tests: called with the path of the finished temp file, just before it is moved to the destination.</summary>
    internal Action<string>? BeforeMove { get; set; }

    /// <summary>Tests: called with the path of the finished, verified temp file of the original copy, just before it is moved to its final name.</summary>
    internal Action<string>? BeforeOriginalMove { get; set; }

    /// <summary>Tests: the clock of the original copy's name.</summary>
    internal Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

    public ImportEngine(ImportPaths paths, ILegacyRunningProbe probe)
        : this(paths, probe, ImportLock.CurrentName(), DefaultLockTimeout)
    {
    }

    /// <summary>The lock name and how long to wait for it are parameters so that tests use their own.</summary>
    public ImportEngine(ImportPaths paths, ILegacyRunningProbe probe, string lockName, TimeSpan lockTimeout)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _lockName = lockName ?? throw new ArgumentNullException(nameof(lockName));
        _lockTimeout = lockTimeout;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Prepare
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Looks at the destination, finds the candidate, asks the old product's probe, reads the source so that a change is seen, and runs
    /// <see cref="StateImport.Inspect"/>. Writes NOTHING (not even the destination folder).
    /// </summary>
    public ImportPlan Prepare()
    {
        using var gate = ImportLock.TryAcquire(_lockName, _lockTimeout);
        if (gate is null) return Plan(ImportOutcome.Busy, ImportReason.LockBusy);
        try
        {
            return PrepareCore();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Plan(ImportOutcome.Failed, ImportReason.InternalError, e.GetType().Name);
        }
    }

    private ImportPlan PrepareCore()
    {
        var dest = CheckDestination();
        if (dest.Kind == DestinationKind.Valid) return Plan(ImportOutcome.AlreadyHave, ImportReason.AlreadyHave);
        if (dest.Kind == DestinationKind.Blocked) return Plan(ImportOutcome.DestinationBlocked, dest.Reason!, dest.Detail);

        // The old product is asked BEFORE any candidate is opened: while it runs, our open handle must not even exist.
        var old = ProbeOldApp();
        if (old is not null) return Plan(ImportOutcome.OldAppRunning, old);

        var located = Locate();
        if (located.Outcome != ImportOutcome.Ready)
            return Plan(located.Outcome, located.Reason, located.Detail, located.Candidate);
        var candidate = located.Candidate!;

        var read = SourceFile.ReadStable(candidate.Path, StateImport.MaxBytes + 1);
        if (!read.Ok) return Plan(FromBadRead(read, out string reason), reason, read.Detail, candidate);

        var verdict = StateImport.Inspect(read.Bytes!);
        if (verdict.Status == ImportStatus.Hold)
        {
            return Plan(ImportOutcome.Hold, verdict.Reason ?? ImportReason.InternalError, verdict.Detail, candidate) with
            {
                Source = read.Fingerprint,
                SourceVersion = verdict.SourceVersion,
            };
        }

        bool repair = verdict.Status == ImportStatus.NeedsRepair;
        return new ImportPlan
        {
            Status = repair ? ImportOutcome.NeedsRepair : ImportOutcome.Ready,
            Reason = repair ? ImportReason.NeedsRepair : ImportReason.Accept,
            Candidate = candidate,
            Source = read.Fingerprint,
            SourceVersion = verdict.SourceVersion,
            RepairedFields = verdict.RepairedFields,
            DroppedFields = verdict.DroppedFields,
        };
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Commit
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks everything again (destination, old product, the source's identity, size and SHA-256, the conversion) and only then writes.
    /// A plan that cannot be committed comes back as its own status and reason, and nothing is done.
    /// A plan that needs repairs and no <paramref name="consentToRepair"/> gives <see cref="ImportOutcome.NeedsConsent"/>.
    /// </summary>
    public ImportResult Commit(ImportPlan plan, bool consentToRepair)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanCommit || plan.Candidate is null || plan.Source is null)
        {
            return new ImportResult { Outcome = plan.Status, Reason = plan.Reason, Detail = plan.Detail, Candidate = plan.Candidate };
        }

        using var gate = ImportLock.TryAcquire(_lockName, _lockTimeout);
        if (gate is null) return Result(ImportOutcome.Busy, ImportReason.LockBusy, null, plan.Candidate);
        try
        {
            return CommitCore(plan, consentToRepair);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Result(ImportOutcome.Failed, ImportReason.InternalError, e.GetType().Name, plan.Candidate);
        }
    }

    private ImportResult CommitCore(ImportPlan plan, bool consent)
    {
        var candidate = plan.Candidate!;
        if (!_paths.Candidates.Any(c => string.Equals(c.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
            return Result(ImportOutcome.Failed, ImportReason.InternalError, "the plan is not from this engine", candidate);

        var dest = CheckDestination();
        if (dest.Kind == DestinationKind.Valid) return Result(ImportOutcome.AlreadyHave, ImportReason.AlreadyHave, null, candidate);
        if (dest.Kind == DestinationKind.Blocked) return Result(ImportOutcome.DestinationBlocked, dest.Reason!, dest.Detail, candidate);

        var old = ProbeOldApp();
        if (old is not null) return Result(ImportOutcome.OldAppRunning, old, null, candidate);

        // The discovery is run again: a PartsLife that appeared (or became unreadable) after the plan chose PartsParty is another source.
        var located = Locate();
        bool same = located.Candidate is not null && string.Equals(located.Candidate.Path, candidate.Path, StringComparison.OrdinalIgnoreCase);
        if (!same)
            return Result(ImportOutcome.SourceChanged, ImportReason.SourceChanged, "the candidate is no longer the one that was inspected", candidate);
        if (located.Outcome != ImportOutcome.Ready)
            return Result(located.Outcome, located.Reason, located.Detail, candidate);

        var read = SourceFile.ReadStable(candidate.Path, StateImport.MaxBytes + 1);
        if (!read.Ok) return Result(FromBadRead(read, out string reason), reason, read.Detail, candidate);
        if (read.Fingerprint != plan.Source)
            return Result(ImportOutcome.SourceChanged, ImportReason.SourceChanged, "identity, size or SHA-256 differs from the inspected version", candidate);

        // The bytes of THIS read are the ones converted: the verdict of Prepare is not trusted, it is made again.
        var verdict = StateImport.Inspect(read.Bytes!);
        if (verdict.Status == ImportStatus.Hold)
            return Result(ImportOutcome.Hold, verdict.Reason ?? ImportReason.InternalError, verdict.Detail, candidate);

        bool repair = verdict.Status == ImportStatus.NeedsRepair;
        if (repair && (!consent || !plan.RepairedFields.SequenceEqual(verdict.RepairedFields)))
        {
            // No consent, or consent that was given for another list of fields.
            return Result(ImportOutcome.NeedsConsent, ImportReason.NeedsConsent, null, candidate) with { RepairedFields = verdict.RepairedFields };
        }

        // Immediately before the final move: the discovery is run again (the pick must be the same candidate), the source is read once more
        // (the two reads and the fingerprint compare), and only then the old product is asked: the probe is the LAST thing before the move.
        ImportResult? LastCheck()
        {
            var again = Locate();
            if (again.Candidate is null || !string.Equals(again.Candidate.Path, candidate.Path, StringComparison.OrdinalIgnoreCase))
                return Result(ImportOutcome.SourceChanged, ImportReason.SourceChanged, "the candidate changed while the save was being written", candidate);
            if (again.Outcome != ImportOutcome.Ready) return Result(again.Outcome, again.Reason, again.Detail, candidate);
            var reread = SourceFile.ReadStable(candidate.Path, StateImport.MaxBytes + 1);
            if (!reread.Ok) return Result(FromBadRead(reread, out string why), why, reread.Detail, candidate);
            if (reread.Fingerprint != plan.Source)
                return Result(ImportOutcome.SourceChanged, ImportReason.SourceChanged, "the source changed while the save was being written", candidate);
            var running = ProbeOldApp();
            if (running is not null) return Result(ImportOutcome.OldAppRunning, running, "it started while the save was being written", candidate);
            return null;
        }

        var published = Publish(verdict.Converted!, repair ? read.Bytes : null, LastCheck);
        if (published.Outcome != ImportOutcome.Imported) return published with { Candidate = candidate };
        return published with { Candidate = candidate, Repaired = repair, RepairedFields = verdict.RepairedFields };
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // StartFresh
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes <see cref="PartyState.Fresh"/> to the destination when there is no destination (the old save is not looked at: the user chose
    /// not to continue it). An existing destination is never touched: a readable one gives <see cref="ImportOutcome.AlreadyHave"/>, one that
    /// cannot be read gives <see cref="ImportOutcome.DestinationBlocked"/> with its reason.
    /// </summary>
    public ImportResult StartFresh()
    {
        using var gate = ImportLock.TryAcquire(_lockName, _lockTimeout);
        if (gate is null) return Result(ImportOutcome.Busy, ImportReason.LockBusy, null, null);
        try
        {
            var dest = CheckDestination();
            if (dest.Kind == DestinationKind.Valid) return Result(ImportOutcome.AlreadyHave, ImportReason.AlreadyHave, null, null);
            if (dest.Kind == DestinationKind.Blocked) return Result(ImportOutcome.DestinationBlocked, dest.Reason!, dest.Detail, null);

            byte[] fresh = new UTF8Encoding(false).GetBytes(PartyState.Fresh().ToJson());
            var published = Publish(fresh, null, null);
            return published.Outcome == ImportOutcome.Imported
                ? published with { Outcome = ImportOutcome.StartedFresh, Reason = ImportReason.StartedFresh }
                : published;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Result(ImportOutcome.Failed, ImportReason.InternalError, e.GetType().Name, null);
        }
    }

    /// <summary>
    /// The automatic first file after <see cref="ImportOutcome.NoCandidate"/>. Like <see cref="StartFresh"/>, but nothing is decided by the user here, so the
    /// "there is nothing to continue from" the plan saw is proven again under this lock: the destination, then (immediately before the final move) the
    /// candidates and last the old product. A candidate that appeared since gives <see cref="ImportOutcome.CandidateAppeared"/> (the caller prepares again);
    /// the old product running gives <see cref="ImportOutcome.OldAppRunning"/>. Nothing is written in either case.
    /// </summary>
    public ImportResult StartFreshIfStillNone()
    {
        using var gate = ImportLock.TryAcquire(_lockName, _lockTimeout);
        if (gate is null) return Result(ImportOutcome.Busy, ImportReason.LockBusy, null, null);
        try
        {
            var dest = CheckDestination();
            if (dest.Kind == DestinationKind.Valid) return Result(ImportOutcome.AlreadyHave, ImportReason.AlreadyHave, null, null);
            if (dest.Kind == DestinationKind.Blocked) return Result(ImportOutcome.DestinationBlocked, dest.Reason!, dest.Detail, null);

            ImportResult? LastCheck()
            {
                var found = Locate();
                if (found.Outcome != ImportOutcome.NoCandidate)
                    return Result(ImportOutcome.CandidateAppeared, ImportReason.CandidateAppeared, found.Candidate?.Origin.ToString(), found.Candidate);
                var running = ProbeOldApp();
                return running is null ? null : Result(ImportOutcome.OldAppRunning, running, "it started before the first file was written", null);
            }

            byte[] fresh = new UTF8Encoding(false).GetBytes(PartyState.Fresh().ToJson());
            var published = Publish(fresh, null, LastCheck);
            return published.Outcome == ImportOutcome.Imported
                ? published with { Outcome = ImportOutcome.StartedFresh, Reason = ImportReason.StartedFresh }
                : published;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Result(ImportOutcome.Failed, ImportReason.InternalError, e.GetType().Name, null);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Pieces
    // ------------------------------------------------------------------------------------------------------------------------------

    private enum DestinationKind { Absent, Valid, Blocked }

    private readonly record struct DestinationCheck(DestinationKind Kind, string? Reason = null, string? Detail = null);

    /// <summary>
    /// Opens the destination (never asks File.Exists first: "absent" is only FileNotFound / DirectoryNotFound) and reads it the way the app will
    /// (<see cref="PartyState.FromJson(string)"/>). A save the app can read is Valid. Anything else that is there is Blocked and left as it is.
    /// </summary>
    private DestinationCheck CheckDestination()
    {
        string text;
        try
        {
            using var fs = new FileStream(_paths.Destination, FileMode.Open, FileAccess.Read, SourceFile.ShareAll, bufferSize: 1, FileOptions.None);
            if (fs.Length > StateImport.MaxOutputBytes)
                return new DestinationCheck(DestinationKind.Blocked, ImportReason.DestinationTooLarge, $"{fs.Length} bytes");
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return SourceFile.Classify(e) switch
            {
                FileProblem.Missing => new DestinationCheck(DestinationKind.Absent),
                FileProblem.Denied => new DestinationCheck(DestinationKind.Blocked, ImportReason.DestinationDenied, e.GetType().Name),
                FileProblem.Sharing => new DestinationCheck(DestinationKind.Blocked, ImportReason.DestinationSharing, e.GetType().Name),
                _ => new DestinationCheck(DestinationKind.Blocked, ImportReason.DestinationIoError, e.GetType().Name),
            };
        }

        try
        {
            PartyState.FromJson(text, out _);
            return new DestinationCheck(DestinationKind.Valid);
        }
        catch (Exception e) when (LenientJson.IsReadError(e))
        {
            return new DestinationCheck(DestinationKind.Blocked, ImportReason.DestinationBroken, e.GetType().Name);
        }
    }

    /// <summary>
    /// The candidate: the first that opens. PartsLife missing (FileNotFound or DirectoryNotFound) moves on to PartsParty; anything else on a
    /// candidate that is there (deny, sharing violation, I/O error) stops with its own reason. Opened and closed again: the read is later.
    /// </summary>
    private (ImportOutcome Outcome, string Reason, string? Detail, ImportCandidate? Candidate) Locate()
    {
        foreach (var candidate in _paths.Candidates)
        {
            var problem = SourceFile.TryOpen(candidate.Path, out string? detail);
            switch (problem)
            {
                case null:
                    return (ImportOutcome.Ready, ImportReason.Accept, null, candidate);
                case FileProblem.Missing:
                    continue;
                default:
                    return (ImportOutcome.SourceUnreadable, SourceReason(problem.Value), detail, candidate);
            }
        }

        return (ImportOutcome.NoCandidate, ImportReason.NoCandidate, null, null);
    }

    /// <summary>Null when the old product is not running; otherwise the reason code of the hold.</summary>
    private string? ProbeOldApp()
    {
        LegacyRunningState state;
        try
        {
            state = _probe.Check();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            state = LegacyRunningState.Unknown;
        }

        return state switch
        {
            LegacyRunningState.NotRunning => null,
            LegacyRunningState.Running => ImportReason.OldAppRunning,
            _ => ImportReason.OldAppUnknown,
        };
    }

    private static string SourceReason(FileProblem problem) => problem switch
    {
        FileProblem.Denied => ImportReason.SourceDenied,
        FileProblem.Sharing => ImportReason.SourceSharing,
        _ => ImportReason.SourceIoError,
    };

    private static ImportOutcome FromBadRead(SourceRead read, out string reason)
    {
        if (read.Changed)
        {
            reason = ImportReason.SourceChanged;
            return ImportOutcome.SourceChanged;
        }

        reason = SourceReason(read.Problem ?? FileProblem.Io);
        return ImportOutcome.SourceUnreadable;
    }

    /// <summary>
    /// Writes the destination: the original copy first when one is wanted (a temp file, read back, then moved WITHOUT overwriting to its final
    /// name, so no partial file ever has that name), then the bytes to a unique temp file next to the destination, read back and verified,
    /// flushed to disk. <paramref name="lastCheck"/> runs immediately before the final move and may stop it with a result. The move is
    /// WITHOUT overwriting. When the destination appeared in the meantime it wins: only our own files are deleted. On any failure or stop the
    /// files of this call are deleted; nothing else is ever deleted.
    /// </summary>
    private ImportResult Publish(byte[] bytes, byte[]? original, Func<ImportResult?>? lastCheck)
    {
        var mine = new List<string>();   // files this call created and has not published (the original copy's final name counts as one)
        string? originalPath = null;
        try
        {
            Directory.CreateDirectory(_paths.DestinationDir);
            if (original is not null) originalPath = WriteOriginalCopy(original, mine);
            string temp = WriteTemp(bytes, mine);
            BeforeMove?.Invoke(temp);
            var stop = lastCheck?.Invoke();
            if (stop is not null)
            {
                foreach (string path in mine) DeleteQuietly(path);
                return stop;
            }

            File.Move(temp, _paths.Destination, overwrite: false);
            return Result(ImportOutcome.Imported, ImportReason.Imported, null, null) with { OriginalCopy = originalPath };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            foreach (string path in mine) DeleteQuietly(path);

            if (e is VerifyFailedException v) return Result(ImportOutcome.Failed, ImportReason.VerifyFailed, v.Message, null);

            // The move fails when the destination is there. Then the one that is there wins, whatever its state.
            var dest = CheckDestination();
            if (dest.Kind == DestinationKind.Valid) return Result(ImportOutcome.AlreadyHave, ImportReason.AlreadyHave, null, null);
            if (dest.Kind == DestinationKind.Blocked) return Result(ImportOutcome.DestinationBlocked, dest.Reason!, dest.Detail, null);
            return Result(ImportOutcome.Failed, ImportReason.WriteFailed, e.GetType().Name + ": " + e.Message, null);
        }
    }

    /// <summary>
    /// Like the destination: a unique <c>CreateNew</c> temp file, flushed to disk, read back and compared with the source bytes, and only then
    /// moved without overwriting to <c>imported-original-&lt;UTC&gt;.json</c> (an existing copy is never overwritten: the next gets <c>-2</c>, ...).
    /// </summary>
    private string WriteOriginalCopy(byte[] original, List<string> mine)
    {
        string stamp = UtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string tmp = Path.Combine(_paths.DestinationDir, $"imported-original.import-{Guid.NewGuid():N}.tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            mine.Add(tmp);
            fs.Write(original);
            fs.Flush(flushToDisk: true);
        }

        if (!File.ReadAllBytes(tmp).AsSpan().SequenceEqual(original))
            throw new VerifyFailedException("the original copy does not read back as the bytes that were written");

        BeforeOriginalMove?.Invoke(tmp);

        for (int n = 1; ; n++)
        {
            string name = n == 1 ? $"imported-original-{stamp}.json" : $"imported-original-{stamp}-{n}.json";
            string path = Path.Combine(_paths.DestinationDir, name);
            try
            {
                File.Move(tmp, path, overwrite: false);
            }
            catch (IOException e) when (n < 20 && SourceFile.IsAlreadyExists(e))
            {
                continue;   // two attempts within one second: the second gets its own name
            }

            mine.Remove(tmp);
            mine.Add(path);
            return path;
        }
    }

    private string WriteTemp(byte[] bytes, List<string> mine)
    {
        string path = Path.Combine(_paths.DestinationDir, $"{ImportPaths.StateFileName}.import-{Guid.NewGuid():N}.tmp");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            mine.Add(path);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }

        // Read it back from the disk file the way the app will, before it is published.
        byte[] back = File.ReadAllBytes(path);
        if (!back.AsSpan().SequenceEqual(bytes))
            throw new VerifyFailedException("the temp file does not read back as the bytes that were written");
        try
        {
            PartyState.FromJson(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(back), out _);
        }
        catch (Exception e) when (LenientJson.IsReadError(e))
        {
            throw new VerifyFailedException("PartyState.FromJson cannot read the temp file: " + e.GetType().Name);
        }

        return path;
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: it is our own file, and there is nothing else to do about it
        }
    }

    private sealed class VerifyFailedException(string message) : Exception(message);

    private static ImportPlan Plan(ImportOutcome status, string reason, string? detail = null, ImportCandidate? candidate = null) =>
        new() { Status = status, Reason = reason, Detail = detail, Candidate = candidate };

    private static ImportResult Result(ImportOutcome outcome, string reason, string? detail, ImportCandidate? candidate) =>
        new() { Outcome = outcome, Reason = reason, Detail = detail, Candidate = candidate };
}
