namespace PcMieru.Checks;

/// <summary>
/// Lists, reads and deletes runs (DESIGN-B §7.1). The new <c>pcm-run/1</c> runs are in <c>root</c>; the old <c>partslife-run/1</c> runs in
/// <c>legacyRoot</c> (Documents\PartsLife\runs) are read only, whatever is asked. Listing never reads a CSV and never writes: a run that says
/// <c>recording</c> but whose lock nobody holds is SHOWN as interrupted (the file is rewritten as that by the next recording's start, not here).
/// </summary>
public sealed class RunStore : IRunStore
{
    private readonly IRunFileSystem _fs;
    private readonly string _root;
    private readonly string? _legacyRoot;

    /// <summary>
    /// <paramref name="root"/> defaults to <c>Documents\PCMieru\runs</c>. The old runs are read from <c>Documents\PartsLife\runs</c> only when BOTH
    /// arguments are left out; with a root given and no <paramref name="legacyRoot"/> there are no old runs (a test or the trial never reaches the real folders).
    /// </summary>
    public RunStore(string? root = null, string? legacyRoot = null)
        : this(
            PhysicalRunFileSystem.Instance,
            string.IsNullOrWhiteSpace(root) ? RunFiles.DefaultRoot() : root,
            string.IsNullOrWhiteSpace(legacyRoot) ? (root is null ? RunFiles.DefaultLegacyRoot() : null) : legacyRoot)
    {
    }

    internal RunStore(IRunFileSystem fileSystem, string root, string? legacyRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _fs = fileSystem;
        _root = root;
        _legacyRoot = string.IsNullOrWhiteSpace(legacyRoot) ? null : legacyRoot;
    }

    public Task<RunList> ListAsync(CancellationToken ct) => Task.Run(() => List(ct), CancellationToken.None);

    public Task<RunDetail?> ReadAsync(Guid runId, CancellationToken ct) => Task.Run(() => Read(runId, ct), CancellationToken.None);

    public Task<RunDeleteResult> DeleteAsync(Guid runId, CancellationToken ct) => Task.Run(() => Delete(runId, ct), CancellationToken.None);

    private RunList List(CancellationToken ct)
    {
        var runs = new List<RunInfo>();
        var problems = new List<RunListProblem>();
        ListFolder(_root, legacy: false, runs, problems, ct);
        if (_legacyRoot is not null)
        {
            ListFolder(_legacyRoot, legacy: true, runs, problems, ct);
        }

        runs.Sort(static (a, b) =>
        {
            var byTime = b.StartedAtUtc.CompareTo(a.StartedAtUtc);
            return byTime != 0 ? byTime : a.RunId.CompareTo(b.RunId);
        });
        return new RunList(runs, problems);
    }

