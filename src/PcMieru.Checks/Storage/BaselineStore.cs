namespace PcMieru.Checks.Storage;

internal enum BaselineReadStatus
{
    /// <summary>There is no such file (yet).</summary>
    Missing,

    /// <summary>The file was read and has exactly the shape.</summary>
    Ok,

    /// <summary>The file is there and readable but is not what it should be (damaged, foreign, too long).</summary>
    Corrupt,

    /// <summary>The file could not be read now (a sharing violation, a missing permission): it may be fine next time.</summary>
    Unreadable,
}

/// <summary>What reading one baseline file gave. <see cref="Value"/> is set only for <see cref="BaselineReadStatus.Ok"/>.</summary>
internal readonly record struct BaselineRead<T>(BaselineReadStatus Status, T? Value)
    where T : class
{
    public static BaselineRead<T> Of(BaselineReadStatus status) => new(status, null);
}

/// <summary>What the migration's mark says (<c>imported-v074.json</c>).</summary>
internal enum BaselineMarker
{
    /// <summary>No mark, or one that is not a real mark (damaged, foreign): the migration has not been completed.</summary>
    None,

    /// <summary>The migration ran to its end: the old folder is imported.</summary>
    Complete,

    /// <summary>
    /// The old set of twins could not be trusted, so nothing of the old folder was imported or is used (DESIGN-A §5.3-2): the
    /// comparison goes on from the baselines of this version alone.
    /// </summary>
    Abandoned,

    /// <summary>The mark could not be read now: it may be there and say either.</summary>
    Unreadable,
}

/// <summary>
/// The files of <c>&lt;root&gt;\baseline\</c> (DESIGN-A §4.1, §5.2): one history per disk key, <c>twins.json</c>, <c>imported-twins.json</c>,
/// <c>imported-v074.json</c> (the migration is complete or abandoned), <c>disk-ordinals.json</c>, <c>acq-seq.json</c> and the lock file.
/// Every write goes to a temporary file first, is flushed to the disk, and is then renamed over the final name (or, for a file that
/// must not be overwritten, renamed with no replace), so a reader and a crash see a whole old file or a whole new one. Nothing here
/// takes the lock: the engine holds it around every call.
/// </summary>
internal sealed class BaselineStore
{
    public const string TwinsFile = "twins.json";
    public const string ImportedTwinsFile = "imported-twins.json";
    public const string MarkerFile = "imported-v074.json";
    public const string OrdinalsFile = "disk-ordinals.json";
    public const string SequenceFile = "acq-seq.json";

    /// <summary>A history is at most 200 entries of a few hundred bytes. Anything far beyond that is not one.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>A temporary file left by a write that never finished is removed once it is this old (the History rule, DESIGN-A §4.1). A write in flight is far younger.</summary>
    public static readonly TimeSpan TempMaxAge = TimeSpan.FromHours(1);

    private const int ReplaceRetries = 4;

    /// <summary>The same number of tries for a free "bad" name as the history's quarantine.</summary>
    private const int MaxNameAttempts = 64;

    private readonly IBaselineFileSystem _files;
    private readonly Action<TimeSpan> _sleep;
    private readonly TimeProvider _time;

    public BaselineStore(string directory, IBaselineFileSystem files, Action<TimeSpan>? sleep = null, TimeProvider? time = null)
    {
        Directory = directory;
        _files = files;
        _sleep = sleep ?? Thread.Sleep;
        _time = time ?? TimeProvider.System;
    }

    public string Directory { get; }

    public string PathOf(string name) => Path.Combine(Directory, name);

    public bool Exists(string name) => _files.Exists(PathOf(name));

    // ---- reading ----

