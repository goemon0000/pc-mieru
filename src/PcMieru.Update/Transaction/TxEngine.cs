using System.Security.Cryptography;
using PcMieru.Update.Bundle;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

/// <summary>What a swap is asked to install: a folder that already passed the bundle checks (step 3). The transaction still checks every file it copies against the folder's own manifest.</summary>
public sealed record SwapRequest(string StagedFolder);

/// <summary>
/// The swap (DESIGN-B 5, 11 step 4): <see cref="Run"/> takes the staged folder of a verified bundle and replaces the installed set with it, or leaves the install as it was. The caller holds the gate EXCLUSIVELY
/// (<c>InstallGate.EnterExclusive</c>) for the whole call; nothing else is needed from the caller except a <see cref="ILaunchVerifier"/> in the seams (the "launch and verify" of the new version).
///
/// What happens, in order: the gate and the update folder are checked; there must be no open journal; the current record is read (or, for a manually unzipped folder, made from its own manifest AFTER every file
/// was checked against it); the old set is verified; the new files are copied to the same volume and checked; the old set is copied to prev-&lt;old&gt; and checked; the runner (recover.exe) is placed; the journal is
/// committed; files are renamed into place one by one with the intent written first; the new set and the gate file's identity are verified; installed.json is replaced; the new version is launched and verified
/// (up to three attempts, each counted in the journal first); the journal is removed and the leftovers cleaned. Any failure after the journal exists is a rollback by REAL hashes; a rollback that cannot be proven
/// is a <see cref="TxOutcome.RecoveryFailed"/> that keeps the journal and the backup.
/// </summary>
public static class SwapTransaction
{
    public static TxResult Run(GateEntry gate, SwapRequest request, TxSeams seams)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(seams);
        return new TxEngine(gate, seams, "swap").Run(request.StagedFolder);
    }
}

/// <summary>
/// The recovery (DESIGN-B 5, 13): the one piece of code that the runner runs at its start, that <c>recover.exe --recover</c> runs for a person, and that the start gate window starts. It looks at what is open
/// (a journal, a sentinel, leftovers) and decides by the REAL hashes of the files, never by a counter: an interrupted swap goes back to the old version; a version that was waiting for its launch check is
/// verified again (or put back); a transaction that already passed is finished. What it cannot prove it does not guess: the journal and the backup stay, the sentinel <c>recovery-failed</c> is written, and the
/// result is <see cref="TxOutcome.RecoveryFailed"/>. <paramref name="automatic"/> is true for a start-up recovery (it gives up after three attempts counted in the journal); recover.exe always tries.
/// </summary>
public static class TxRecovery
{
    public static TxResult Recover(GateEntry gate, TxSeams seams, bool automatic)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(seams);
        return new TxEngine(gate, seams, "recovery").Recover(automatic);
    }
}

/// <summary>A failure with a reason (an abort before the journal, a failed recovery).</summary>
internal sealed class TxFailure(TxReason reason, string detail) : Exception(detail)
{
    public TxReason Reason { get; } = reason;
}

/// <summary>A file was not what the record says at the moment it was about to be changed (someone else changed it).</summary>
internal sealed class TxChangedException(string detail) : IOException(detail);

internal sealed partial class TxEngine
{
    private readonly GateEntry _gate;
    private readonly TxPaths _paths;
    private readonly TxSeams _seams;
    private readonly TxIo _io;
    private readonly string _kind;

    private string? _tx;
    private InstalledRecord? _fromRecord;
    private InstalledRecord? _toRecord;
    private string? _workFolder;
    private string? _prevFolder;
    private bool _mayWriteResult;
    private bool _attemptCounted;
    private FileIdentity? _gateIdentity;

    public TxEngine(GateEntry gate, TxSeams seams, string kind)
    {
        _gate = gate;
        _seams = seams;
        _kind = kind;
        _io = new TxIo(seams.Fault, seams.Durable);
        _paths = new TxPaths(Path.GetDirectoryName(gate.GatePath)!);
    }

    // ---------------------------------------------------------------------------------------------------------------- shared helpers

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The entries of update/ a cleaning found and left alone because they are not transaction folders (relative to update/); named in the detail of the result.</summary>
    private IReadOnlyList<string> _leftAlone = [];