    private void ListFolder(string folder, bool legacy, List<RunInfo> runs, List<RunListProblem> problems, CancellationToken ct)
    {
        IReadOnlyList<string> names;
        try
        {
            names = _fs.ListFiles(folder, "run-*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new RunListProblem(folder, "unreadable"));
            return;
        }

        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, name);
            try
            {
                if (legacy)
                {
                    if (!LegacyRun.TryIdOfJson(name, out var legacyId))
                    {
                        problems.Add(new RunListProblem(name, "not_a_run"));
                        continue;
                    }

                    var csv = Path.Combine(folder, LegacyRun.CsvName(legacyId));
                    runs.Add(LegacyRun.ParseHeader(_fs.ReadAllBytes(path, RunJson.MaxBytes), path, csv).Info);
                }
                else
                {
                    if (!RunFiles.TryIdOfJson(name, out var id))
                    {
                        problems.Add(new RunListProblem(name, "not_a_run"));
                        continue;
                    }

                    var header = RunJson.Read(_fs.ReadAllBytes(path, RunJson.MaxBytes));
                    if (header.RunId != id)
                    {
                        problems.Add(new RunListProblem(name, "bad_format"));
                        continue;
                    }

                    runs.Add(ToInfo(header, path, Path.Combine(folder, RunFiles.CsvName(id)), Path.Combine(folder, RunFiles.LockName(id))));
                }
            }
            catch (FormatException)
            {
                problems.Add(new RunListProblem(name, "bad_format"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add(new RunListProblem(name, "unreadable"));
            }
        }
    }

    /// <summary>The header as the listing shows it: a run that says <c>recording</c> without a holder of its lock was cut off.</summary>
    private RunInfo ToInfo(RunHeader header, string jsonPath, string csvPath, string lockPath)
    {
        var status = header.Status;
        var stop = header.StopReason;
        var ended = header.EndedAtUtc;
        if (status == RunStatus.Recording && !_fs.IsLockHeld(lockPath))
        {
            status = RunStatus.Interrupted;
            stop = RunStopReason.Unknown;
            ended = null;
        }

        return new RunInfo(
            header.RunId,
            RunCodes.Format,
            header.App,
            IsLegacy: false,
            LegacyId: null,
            header.StartedAtUtc,
            ended,
            header.DurationUs,
            status,
            stop,
            header.DroppedTicks,
            header.Rows,
            header.Metrics,
            header.Sources,
            header.Markers,
            jsonPath,
            csvPath);
    }

    private RunDetail? Read(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var jsonPath = Path.Combine(_root, RunFiles.JsonName(runId));
        if (_fs.Exists(jsonPath))
        {
            return ReadNew(runId, jsonPath, ct);
        }

        if (FindLegacy(runId) is { } legacy)
        {
            return ReadLegacy(legacy, ct);
        }

        return null;
    }

    private RunDetail ReadNew(Guid runId, string jsonPath, CancellationToken ct)
    {
        var header = RunJson.Read(_fs.ReadAllBytes(jsonPath, RunJson.MaxBytes));
        if (header.RunId != runId)
        {
            throw new FormatException("The file's runId is not the one in its name.");
        }

        var csvPath = Path.Combine(_root, RunFiles.CsvName(runId));
        var info = ToInfo(header, jsonPath, csvPath, Path.Combine(_root, RunFiles.LockName(runId)));
        ct.ThrowIfCancellationRequested();

        // The CSV's path is built from the convention, never taken from the JSON's own "csv" field. A half-deleted run (the CSV goes first) is a JSON with no data.
        var data = _fs.Exists(csvPath) ? RunCsvReader.Read(_fs.ReadAllBytes(csvPath, RunFiles.MaxCsvBytes), info.Metrics) : new RunCsvData();
        var normal = info.Status == RunStatus.Completed;
        var series = new RunSeries
        {
            Markers = info.Markers,
            NormalEnd = normal,
            EndUs = normal ? Math.Max(info.DurationUs, data.LastOffsetUs) : data.LastOffsetUs,
            Rows = data.Rows,
            DroppedTicks = info.DroppedTicks,
            Status = info.Status,
            StopReason = info.StopReason,
        };
        series.Tracks.AddRange(data.Tracks);
        return new RunDetail(info with { Rows = data.Rows }, RunSummarizer.Summarize(series));
    }

    private RunDetail ReadLegacy(LegacyHeader header, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = header.Info;
        var csv = _fs.Exists(info.CsvPath)
            ? _fs.ReadAllBytes(info.CsvPath, RunFiles.MaxCsvBytes)
            : throw new FileNotFoundException("The old run's CSV is missing.", info.CsvPath);
        var data = LegacyRun.ReadSeries(header, csv);
        var summary = RunSummarizer.Summarize(data.Series);
        return new RunDetail(info with { Rows = data.Rows, Metrics = data.Metrics }, summary);
    }

    private LegacyHeader? FindLegacy(Guid runId)
    {
        if (_legacyRoot is null)
        {
            return null;
        }

        IReadOnlyList<string> names;
        try
        {
            names = _fs.ListFiles(_legacyRoot, "run-*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!LegacyRun.TryIdOfJson(name, out var legacyId) || LegacyRun.IdOf(legacyId) != runId)
            {
                continue;
            }

            var path = Path.Combine(_legacyRoot, name);
            return LegacyRun.ParseHeader(_fs.ReadAllBytes(path, RunJson.MaxBytes), path, Path.Combine(_legacyRoot, LegacyRun.CsvName(legacyId)));
        }

        return null;
    }

    private RunDeleteResult Delete(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var jsonPath = Path.Combine(_root, RunFiles.JsonName(runId));
        var csvPath = Path.Combine(_root, RunFiles.CsvName(runId));
        var tempPath = Path.Combine(_root, RunFiles.TempName(runId));
        if (!_fs.Exists(jsonPath) && !_fs.Exists(csvPath))
        {
            return FindLegacy(runId) is not null ? new RunDeleteResult(RunDeleteOutcome.ReadOnlyLegacy) : new RunDeleteResult(RunDeleteOutcome.NotFound);
        }

        try
        {
            // The lock is the answer to "is it being recorded?": a recorder holds it for the whole run. Taking it here also keeps one from starting on this run.
            using var held = _fs.TryLock(Path.Combine(_root, RunFiles.LockName(runId)));
            if (held is null)
            {
                return new RunDeleteResult(RunDeleteOutcome.Recording);
            }

            // The data first, then the header: a delete that stops half way leaves a JSON without data (listed, empty), never data nobody lists.
            _fs.Delete(csvPath);
            _fs.Delete(tempPath);
            _fs.Delete(jsonPath);
            return new RunDeleteResult(RunDeleteOutcome.Deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RunDeleteResult(RunDeleteOutcome.Failed, ex.Message);
        }
    }
}