    public BaselineRead<byte[]> ReadBytes(string name)
    {
        try
        {
            return new BaselineRead<byte[]>(BaselineReadStatus.Ok, _files.ReadAllBytes(PathOf(name), MaxFileBytes));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return BaselineRead<byte[]>.Of(BaselineReadStatus.Missing);
        }
        catch (InvalidDataException)
        {
            return BaselineRead<byte[]>.Of(BaselineReadStatus.Corrupt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BaselineRead<byte[]>.Of(BaselineReadStatus.Unreadable);
        }
    }

    /// <summary>One disk's history. A file whose key is not the expected one is corrupt: the name is a hash of the key, so that is a collision or a copy.</summary>
    public BaselineRead<BaselineHistory> ReadHistory(string fileName, string expectedKey)
    {
        var raw = ReadBytes(fileName);
        if (raw.Status != BaselineReadStatus.Ok)
        {
            return BaselineRead<BaselineHistory>.Of(raw.Status);
        }

        return BaselineJson.TryParseHistory(raw.Value!, out var history) && history!.Key == expectedKey
            ? new BaselineRead<BaselineHistory>(BaselineReadStatus.Ok, history)
            : BaselineRead<BaselineHistory>.Of(BaselineReadStatus.Corrupt);
    }

    public BaselineRead<HashSet<string>> ReadKeySet(string name)
    {
        var raw = ReadBytes(name);
        if (raw.Status != BaselineReadStatus.Ok)
        {
            return BaselineRead<HashSet<string>>.Of(raw.Status);
        }

        return BaselineJson.TryParseKeyList(raw.Value!, out var keys)
            ? new BaselineRead<HashSet<string>>(BaselineReadStatus.Ok, keys)
            : BaselineRead<HashSet<string>>.Of(BaselineReadStatus.Corrupt);
    }

    public BaselineRead<Dictionary<string, int>> ReadOrdinals()
    {
        var raw = ReadBytes(OrdinalsFile);
        if (raw.Status != BaselineReadStatus.Ok)
        {
            return BaselineRead<Dictionary<string, int>>.Of(raw.Status);
        }

        return BaselineJson.TryParseOrdinals(raw.Value!, out var map)
            ? new BaselineRead<Dictionary<string, int>>(BaselineReadStatus.Ok, map)
            : BaselineRead<Dictionary<string, int>>.Of(BaselineReadStatus.Corrupt);
    }

    /// <summary>What the mark says. A missing or damaged mark is <see cref="BaselineMarker.None"/> (the migration runs and writes a good one); one that cannot be read now is <see cref="BaselineMarker.Unreadable"/>.</summary>
    public BaselineMarker ReadMarker()
    {
        var raw = ReadBytes(MarkerFile);
        return raw.Status switch
        {
            BaselineReadStatus.Ok => BaselineJson.TryReadMarker(raw.Value!, out var abandoned)
                ? (abandoned ? BaselineMarker.Abandoned : BaselineMarker.Complete)
                : BaselineMarker.None,
            BaselineReadStatus.Unreadable => BaselineMarker.Unreadable,
            _ => BaselineMarker.None,
        };
    }

    // ---- writing ----

    /// <summary>Writes the file whole, replacing the old version. Throws the I/O exceptions; no half file is ever under the final name.</summary>
    public void WriteReplace(string name, byte[] bytes) => WriteAtomic(name, bytes, replace: true);

    /// <summary>Writes the file only when there is none yet. False when there already was one (it is left as it was); other failures throw.</summary>
    public bool WriteNew(string name, byte[] bytes)
    {
        try
        {
            WriteAtomic(name, bytes, replace: false);
            return true;
        }
        catch (IOException) when (_files.Exists(PathOf(name)))
        {
            return false;
        }
    }

    private void WriteAtomic(string name, byte[] bytes, bool replace)
    {
        _files.CreateDirectory(Directory);
        var path = PathOf(name);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..16] + ".tmp";
        var done = false;
        try
        {
            using (var file = _files.CreateNew(temp))
            {
                file.Write(bytes);
                file.FlushToDisk();
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (replace)
                    {
                        _files.Replace(temp, path);
                    }
                    else
                    {
                        _files.Move(temp, path);
                    }

                    done = true;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceRetries && (replace || !_files.Exists(path)))
                {
                    // A virus scanner or an indexer may hold the file for a moment (v0.7.4 waited the same way).
                    _sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                }
            }
        }
        finally
        {
            if (!done)
            {
                try
                {
                    _files.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An orphan .tmp is never read as data (a file name is matched in full): the clean-up after a later save takes it.
                }
            }
        }

