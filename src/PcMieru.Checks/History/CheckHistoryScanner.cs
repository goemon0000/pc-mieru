namespace PcMieru.Checks;

/// <summary>What reading one history file turned out to be.</summary>
internal enum HistoryFileState
{
    /// <summary>Readable, and the name agrees with the body.</summary>
    Valid,

    /// <summary>Broken JSON, a broken rule, or a name that disagrees with the body. The only state that may be moved aside.</summary>
    Unreadable,

    /// <summary>A later version wrote it (unknown format or kind). Kept as it is.</summary>
    FutureVersion,

    /// <summary>Another process holds the file (a sharing or lock violation). Kept as it is.</summary>
    Locked,

    /// <summary>The permission is missing. Kept as it is.</summary>
    AccessDenied,

    /// <summary>Any other I/O failure (or a reader fault). Kept as it is.</summary>
    ReadFailed,

    /// <summary>The file vanished between the listing and the read (another process moved or deleted it). Not a problem; not counted.</summary>
    Gone,
}

/// <summary>The fixed short reasons behind a state, for the per-file problem list (never shown as a sentence).</summary>
internal static class CheckHistoryReasons
{
    public const string InvalidContent = "invalid_content";
    public const string TooLarge = "too_large";
    public const string NameKindMismatch = "name_kind_mismatch";
    public const string NameTimeMismatch = "name_time_mismatch";
    public const string NameIdMismatch = "name_id_mismatch";
    public const string FutureFormat = "future_format";
    public const string FutureKind = "future_kind";
    public const string SharingViolation = "sharing_violation";
    public const string PermissionDenied = "permission_denied";
    public const string IoError = "io_error";
    public const string ReaderError = "reader_error";
    public const string Vanished = "vanished";
}

internal sealed record HistoryFileOutcome(HistoryFileState State, string Reason, CheckReading? Reading);

/// <summary>What a quarantine attempt came to: moved (to <see cref="MovedTo"/>), left in place, or already gone.</summary>
internal sealed record QuarantineResult(string? MovedTo, bool Gone)
{
    public static QuarantineResult Moved(string name) => new(name, false);

    public static QuarantineResult Failed { get; } = new(null, false);

    public static QuarantineResult Vanished { get; } = new(null, true);
}

/// <summary>One file that could not be listed: its name, the warning code it falls under, the reason, and what the quarantine did.</summary>
internal sealed record CheckHistoryProblem(string FileName, string Code, string Reason, string? QuarantinedAs, bool QuarantineFailed);

/// <summary>A listing and the per-file detail behind its warnings.</summary>
internal sealed record CheckHistoryScan(CheckHistoryPage Page, IReadOnlyList<CheckHistoryProblem> Problems);

/// <summary>One stored run, as read, or why it could not be: <see cref="Problem"/> is the first file of that run that failed (null when no file has that ID at all).</summary>
internal sealed record CheckHistoryDetail(CheckReading? Reading, CheckHistoryProblem? Problem)
{
    public CheckResult? Result => Reading?.Result;

    /// <summary>
    /// The public answer: found; not found (no file carries the ID); or unreadable with the code that says why. The code is the one of
    /// the listing for the same file: <c>history_quarantined</c> / <c>history_quarantine_failed</c> when the store acted on a corrupt
    /// file of the run, otherwise the cause (<c>history_unreadable</c>, <c>history_future_format</c>, <c>history_locked</c>, ...).
    /// </summary>
    public CheckHistoryGetResult ToResult()
    {
        if (Reading is not null)
        {
            return CheckHistoryGetResult.Found(Reading.Result);
        }

        if (Problem is null)
        {
            return CheckHistoryGetResult.NotFound;
        }

        var code = Problem.QuarantinedAs is not null
            ? CheckHistoryWarningCodes.Quarantined
            : Problem.QuarantineFailed ? CheckHistoryWarningCodes.QuarantineFailed : Problem.Code;
        return CheckHistoryGetResult.Unreadable(code);
    }
}

/// <summary>
/// Reads the history folder: lists the candidates, reads and cross-checks one file, and lists or fetches runs (DESIGN-A §4).
/// It only reads (<see cref="ICheckHistoryFileReader"/>); a caller that may move a corrupt file aside passes the quarantine in.
/// </summary>
internal sealed class CheckHistoryScanner
{
    /// <summary>How many valid files per kind the history keeps and lists (DESIGN-A §4).</summary>
    public const int KeepPerKind = 200;

    /// <summary>A history file is a few tens of KB; a longer one is not a result of ours (and is not read into memory).</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    private readonly ICheckHistoryFileReader _files;

    public CheckHistoryScanner(ICheckHistoryFileReader files, string root)
    {
        _files = files;
        Root = root;
    }

    public string Root { get; }

    public string PathOf(HistoryFileName file) => Path.Combine(Root, file.Name);

