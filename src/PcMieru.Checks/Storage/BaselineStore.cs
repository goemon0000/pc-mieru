using System.Globalization;

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

/// <summary>
/// The files of <c>&lt;root&gt;\baseline\</c> (DESIGN-A §4.1, §5.2): one history per disk key, <c>twins.json</c>, <c>imported-twins.json</c>,
/// <c>imported-v074.json</c> (the migration is complete), <c>disk-ordinals.json</c> and <c>acq-seq.json</c>. Every write goes to a
/// temporary file first, is flushed to the disk, and is then renamed over the final name (or, for a file that must not be
/// overwritten, renamed with no replace), so a reader and a crash see a whole old file or a whole new one. Nothing here takes the
/// lock: the engine holds it around every call.
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

    private const int ReplaceRetries = 4;

    private readonly IBaselineFileSystem _files;
    private readonly Action<TimeSpan> _sleep;

    public BaselineStore(string directory, IBaselineFileSystem files, Action<TimeSpan>? sleep = null)
    {
        Directory = directory;
        _files = files;
        _sleep = sleep ?? Thread.Sleep;
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

    /// <summary>True when the migration has been completed: the mark is there and is a real mark.</summary>
    public bool IsMigrationComplete()
    {
        var raw = ReadBytes(MarkerFile);
        return raw.Status == BaselineReadStatus.Ok && BaselineJson.IsValidMarker(raw.Value!);
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
                    return;
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
                    // An orphan .tmp is never read as data (a file name is matched in full), so it can wait.
                }
            }
        }
    }

    /// <summary>Renames a file that cannot be used to "&lt;name&gt;.bad-&lt;time&gt;" so it is kept for a look and no longer in the way. Best effort: false when it could not.</summary>
    public bool Quarantine(string name, DateTimeOffset now)
    {
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        try
        {
            _files.Move(PathOf(name), PathOf(name + ".bad-" + stamp));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