        CleanStaleTemps();
    }

    /// <summary>
    /// Deletes the temporary files an earlier write left behind (a crash, a power cut, a failed delete), after a save that succeeded.
    /// The History rule (DESIGN-A §4.1): a name of exactly the shape of this saver's temporary file (<see cref="BaselineNames.IsOwnTemp"/>)
    /// and a last-write time at least <see cref="TempMaxAge"/> before now. The pid is never looked at (there is none in the name); a time in
    /// the future is not old. No other file is touched: not a history, not a ".bad" file, not the lock file, not a foreign ".tmp". Best
    /// effort: a file that cannot be listed or deleted just now stays for the next save.
    /// </summary>
    private void CleanStaleTemps()
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var entry in _files.ListFiles(Directory))
            {
                if (!BaselineNames.IsOwnTemp(entry.Name) || now - entry.LastWriteUtc < TempMaxAge)
                {
                    continue;
                }

                try
                {
                    _files.Delete(PathOf(entry.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held by someone or not ours to delete after all: leave it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed just now: the save itself is done and stays done.
        }
    }

    /// <summary>
    /// Moves a file that cannot be used aside as "&lt;name&gt;.bad-&lt;UTC&gt;", then "-1", "-2" ... when that name is taken (the History's
    /// rule, <see cref="CheckHistoryNames.BadName"/>), so it is kept for a look and no longer in the way. Nothing is ever replaced.
    /// True when the file is no longer under its name (moved, or already gone); false when it could not be moved: the file is then
    /// exactly as it was and the caller must not write under that name, because that would overwrite what this was meant to keep.
    /// </summary>
    public bool Quarantine(string name, DateTimeOffset now)
    {
        var source = PathOf(name);
        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var destination = PathOf(CheckHistoryNames.BadName(name, now, attempt));
            try
            {
                if (_files.Exists(destination))
                {
                    continue;
                }

                _files.Move(source, destination);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return true; // it is gone since it was read: there is nothing left to keep and nothing in the way
            }
            catch (IOException) when (_files.Exists(destination))
            {
                // Someone took the name between the check and the move: the next name.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    // ---- the acquisition sequence ----

    /// <summary>
    /// The next <c>acq</c>: the stored number plus one, written back. A missing or damaged <c>acq-seq.json</c> is rebuilt from the
    /// largest <c>acq</c> in the disk files, so losing it never leaves a disk's later records looking "newer" than every future run.
    /// False when the number could not be read or stored; the caller must then not compare or record.
    /// </summary>
    public bool TryNextSequence(out long seq)
    {
        seq = 0;
        long current;
        var raw = ReadBytes(SequenceFile);
        switch (raw.Status)
        {
            case BaselineReadStatus.Ok when BaselineJson.TryParseSequence(raw.Value!, out current):
                break;
            case BaselineReadStatus.Unreadable:
                return false;
            default:
                if (!TryRecoverSequence(out current))
                {
                    return false;
                }

                break;
        }

        if (current == long.MaxValue)
        {
            return false;
        }

        var next = current + 1;
        try
        {
            WriteReplace(SequenceFile, BaselineJson.WriteSequence(next));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        seq = next;
        return true;
    }

    private bool TryRecoverSequence(out long max)
    {
        max = 0;
        IReadOnlyList<HistoryFileEntry> files;
        try
        {
            files = _files.ListFiles(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var file in files)
        {
            if (!file.Name.StartsWith("disk-", StringComparison.Ordinal) || !file.Name.EndsWith(".json", StringComparison.Ordinal) || file.Name == OrdinalsFile)
            {
                continue;
            }

            var raw = ReadBytes(file.Name);
            if (raw.Status == BaselineReadStatus.Unreadable)
            {
                return false;
            }

            if (raw.Status == BaselineReadStatus.Ok && BaselineJson.TryParseHistory(raw.Value!, out var history))
            {
                max = Math.Max(max, history!.Entries.Count == 0 ? 0 : history.Entries.Max(static e => e.Acq));
            }
        }

        return true;
    }
}
