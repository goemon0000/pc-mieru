namespace PcMieru.Checks;

/// <summary>The names of a run's files and the one atomic save (DESIGN-B §7.1: <c>run-&lt;UUID&gt;.json</c>, <c>sensors-&lt;UUID&gt;.csv</c>, <c>run-&lt;UUID&gt;.lock</c>, <c>run-&lt;UUID&gt;.json.tmp</c>).</summary>
internal static class RunFiles
{
    /// <summary>A day of one-second rows is a few tens of megabytes; a file bigger than this is not read (it is shown as unreadable).</summary>
    public const int MaxCsvBytes = 256 * 1024 * 1024;

    /// <summary>Where runs are kept unless the caller says otherwise: <c>Documents\PCMieru\runs</c>. Nothing is created by asking.</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCMieru", "runs");

    /// <summary>Where Parts Life kept its runs (<c>partslife-run/1</c>, read only): <c>Documents\PartsLife\runs</c>.</summary>
    public static string DefaultLegacyRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PartsLife", "runs");

    public static string JsonName(Guid id) => "run-" + RunCodes.IdText(id) + ".json";

    public static string CsvName(Guid id) => "sensors-" + RunCodes.IdText(id) + ".csv";

    public static string LockName(Guid id) => "run-" + RunCodes.IdText(id) + ".lock";

    public static string TempName(Guid id) => "run-" + RunCodes.IdText(id) + ".json.tmp";

    /// <summary>The run id in a <c>run-&lt;UUID&gt;.json</c> name; false for anything else (the id must be the lowercase canonical form, so the name can be rebuilt from it).</summary>
    public static bool TryIdOfJson(string fileName, out Guid id)
    {
        id = Guid.Empty;
        const string prefix = "run-";
        const string suffix = ".json";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.Ordinal) || fileName.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        var middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return Guid.TryParseExact(middle, "D", out id) && RunCodes.IdText(id) == middle;
    }

    /// <summary>
    /// The waits between the tries of a save (about 50 to 100 ms, five retries): the rename of a file that was just written can meet a sharing violation
    /// or an access denied for a moment (a virus scanner or an indexer still holding it). Only these two are retried; a full disk is not.
    /// </summary>
    internal static readonly TimeSpan[] SaveRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100),
    ];

    /// <summary>A failure that may pass in a moment (sharing violation 32 or access denied 5): <see cref="SaveAtomic"/> retries it, and a periodic flush may put the save off.</summary>
    internal static bool IsTransientFailure(Exception ex) => CheckHistoryStore.IsTransientMoveFailure(ex);

    /// <summary>
    /// Writes the bytes to <paramref name="temp"/>, forces them to the disk and renames the file over <paramref name="destination"/>: a reader sees the old
    /// file or the new one, never half of one. A transient failure (<see cref="IsTransientFailure"/>) is tried again after each of <see cref="SaveRetryDelays"/>,
    /// the wait going through <paramref name="pause"/> (a test passes one that does not sleep); the last failure, and any other, goes to the caller.
    /// </summary>
    public static void SaveAtomic(IRunFileSystem fs, string temp, string destination, byte[] bytes, Action<TimeSpan>? pause = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SaveOnce(fs, temp, destination, bytes);
                return;
            }
            catch (Exception ex) when (attempt < SaveRetryDelays.Length && IsTransientFailure(ex))
            {
                (pause ?? DefaultPause)(SaveRetryDelays[attempt]);
            }
        }
    }

    private static void DefaultPause(TimeSpan delay) => Thread.Sleep(delay);

    private static void SaveOnce(IRunFileSystem fs, string temp, string destination, byte[] bytes)
    {
        try
        {
            using (var file = fs.Create(temp))
            {
                file.Write(bytes);
                file.FlushToDisk();
            }

            fs.ReplaceAtomic(temp, destination);
        }
        catch
        {
            try
            {
                fs.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The failure that got us here is the one that matters.
            }

            throw;
        }
    }
}

/// <summary>
/// A run that still says <c>recording</c> while nobody holds its lock was cut off (the process died or the machine lost power). Before the next
/// recording starts it is rewritten as <c>interrupted</c>, with the row count and the duration taken from the CSV up to its last COMPLETE line
/// (DESIGN-B §7.1). Nothing is deleted and the CSV is not touched.
/// </summary>
internal static class RunRecovery
{
    /// <summary>Recovers every such run in <paramref name="root"/> and returns how many. A file that cannot be read or written (too long, malformed, in use) is skipped (it is shown as it is): recovery never stops anything else.</summary>
    public static int Recover(IRunFileSystem fs, string root, Action<TimeSpan>? pause = null)
    {
        var recovered = 0;
        foreach (var name in fs.ListFiles(root, "run-*.json"))
        {
            if (!RunFiles.TryIdOfJson(name, out var id))
            {
                continue;
            }

            try
            {
                if (RecoverOne(fs, root, id, pause))
                {
                    recovered++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
            {
                // Not ours to fix now; the store lists it as interrupted or unreadable.
            }
        }

        // A save that was cut off leaves its .tmp behind: it holds nothing the .json does not, so it goes (only when no recorder holds the run).
        foreach (var name in fs.ListFiles(root, "run-*.json.tmp"))
        {
            var stem = name[..^".json.tmp".Length] + ".lock";
            try
            {
                using var held = fs.TryLock(Path.Combine(root, stem));
                if (held is not null)
                {
                    fs.Delete(Path.Combine(root, name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next start.
            }
        }

        return recovered;
    }

    private static bool RecoverOne(IRunFileSystem fs, string root, Guid id, Action<TimeSpan>? pause)
    {
        var jsonPath = Path.Combine(root, RunFiles.JsonName(id));
        if (RunJson.Read(fs.ReadAllBytes(jsonPath, RunJson.MaxBytes)).Status != RunStatus.Recording)
        {
            return false;
        }

        // The lock is taken BEFORE the file is trusted: a recorder that holds it is alive and the run is its.
        using var held = fs.TryLock(Path.Combine(root, RunFiles.LockName(id)));
        if (held is null)
        {
            return false;
        }

        var header = RunJson.Read(fs.ReadAllBytes(jsonPath, RunJson.MaxBytes));
        if (header.Status != RunStatus.Recording)
        {
            return false;
        }

        var rows = 0L;
        var last = 0L;
        var csvPath = Path.Combine(root, RunFiles.CsvName(id));
        if (fs.Exists(csvPath))
        {
            try
            {
                var data = RunCsvReader.Read(fs.ReadAllBytes(csvPath, RunFiles.MaxCsvBytes), header.Metrics);
                rows = data.Rows;
                last = data.LastOffsetUs;
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                // A CSV that cannot be read leaves a run with no rows; the JSON is still worth keeping readable.
            }
        }

        var duration = Math.Max(last, header.Markers.Count == 0 ? 0 : header.Markers.Max(m => m.OffsetUs));
        var fixedHeader = header with
        {
            Status = RunStatus.Interrupted,
            StopReason = RunStopReason.Unknown,
            EndedAtUtc = null,
            DurationUs = duration,
            Rows = rows,
        };
        RunFiles.SaveAtomic(fs, Path.Combine(root, RunFiles.TempName(id)), jsonPath, RunJson.Write(fixedHeader), pause);
        return true;
    }
}
