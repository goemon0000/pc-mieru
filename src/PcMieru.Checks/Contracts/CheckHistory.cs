namespace PcMieru.Checks;

// DESIGN-A §2: the history shapes, fixed here so the store (History/), the CLI and the MCP tool agree. The reading
// interface (IReadOnlyCheckHistory) and the files behind it live in History/; only the data shapes live here.

/// <summary>
/// Which runs to list. Kind null is every kind; Limit null is everything kept (CLI, GUI: up to 200 per kind). The stores take
/// any Limit of 1 or more (0 and below are refused). The MCP tool's own range, 1-200 with default 20, is applied where the
/// tool builds its query: <see cref="ForMcp"/>.
/// </summary>
public sealed record CheckHistoryQuery(CheckKind? Kind, int? Limit)
{
    /// <summary>The MCP tool's default <c>limit</c>.</summary>
    public const int McpDefaultLimit = 20;

    /// <summary>The MCP tool's largest <c>limit</c> (the most a kind keeps).</summary>
    public const int McpMaxLimit = 200;

    /// <summary>
    /// The query of an MCP <c>get_check_history</c> call: an absent limit is <see cref="McpDefaultLimit"/>; a limit outside
    /// 1-<see cref="McpMaxLimit"/> throws <see cref="ArgumentOutOfRangeException"/> (the tool answers it as a tool error).
    /// </summary>
    public static CheckHistoryQuery ForMcp(CheckKind? kind, int? limit)
    {
        var effective = limit ?? McpDefaultLimit;
        if (effective is < 1 or > McpMaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be 1-{McpMaxLimit}.");
        }

        return new CheckHistoryQuery(kind, effective);
    }
}

/// <summary>One row of the history listing. <see cref="Verdict"/> is the overall four-valued verdict.</summary>
public sealed record CheckHistoryEntry(
    Guid RunId,
    CheckKind Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    CheckOutcome Outcome,
    CheckVerdict Verdict,
    CheckCounts Counts)
{
    public static CheckHistoryEntry From(CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new CheckHistoryEntry(result.RunId, result.Kind, result.StartedAtUtc, result.EndedAtUtc, result.Outcome, result.Verdict, result.Counts);
    }
}

/// <summary>A listing problem, one line per code with a count.</summary>
public sealed record CheckHistoryWarning(string Code, int Count);

/// <summary>What fetching one run came to (DESIGN-A §2): found, no such run, or a file of that run that could not be read.</summary>
public enum CheckHistoryGetStatus
{
    /// <summary>The run's file was read: <see cref="CheckHistoryGetResult.Result"/> is set.</summary>
    Found,

    /// <summary>No file in the history carries this run ID.</summary>
    NotFound,

    /// <summary>A file of this run is there but could not be read: <see cref="CheckHistoryGetResult.WarningCode"/> says why.</summary>
    Unreadable,
}

/// <summary>
/// The answer to <c>GetAsync</c>: exactly one of <see cref="CheckHistoryGetResult.Found"/>, <see cref="CheckHistoryGetResult.NotFound"/>
/// or <see cref="CheckHistoryGetResult.Unreadable"/>. A run whose file exists but cannot be read is never "not found": the MCP tool
/// answers it with a tool error that carries <see cref="WarningCode"/>, and the CLI exits 1.
/// </summary>
public sealed record CheckHistoryGetResult
{
    private CheckHistoryGetResult(CheckHistoryGetStatus status, CheckResult? result, string? warningCode)
    {
        Status = status;
        Result = result;
        WarningCode = warningCode;
    }

    public CheckHistoryGetStatus Status { get; }

    /// <summary>The stored result. Set only for <see cref="CheckHistoryGetStatus.Found"/>.</summary>
    public CheckResult? Result { get; }

    /// <summary>One of <see cref="CheckHistoryWarningCodes.All"/>. Set only for <see cref="CheckHistoryGetStatus.Unreadable"/>.</summary>
    public string? WarningCode { get; }

    public static CheckHistoryGetResult NotFound { get; } = new(CheckHistoryGetStatus.NotFound, null, null);

    public static CheckHistoryGetResult Found(CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new CheckHistoryGetResult(CheckHistoryGetStatus.Found, result, null);
    }

    public static CheckHistoryGetResult Unreadable(string warningCode)
    {
        if (!CheckHistoryWarningCodes.IsKnown(warningCode))
        {
            throw new ArgumentOutOfRangeException(nameof(warningCode), warningCode, "Not one of the history warning codes.");
        }

        return new CheckHistoryGetResult(CheckHistoryGetStatus.Unreadable, null, warningCode);
    }
}

/// <summary>The listing: entries newest first, plus the warnings about files that could not be listed.</summary>
public sealed record CheckHistoryPage(
    IReadOnlyList<CheckHistoryEntry> Entries,
    IReadOnlyList<CheckHistoryWarning> Warnings);

/// <summary>
/// The warning codes of a history listing (DESIGN-A §2, §4). No others. The first four are the codes of §2; the last three
/// (a file that is kept as it is because it cannot be read right now) were fixed in step 2 for the cases §4 names:
/// a sharing violation, a permission error, and any other I/O failure.
/// </summary>
public static class CheckHistoryWarningCodes
{
    /// <summary>Broken JSON, or required fields that disagree.</summary>
    public const string Unreadable = "history_unreadable";

    /// <summary>An unknown "format" or an unknown "kind": a newer version wrote it; the file is kept as it is.</summary>
    public const string FutureFormat = "history_future_format";

    /// <summary>The file was moved aside (.bad-) by this listing.</summary>
    public const string Quarantined = "history_quarantined";

    /// <summary>The file could not be moved aside and was left.</summary>
    public const string QuarantineFailed = "history_quarantine_failed";

    /// <summary>Another process holds the file (a sharing or lock violation). Kept as it is; it is listed once it can be read.</summary>
    public const string Locked = "history_locked";

    /// <summary>The permission to read the file is missing. Kept as it is.</summary>
    public const string AccessDenied = "history_access_denied";

    /// <summary>Any other I/O failure while reading the file (or a fault of the reader itself). Kept as it is.</summary>
    public const string ReadFailed = "history_read_failed";

    /// <summary>The fixed order a page lists its warnings in.</summary>
    public static IReadOnlyList<string> All { get; } = [Unreadable, FutureFormat, Quarantined, QuarantineFailed, Locked, AccessDenied, ReadFailed];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}
