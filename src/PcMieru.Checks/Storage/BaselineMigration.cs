namespace PcMieru.Checks.Storage;

/// <summary>Where the read-only import of the v0.7.4 folder stands (DESIGN-A §5.3).</summary>
public enum BaselineMigrationStatus
{
    /// <summary>Nothing to do: no old folder was given, or the completion mark is already there.</summary>
    NotNeeded,

    /// <summary>The old folder is missing or has nothing in it that belongs to the baseline. No mark is written (it may appear later).</summary>
    NothingToImport,

    /// <summary>Everything that could be imported is in the new folder and the completion mark was written.</summary>
    Complete,

    /// <summary>
    /// Some histories could not be copied right now (the old tool is writing, a sharing violation). The disks they belong to are not
    /// compared in this run (<c>baseline_read_failed</c>); the next run picks up where this one stopped.
    /// </summary>
    Incomplete,

    /// <summary>
    /// Something that may pass could not be done just now: the old folder could not be listed, the old set of twins could not be read
    /// (a sharing violation, a missing permission), the new side's own set of twins or mark could not be read, or nothing could be
    /// written. No mark is written and no disk is compared in this run (<c>baseline_read_failed</c>); the next run tries again.
    /// </summary>
    Blocked,

    /// <summary>
    /// The old set of twins is there and read, but its content cannot be trusted (0 bytes, not JSON, not a list of keys, too long). Which
    /// disks were twins is unknown, so none of the old histories is imported or used (a disk that was one of two is never compared with
    /// its twin's record), and the mark was written with "status": "abandoned". The comparison goes on in this very run, from the
    /// baselines of this version alone. Reported by the run that abandoned the import only.
    /// </summary>
    Abandoned,
}

/// <summary>What one run of the import did. Counts only: it never carries a key, a serial or a file name.</summary>
public sealed class BaselineMigrationReport
{
    internal static BaselineMigrationReport None(BaselineMigrationStatus status) => new(status, [], 0, 0, 0, 0);

    internal BaselineMigrationReport(BaselineMigrationStatus status, HashSet<string> pendingHashes, int histories, int alreadyThere, int skipped, int twins)
    {
        Status = status;
        PendingHashes = pendingHashes;
        Histories = histories;
        AlreadyThere = alreadyThere;
        Skipped = skipped;
        Twins = twins;
    }

    public BaselineMigrationStatus Status { get; }

    /// <summary>Histories copied in this run.</summary>
    public int Histories { get; }

    /// <summary>Histories that already had a file in the new folder (the existing one is kept).</summary>
    public int AlreadyThere { get; }

    /// <summary>Old files that were not usable (damaged, a key that does not match its file name, a renamed copy of a history the old folder has under its proper name) and were left alone.</summary>
    public int Skipped { get; }

    /// <summary>The number of keys in the old set of twins.</summary>
    public int Twins { get; }

    /// <summary>The file-name hashes of the histories that could not be copied in this run.</summary>
    internal HashSet<string> PendingHashes { get; }

    /// <summary>No disk may be compared in this run.</summary>
    public bool StopsComparison => Status == BaselineMigrationStatus.Blocked;
}

/// <summary>
/// The import of the v0.7.4 baseline folder (DESIGN-A §5.3). The old side is opened through <see cref="ICheckHistoryFileReader"/>, which
/// has no way to write, rename or delete: nothing on the old side is ever touched. The new side only ever gets files that were not there
/// (existing ones win), in an order a stop can be resumed from: the twins first, the histories next, the completion mark last.
/// </summary>
internal static class BaselineMigration
{
    private const string OldTwinsFile = BaselineStore.TwinsFile;