    /// <summary>
    /// The entries of the history folder. A folder that does not exist is an empty list (the file system says so). A folder that cannot
    /// be opened or enumerated (permission, I/O error, a path that is a file or is not valid, a fault of the reader) is the one failure
    /// that is not about a file: it is thrown as <see cref="CheckHistoryFolderException"/>, so a caller can tell it from the warnings.
    /// </summary>
    public IReadOnlyList<HistoryFileEntry> ListFiles()
    {
        try
        {
            return _files.ListFiles(Root);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or CheckHistoryFolderException))
        {
            throw new CheckHistoryFolderException(ex);
        }
    }

    /// <summary>
    /// The history files among the entries, newest end time first; equal times by UUID (ascending, ordinal order of the N-format string as it is in the file name), then by kind
    /// name so the order never depends on the file system. Only names that are exactly a history file name are candidates:
    /// .tmp files, .bad files, other files are not. <paramref name="kindName"/> limits the list to one kind's name.
    /// </summary>
    public static List<HistoryFileName> Candidates(IEnumerable<HistoryFileEntry> entries, string? kindName = null)
    {
        var list = new List<HistoryFileName>();
        foreach (var entry in entries)
        {
            if (CheckHistoryNames.TryParse(entry.Name, out var parsed) && (kindName is null || parsed.Kind == kindName))
            {
                list.Add(parsed);
            }
        }

        list.Sort(Compare);
        return list;
    }

    private static int Compare(HistoryFileName a, HistoryFileName b)
    {
        var byTime = string.CompareOrdinal(b.Stamp, a.Stamp);
        if (byTime != 0)
        {
            return byTime;
        }

        var byId = string.CompareOrdinal(a.Id, b.Id);
        return byId != 0 ? byId : string.CompareOrdinal(a.Kind, b.Kind);
    }

    /// <summary>
    /// Reads one file and decides what it is. Never throws for a file problem: every I/O or format failure is a state.
    /// Only <see cref="HistoryFileState.Unreadable"/> is the file's own fault; the rest keep the file as it is.
    /// </summary>
    public HistoryFileOutcome Classify(HistoryFileName file)
    {
        byte[] bytes;
        try
        {
            bytes = _files.ReadAllBytes(PathOf(file), MaxFileBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HistoryFileOutcome(HistoryFileState.Gone, CheckHistoryReasons.Vanished, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new HistoryFileOutcome(HistoryFileState.AccessDenied, CheckHistoryReasons.PermissionDenied, null);
        }
        catch (InvalidDataException)
        {
            return new HistoryFileOutcome(HistoryFileState.Unreadable, CheckHistoryReasons.TooLarge, null);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return new HistoryFileOutcome(HistoryFileState.Locked, CheckHistoryReasons.SharingViolation, null);
        }
        catch (IOException)
        {
            return new HistoryFileOutcome(HistoryFileState.ReadFailed, CheckHistoryReasons.IoError, null);
        }

        CheckReading reading;
        try
        {
            reading = CheckJson.Read(bytes, CheckReadMode.Tolerant);
        }
        catch (CheckFormatException ex) when (ex.IsFromFutureVersion)
        {
            return new HistoryFileOutcome(
                HistoryFileState.FutureVersion,
                ex.UnknownFormat is not null ? CheckHistoryReasons.FutureFormat : CheckHistoryReasons.FutureKind,
                null);
        }
        catch (CheckFormatException)
        {
            return new HistoryFileOutcome(HistoryFileState.Unreadable, CheckHistoryReasons.InvalidContent, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A fault in the reader is not proof that the file is bad: keep it, and say so.
            return new HistoryFileOutcome(HistoryFileState.ReadFailed, CheckHistoryReasons.ReaderError, null);
        }

        // The name must agree with the body in kind, end time and UUID (DESIGN-A §4). A disagreement is an inconsistency
        // of the file, which is the same class of fault as a broken field.
        return NameMismatch(reading.Result, file) is { } mismatch
            ? new HistoryFileOutcome(HistoryFileState.Unreadable, mismatch, null)
            : new HistoryFileOutcome(HistoryFileState.Valid, string.Empty, reading);
    }

    /// <summary>The reason (a <see cref="CheckHistoryReasons"/> value) when the body disagrees with the name in kind, end time or UUID; null when all three agree.</summary>
    public static string? NameMismatch(CheckResult result, HistoryFileName file)
    {
        if (SnakeCaseEnum<CheckKind>.Name(result.Kind) != file.Kind)
        {
            return CheckHistoryReasons.NameKindMismatch;
        }

        if (CheckTime.FileStamp(result.EndedAtUtc) != file.Stamp)
        {
            return CheckHistoryReasons.NameTimeMismatch;
        }

        return string.Equals(result.RunId.ToString("N"), file.Id, StringComparison.Ordinal) ? null : CheckHistoryReasons.NameIdMismatch;
    }

    /// <summary>The warning code a not-valid state is reported under.</summary>
    public static string WarningCode(HistoryFileState state) => state switch
    {
        HistoryFileState.Unreadable => CheckHistoryWarningCodes.Unreadable,
        HistoryFileState.FutureVersion => CheckHistoryWarningCodes.FutureFormat,
        HistoryFileState.Locked => CheckHistoryWarningCodes.Locked,
        HistoryFileState.AccessDenied => CheckHistoryWarningCodes.AccessDenied,
        HistoryFileState.ReadFailed => CheckHistoryWarningCodes.ReadFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "This state has no warning."),
    };

    /// <summary>
    /// The listing. Files are visited newest first. A kind's files beyond its newest 200 valid ones are outside the retention
    /// window and not listed (nor read). <see cref="CheckHistoryQuery.Limit"/> stops the visit: older files are not examined,
    /// so problems among them are not found yet. A file that is not valid is skipped and reported; a corrupt one is passed to
    /// <paramref name="quarantine"/> when there is one (null: the read-only history, which touches nothing).
    /// </summary>
    public CheckHistoryScan Scan(CheckHistoryQuery query, Func<HistoryFileName, QuarantineResult>? quarantine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "Limit must be 1 or more, or null for everything retained.");
        }

        var kindName = query.Kind is { } kind ? SnakeCaseEnum<CheckKind>.Name(kind) : null;
        var entries = new List<CheckHistoryEntry>();
        var problems = new List<CheckHistoryProblem>();
        var validPerKind = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in Candidates(ListFiles(), kindName))
        {
            ct.ThrowIfCancellationRequested();
            if (query.Limit is { } limit && entries.Count >= limit)
            {
                break;
            }

            if (SnakeCaseEnum<CheckKind>.TryParse(file.Kind, out _) && validPerKind.GetValueOrDefault(file.Kind) >= KeepPerKind)
            {
                continue;
            }

            var outcome = Classify(file);
            if (outcome.State == HistoryFileState.Valid)
            {
                validPerKind[file.Kind] = validPerKind.GetValueOrDefault(file.Kind) + 1;
                entries.Add(CheckHistoryEntry.From(outcome.Reading!.Result));
            }
            else if (Problem(file, outcome, quarantine) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return new CheckHistoryScan(new CheckHistoryPage(entries, Warnings(problems)), problems);
    }

    /// <summary>The run with this ID (the newest file that carries it and reads), tolerant. Files of the ID that fail are passed to the quarantine like in <see cref="Scan"/>.</summary>
    public CheckHistoryDetail Get(Guid runId, Func<HistoryFileName, QuarantineResult>? quarantine, CancellationToken ct)
    {
        var id = runId.ToString("N");
        CheckHistoryProblem? first = null;
        foreach (var file in Candidates(ListFiles()).Where(f => f.Id == id))
        {
            ct.ThrowIfCancellationRequested();
            var outcome = Classify(file);
            if (outcome.State == HistoryFileState.Valid)
            {
                return new CheckHistoryDetail(outcome.Reading, null);
            }

            first ??= Problem(file, outcome, quarantine);
        }

        return new CheckHistoryDetail(null, first);
    }

    // Null for a file that vanished (not a problem). For an unreadable one, the quarantine runs first: a file another process
    // already moved aside is not reported twice.
    private static CheckHistoryProblem? Problem(HistoryFileName file, HistoryFileOutcome outcome, Func<HistoryFileName, QuarantineResult>? quarantine)
    {
        if (outcome.State == HistoryFileState.Gone)
        {
            return null;
        }

        var code = WarningCode(outcome.State);
        if (outcome.State != HistoryFileState.Unreadable || quarantine is null)
        {
            return new CheckHistoryProblem(file.Name, code, outcome.Reason, null, false);
        }

        var moved = quarantine(file);
        return moved.Gone
            ? null
            : new CheckHistoryProblem(file.Name, code, outcome.Reason, moved.MovedTo, moved.MovedTo is null);
    }

    // One line per code with its count, in the fixed order of CheckHistoryWarningCodes.All. An unreadable file that was moved
    // aside is counted in history_unreadable and in history_quarantined; one that could not be moved, in history_quarantine_failed.
    private static List<CheckHistoryWarning> Warnings(IReadOnlyList<CheckHistoryProblem> problems)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string code) => counts[code] = counts.GetValueOrDefault(code) + 1;

        foreach (var problem in problems)
        {
            Add(problem.Code);
            if (problem.QuarantinedAs is not null)
            {
                Add(CheckHistoryWarningCodes.Quarantined);
            }
            else if (problem.QuarantineFailed)
            {
                Add(CheckHistoryWarningCodes.QuarantineFailed);
            }
        }

        var warnings = new List<CheckHistoryWarning>();
        foreach (var code in CheckHistoryWarningCodes.All)
        {
            if (counts.TryGetValue(code, out var count))
            {
                warnings.Add(new CheckHistoryWarning(code, count));
            }
        }

        return warnings;
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33) in the low word of the HRESULT.</summary>
    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