    private string WithLeftAlone(string detail)
    {
        if (_leftAlone.Count == 0)
        {
            return detail;
        }

        var shown = string.Join(", ", _leftAlone.Take(5).Select(BundleNames.Show));
        return $"{detail}; left alone, not a transaction folder: {shown}{(_leftAlone.Count > 5 ? $" and {_leftAlone.Count - 5} more" : string.Empty)}";
    }

    private TxResult Make(TxOutcome outcome, TxReason reason, string detail, string? tag = null, bool cleanupPending = false) =>
        new(outcome, reason, WithLeftAlone(detail))
        {
            Tx = _tx,
            FromVersion = _fromRecord?.Version,
            FromBuildId = _fromRecord?.BuildId,
            ToVersion = _toRecord?.Version,
            ToBuildId = _toRecord?.BuildId,
            Tag = tag,
            CleanupPending = cleanupPending,
        };

    private void Bind(TxJournal journal)
    {
        _tx = journal.Tx;
        _fromRecord = journal.From;
        _toRecord = journal.To;
    }

    private void WriteJournal(TxJournal journal, TxStep step) => _io.WriteReplace(step, _paths.Journal, journal.ToUtf8Bytes());

    /// <summary>A small file that is an ordinary file (never a link), or null when it is missing, too long, a link or cannot be read.</summary>
    internal static byte[]? ReadSmall(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The gate must be the open one this process holds, with the fixed bytes: that is what "nobody of this install runs" means.</summary>
    private void CheckGate()
    {
        if (!_gate.IsOpen)
        {
            throw new TxFailure(TxReason.GateLost, $"the gate is not held ({_gate.Outcome})");
        }

        var held = _gate.ReadHeld();
        if (held is null || !held.AsSpan().SequenceEqual(GateFile.Bytes()))
        {
            throw new TxFailure(TxReason.GateLost, "the gate file is not the fixed bytes");
        }
    }

    /// <summary>last-result.json, best effort: a failure to write it never changes the outcome.</summary>
    private void WriteLastResult(TxResult result)
    {
        if (!_mayWriteResult || TxPaths.IsLink(_paths.Update) || result.Outcome == TxOutcome.NothingToDo)
        {
            return;
        }

        try
        {
            _io.WriteReplace(TxSteps.LastResult, _paths.LastResult, LastResult.Bytes(_seams.UtcNow(), _kind, result));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the settings page has nothing to show; the transaction's own result stands
        }
    }

    /// <summary>The failure tag of a rollback, best effort (it only keeps the next check from offering the same build again).</summary>
    private void WriteBlockedTag(TxJournal journal)
    {
        if (journal.Tag is null)
        {
            return;
        }

        try
        {
            var tag = new BlockedTag(journal.To.Version, journal.To.BuildId, journal.Tag, _seams.UtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            _io.WriteReplace(TxSteps.BlockedTag, _paths.Blocked, BlockedTags.Bytes(BlockedTags.Read(_paths.Blocked), tag));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // see above
        }
    }

    /// <summary>
    /// Removes what a transaction leaves (its backup, its work folder, the note), each on its own: a failure of one never stops the others and never undoes the result. Returns true when something is left.
    /// </summary>
    private bool Cleanup(string tx, string? fromVersion)
    {
        var left = false;
        if (fromVersion is not null)
        {
            left |= !Attempt(() => _io.DeleteTree(TxSteps.CleanupPrev, _paths.Prev(fromVersion, tx)));
        }

        left |= !Attempt(() => _io.DeleteTree(TxSteps.CleanupWork, _paths.Work(tx)));
        left |= !Attempt(() => DeleteNote());
        return left;
    }

    private void DeleteNote()
    {
        if (ManagedSet.Exists(_paths.Note))
        {
            _io.DeleteFile(TxSteps.CleanupNote, _paths.Note);
        }
    }

    private static bool Attempt(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- Run: prepare

    public TxResult Run(string stagedFolder)
    {
        if (_seams.Launcher is null)
        {
            throw new ArgumentException("a swap needs a launcher in the seams", nameof(stagedFolder));
        }

        _tx = TxPaths.NewTx();
        TxJournal journal;
        try
        {
            journal = Prepare(stagedFolder);
        }
        catch (TxFailure failure)
        {
            return Abort(failure.Reason, failure.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Abort(TxReason.IoFailure, ex.Message);
        }

        return Swap(journal);
    }

    private TxResult Abort(TxReason reason, string detail)
    {
        // Whatever the preparation made is removed again (best effort: the next run or recovery removes leftovers too).
        if (_workFolder is not null)
        {
            Attempt(() => _io.DeleteTree(TxSteps.CleanupWork, _workFolder));
        }

        if (_prevFolder is not null)
        {
            Attempt(() => _io.DeleteTree(TxSteps.CleanupPrev, _prevFolder));
        }

        if (_workFolder is not null || _prevFolder is not null)
        {
            Attempt(DeleteNote);
        }

        var result = Make(TxOutcome.Aborted, reason, detail);
        WriteLastResult(result);
        return result;
    }

    /// <summary>Everything up to and including the committed journal (phase Prepared). Throws <see cref="TxFailure"/> to abort; no file of the install is changed by any of it.</summary>
    private TxJournal Prepare(string stagedFolder)
    {
        var tx = _tx!;
        CheckGate();

        // NTFS or ReFS only, and asked before anything is written (not even last-result.json): the swap stands on the order of their metadata log (see TxNative).
        if (!SystemVolumeFormat.IsSupported(_seams.Volume.FormatOf(_paths.Install)))
        {
            throw new TxFailure(TxReason.NotNtfs, "the volume of the install folder is not NTFS or ReFS");
        }

        if (TxPaths.IsLink(_paths.Update))
        {
            throw new TxFailure(TxReason.UpdateFolderUnsafe, "the update folder is a link");
        }

        if (ManagedSet.Exists(_paths.Journal) || ManagedSet.Exists(_paths.JournalTemp) || ManagedSet.Exists(_paths.Sentinel))
        {
            // journal.json.tmp too: it is what is left of a journal write that was cut, and only a recovery decides what that means.
            throw new TxFailure(TxReason.JournalPresent, "journal.json, journal.json.tmp or the recovery-failed sentinel is present");
        }

        _mayWriteResult = true;

        // The gate file keeps its identity (volume and file index) through the whole swap: it is never renamed, replaced or deleted. Asked now, asked again after the swap.
        _gateIdentity = FileIdentity.Of(_paths.Gate) ?? throw new TxFailure(TxReason.GateLost, "the gate file cannot be identified");

        // The new generation, from the staged folder's own manifest.
        var stagedManifest = ReadSmall(Path.Combine(stagedFolder, BundleFormat.InnerManifestName), BundleManifest.MaxBytes)
            ?? throw new TxFailure(TxReason.StagedUnreadable, $"{BundleFormat.InnerManifestName} of the staged folder cannot be read");
        if (!BundleManifest.TryParse(stagedManifest, out var stagedBundle, out var stagedVerdict))
        {
            throw new TxFailure(TxReason.StagedUnreadable, $"{BundleFormat.InnerManifestName} of the staged folder is refused: {stagedVerdict.Fault}");
        }

        var to = InstalledRecord.FromBundle(stagedBundle, stagedManifest);
        _toRecord = to;

        // The current generation: installed.json, or (a manually unzipped folder) the folder's own manifest, trusted only after every file was checked against it.
        InstalledRecord current;
        byte[] currentBytes;
        var firstRecord = false;
        if (ManagedSet.Exists(_paths.Installed))
        {
            currentBytes = ReadSmall(_paths.Installed, TxJson.MaxBytes)
                ?? throw new TxFailure(TxReason.InstalledRecordUnreadable, "installed.json cannot be read");
            if (!InstalledRecord.TryParse(currentBytes, out current!, out var recordError))
            {
                throw new TxFailure(TxReason.InstalledRecordUnreadable, "installed.json is refused: " + recordError);
            }
        }
        else
        {
            var ownManifest = ReadSmall(_paths.InInstall(BundleFormat.InnerManifestName), BundleManifest.MaxBytes)
                ?? throw new TxFailure(TxReason.NoRecord, $"there is no installed.json and no readable {BundleFormat.InnerManifestName} to make one from");
            if (!BundleManifest.TryParse(ownManifest, out var ownBundle, out var ownVerdict))
            {
                throw new TxFailure(TxReason.NoRecord, $"{BundleFormat.InnerManifestName} of the install is refused: {ownVerdict.Fault}");
            }

            current = InstalledRecord.FromBundle(ownBundle, ownManifest);
            _fromRecord = current;
            var differences = ManagedSet.Verify(_paths, current, _gate);
            if (differences.Count > 0)
            {
                throw new TxFailure(TxReason.InstalledFilesChanged, "the files are not what the manifest says: " + ManagedSet.Describe(differences));
            }

            currentBytes = current.ToUtf8Bytes();
            _io.WriteReplace(TxSteps.FirstRecord, _paths.Installed, currentBytes);
            firstRecord = true;
        }

        _fromRecord = current;
        var currentSha = Hex(currentBytes);

        if (string.Equals(current.BuildId, to.BuildId, StringComparison.Ordinal))
        {
            throw new TxFailure(TxReason.SameBuild, "the staged build is the installed build");
        }

        if (!current.TryGet(GateFile.FileName, out var oldGate) || !to.TryGet(GateFile.FileName, out var newGate)
            || oldGate.Bytes != newGate.Bytes || !string.Equals(oldGate.Sha256, newGate.Sha256, StringComparison.Ordinal))
        {
            throw new TxFailure(TxReason.StagedMismatch, "the two generations do not have the same gate file");
        }

        if (!firstRecord)
        {
            var differences = ManagedSet.Verify(_paths, current, _gate);
            if (differences.Count > 0)
            {
                throw new TxFailure(TxReason.InstalledFilesChanged, "the installed files are not what installed.json says: " + ManagedSet.Describe(differences));
            }
        }

        var ops = TxJournal.Diff(current, to, out var problem) ?? throw new TxFailure(TxReason.StagedMismatch, problem);

        foreach (var add in ops.Where(o => o.Intent == TxOpIntent.Add))
        {
            if (CollidesWithUnknown(add.Path, out var why))
            {
                throw new TxFailure(TxReason.NameCollision, why);
            }
        }

        CheckSpace(current, currentBytes, to, ops);
        var newDirs = NewDirectories(ops);

        // Leftovers of earlier runs: only transaction folders (and links standing where they stand, unlinked and never entered); anything else in work/ or prev-*/ is not ours and stays.
        var stale = _paths.ScanStale();
        _leftAlone = stale.LeftAlone;
        foreach (var folder in stale.ToDelete)
        {
            // Best effort: what an earlier run left may hold a file a running program still has mapped (it was renamed aside there). That must not stop an update; the next cleaning gets it.
            var one = folder;
            Attempt(() => _io.DeleteTree(TxSteps.StaleClean, one));
        }

        // New files, on the same volume, next to the install.
        _workFolder = _paths.Work(tx);
        var next = _paths.Next(tx);
        foreach (var op in ops.Where(o => o.Intent != TxOpIntent.Remove))
        {
            to.TryGet(op.Path, out var file);
            var source = TxPaths.Under(stagedFolder, op.Path);
            if (ManagedSet.Look(source) != Presence.File)
            {
                throw new TxFailure(TxReason.StagedMismatch, $"the staged file '{BundleNames.Show(op.Path)}' is missing or not a file");
            }

            try
            {
                _io.CopyNew(TxSteps.NextCopy, source, TxPaths.Under(next, op.Path), file!.Bytes, file.Sha256);
            }
            catch (TxSourceMismatchException ex)
            {
                throw new TxFailure(TxReason.StagedMismatch, ex.Message);
            }
        }

        // A full copy of the old set (not the gate file: it is never swapped) and of its record.
        _prevFolder = _paths.Prev(current.Version, tx);
        var backup = _paths.PrevFiles(current.Version, tx);
        try
        {
            foreach (var file in current.Files.Where(f => !GateFile.IsExcludedFromSwap(f.Path)))
            {
                _io.CopyNew(TxSteps.BackupCopy, _paths.InInstall(file.Path), TxPaths.Under(backup, file.Path), file.Bytes, file.Sha256);
            }

            _io.CopyNew(TxSteps.BackupRecord, _paths.Installed, _paths.PrevInstalled(current.Version, tx), currentBytes.Length, currentSha);
        }
        catch (TxSourceMismatchException ex)
        {
            throw new TxFailure(TxReason.InstalledFilesChanged, ex.Message);
        }

        var incomplete = VerifyBackup(current, currentBytes.Length, currentSha);
        if (incomplete.Count > 0)
        {
            throw new TxFailure(TxReason.BackupIncomplete, "the backup does not read back whole: " + string.Join(", ", incomplete.Take(3)));
        }

        // recover.exe and RECOVER.txt, before the first change.
        try
        {
            _seams.Placement.Place(_io, new PlacementContext(_paths, current, to));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TxFailure(TxReason.RunnerUnavailable, ex.Message);
        }

        var journal = new TxJournal(tx, current, currentSha, to, to.Sha256, ops, newDirs);
        WriteJournal(journal, TxSteps.JournalPrepared);
        return journal;
    }

    /// <summary>The folders the added files need that are not there yet (names relative to I, with "/"): what a rollback may remove again, and nothing else.</summary>
    private List<string> NewDirectories(List<TxOp> ops)
    {
        var made = new List<string>();
        foreach (var add in ops.Where(o => o.Intent == TxOpIntent.Add))
        {
            var parts = add.Path.Split('/');
            var path = _paths.Install;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                path = Path.Combine(path, parts[i]);
                if (ManagedSet.Look(path) == Presence.Absent)
                {
                    var name = string.Join('/', parts.Take(i + 1));
                    if (!made.Contains(name, StringComparer.Ordinal))
                    {
                        made.Add(name);
                    }
                }
            }
        }

        return made;
    }

    /// <summary>Whether a new name is taken by something the record does not know: the name itself, or a file or a link in place of one of its folders.</summary>
    private bool CollidesWithUnknown(string relativeName, out string why)
    {
        why = string.Empty;
        var parts = relativeName.Split('/');
        var path = _paths.Install;
        for (var i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            var presence = ManagedSet.Look(path);
            if (presence == Presence.Absent)
            {
                return false; // nothing below it can exist either
            }

            var last = i == parts.Length - 1;
            if (last || presence != Presence.Other)
            {
                why = $"'{BundleNames.Show(relativeName)}' is already taken by a {(presence == Presence.Other ? "folder" : presence == Presence.Link ? "link" : "file")} the record does not know";
                return true;
            }
        }

        return false;
    }

    private void CheckSpace(InstalledRecord current, byte[] currentBytes, InstalledRecord to, List<TxOp> ops)
    {
        var need = TxLimits.SpaceMarginBytes + currentBytes.Length;
        foreach (var op in ops.Where(o => o.Intent != TxOpIntent.Remove))
        {
            to.TryGet(op.Path, out var file);
            need += file!.Bytes;
        }

        need += current.Files.Where(f => !GateFile.IsExcludedFromSwap(f.Path)).Sum(f => f.Bytes);
        var free = _seams.Disk.FreeBytes(_paths.Install);
        if (free < need)
        {
            throw new TxFailure(TxReason.DiskFull, $"{need} bytes are needed and {free} are free");
        }
    }

    /// <summary>The backup read back in full: every file with its recorded length and hash, and the old installed.json. The names of what is wrong (empty when whole).</summary>
    private List<string> VerifyBackup(InstalledRecord current, int recordLength, string recordSha)
    {
        var wrong = new List<string>();
        var backup = _paths.PrevFiles(current.Version, _tx!);
        foreach (var file in current.Files.Where(f => !GateFile.IsExcludedFromSwap(f.Path)))
        {
            var presence = ManagedSet.Probe(TxPaths.Under(backup, file.Path), out var length, out var sha);
            if (presence != Presence.File || length != file.Bytes || !string.Equals(sha, file.Sha256, StringComparison.Ordinal))
            {
                wrong.Add(file.Path);
            }
        }

        var record = ManagedSet.Probe(_paths.PrevInstalled(current.Version, _tx!), out var recordLen, out var recordHash);
        if (record != Presence.File || recordLen != recordLength || !string.Equals(recordHash, recordSha, StringComparison.Ordinal))
        {
            wrong.Add("installed.json");
        }

        return wrong;
    }
}