    public static BaselineMigrationReport Run(BaselineStore store, ICheckHistoryFileReader legacy, string legacyDirectory, DateTimeOffset now)
    {
        switch (store.ReadMarker())
        {
            case BaselineMarker.Complete:
            case BaselineMarker.Abandoned:
                return BaselineMigrationReport.None(BaselineMigrationStatus.NotNeeded); // settled, one way or the other
            case BaselineMarker.Unreadable:
                return BaselineMigrationReport.None(BaselineMigrationStatus.Blocked); // it may say "abandoned": do not import over it
        }

        IReadOnlyList<HistoryFileEntry> listing;
        try
        {
            listing = legacy.ListFiles(legacyDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BaselineMigrationReport.None(BaselineMigrationStatus.Blocked);
        }

        var names = listing.Select(static f => f.Name).ToHashSet(StringComparer.Ordinal);
        var histories = names.Where(static n => BaselineNames.TryParseFileName(n, out _)).Order(StringComparer.Ordinal).ToList();
        var hasTwins = names.Contains(OldTwinsFile);
        if (histories.Count == 0 && !hasTwins)
        {
            return BaselineMigrationReport.None(BaselineMigrationStatus.NothingToImport);
        }

        // 1. The twins first: a history is only worth importing if the set of disks that must never be compared is safe.
        var twinCount = 0;
        if (hasTwins)
        {
            switch (ImportTwins(store, legacy, Path.Combine(legacyDirectory, OldTwinsFile), out twinCount))
            {
                case TwinsStep.Stop:
                    return new BaselineMigrationReport(BaselineMigrationStatus.Blocked, [], 0, 0, 0, twinCount);
                case TwinsStep.Corrupt:
                    return Abandon(store, now);
            }
        }

        // 2. The histories, each only if it is whole and is what its file name says, and only if the new side has none yet.
        var pending = new HashSet<string>(StringComparer.Ordinal);
        int copied = 0, alreadyThere = 0, skipped = 0;
        foreach (var name in histories)
        {
            BaselineNames.TryParseFileName(name, out var hash);
            switch (ImportHistory(store, legacy, legacyDirectory, names, name, hash))
            {
                case HistoryStep.Copied:
                    copied++;
                    break;
                case HistoryStep.AlreadyThere:
                    alreadyThere++;
                    break;
                case HistoryStep.Skipped:
                    skipped++;
                    break;
                case HistoryStep.Pending:
                    pending.Add(hash);
                    break;
            }
        }

        if (pending.Count > 0)
        {
            return new BaselineMigrationReport(BaselineMigrationStatus.Incomplete, pending, copied, alreadyThere, skipped, twinCount);
        }

        // 3. The mark. It holds counts only. It is replaced if a damaged one is in the way (it is our own file and has no content to lose).
        try
        {
            store.WriteReplace(BaselineStore.MarkerFile, BaselineJson.WriteMarker(now, copied, alreadyThere, skipped, twinCount));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BaselineMigrationReport(BaselineMigrationStatus.Incomplete, [], copied, alreadyThere, skipped, twinCount);
        }

        return new BaselineMigrationReport(BaselineMigrationStatus.Complete, [], copied, alreadyThere, skipped, twinCount);
    }

    /// <summary>
    /// The old set of twins cannot be trusted (<see cref="TwinsStep.Corrupt"/>): nothing of the old folder is imported or used. The mark
    /// says so ("status": "abandoned", no keys, no contents). The old folder is not touched. A mark that cannot be written is a stop
    /// like any other write that failed: the next run reads the old set again.
    /// </summary>
    private static BaselineMigrationReport Abandon(BaselineStore store, DateTimeOffset now)
    {
        try
        {
            store.WriteReplace(BaselineStore.MarkerFile, BaselineJson.WriteMarker(now, 0, 0, 0, 0, abandoned: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BaselineMigrationReport.None(BaselineMigrationStatus.Blocked);
        }

        return BaselineMigrationReport.None(BaselineMigrationStatus.Abandoned);
    }

    private enum TwinsStep
    {
        Done,

        /// <summary>It could not be read or stored now (a sharing violation, a permission, a listing or a write that failed): no mark, no comparison, next run.</summary>
        Stop,

        /// <summary>The old set was read and is not a set of twins: 0 bytes, not JSON, the wrong shape, too long. Retrying reads the same thing.</summary>
        Corrupt,
    }

    /// <summary>
    /// The old twins go to <c>imported-twins.json</c> without overwriting. A valid file that is already there is reused. An old set that
    /// cannot be read now, a damaged or unreadable set of the new side, or a failed write stops the import (and the comparison) with no
    /// mark. An old set that was read but is not a set of twins is <see cref="TwinsStep.Corrupt"/>: the import is abandoned.
    /// </summary>
    private static TwinsStep ImportTwins(BaselineStore store, ICheckHistoryFileReader legacy, string oldPath, out int twinCount)
    {
        twinCount = 0;
        byte[] bytes;
        try
        {
            bytes = legacy.ReadAllBytes(oldPath, BaselineStore.MaxFileBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TwinsStep.Done; // it vanished between the listing and the read: there is no old set
        }
        catch (InvalidDataException)
        {
            return TwinsStep.Corrupt; // far longer than any set of twins
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TwinsStep.Stop;
        }

        if (!BaselineJson.TryParseKeyList(bytes, out var keys))
        {
            return TwinsStep.Corrupt;
        }

        twinCount = keys!.Count;
        var existing = store.ReadKeySet(BaselineStore.ImportedTwinsFile);
        switch (existing.Status)
        {
            case BaselineReadStatus.Ok:
                return TwinsStep.Done;
            case BaselineReadStatus.Missing:
                break;
            default:
                return TwinsStep.Stop;
        }

        try
        {
            store.WriteNew(BaselineStore.ImportedTwinsFile, BaselineJson.WriteKeyList(keys));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TwinsStep.Stop;
        }

        // Someone may have written one between the check and the write (WriteNew then leaves theirs): what is there must be a good set.
        return store.ReadKeySet(BaselineStore.ImportedTwinsFile).Status == BaselineReadStatus.Ok ? TwinsStep.Done : TwinsStep.Stop;
    }

    private enum HistoryStep
    {
        Copied,
        AlreadyThere,

        /// <summary>The old file is damaged or is not the history of the key in its name: it is left alone and not copied.</summary>
        Skipped,

        /// <summary>It could not be read or written now: next run.</summary>
        Pending,
    }

    /// <summary>
    /// One old history. It is imported when it is whole, its key is a key, and the hash in its file name is the hash of that key. The
    /// file name must also be exactly the one v0.7.4 gave that key (<see cref="BaselineNames.FileName"/>): a file whose hash is right but
    /// whose prefix is not (a copy somebody renamed, an old file name rule) is imported under this version's own name for the key, still
    /// only when there is none yet and never over one, and only when the old folder has no file of the proper name for that key (that
    /// one is the real history; the renamed one is a copy of unknown age).
    /// </summary>
    private static HistoryStep ImportHistory(
        BaselineStore store,
        ICheckHistoryFileReader legacy,
        string legacyDirectory,
        HashSet<string> legacyNames,
        string oldName,
        string hash)
    {
        byte[] bytes;
        try
        {
            bytes = legacy.ReadAllBytes(Path.Combine(legacyDirectory, oldName), BaselineStore.MaxFileBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return HistoryStep.AlreadyThere; // gone since the listing (the old tool cleaned up): nothing to import, nothing to retry
        }
        catch (InvalidDataException)
        {
            return HistoryStep.Skipped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HistoryStep.Pending;
        }

        // Old records count as acq 0: they are older than every run of this version.
        if (!BaselineJson.TryParseHistory(bytes, out var history, zeroAcq: true)
            || !BaselineNames.IsValidKey(history!.Key, allowLocation: true)
            || BaselineNames.KeyHash(history.Key) != hash
            || !DiskKey.TryFromKeyText(history.Key, out var diskKey))
        {
            return HistoryStep.Skipped;
        }

        var name = BaselineNames.FileName(diskKey);
        if (name != oldName && legacyNames.Contains(name))
        {
            return HistoryStep.Skipped; // the old folder has the file of the proper name for this key: that is the history, this is a copy
        }

        if (store.Exists(name))
        {
            return HistoryStep.AlreadyThere; // the history of the new folder is the one to keep
        }

        try
        {
            return store.WriteNew(name, BaselineJson.WriteHistory(history)) ? HistoryStep.Copied : HistoryStep.AlreadyThere;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HistoryStep.Pending;
        }
    }
}
