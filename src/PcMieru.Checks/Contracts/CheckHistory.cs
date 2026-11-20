namespace PcMieru.Checks;

// DESIGN-A §2: the history shapes, fixed here so the store (step 2), the CLI and the MCP tool agree. The store, the
// listing and the reading interfaces come with their own steps; only the data shapes live here.

/// <summary>Which runs to list. Kind null is every kind; Limit null is everything kept (CLI, GUI: up to 200 per kind). MCP uses 1-200, default 20.</summary>
public sealed record CheckHistoryQuery(CheckKind? Kind, int? Limit);

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

/// <summary>The listing: entries newest first, plus the warnings about files that could not be listed.</summary>
public sealed record CheckHistoryPage(
    IReadOnlyList<CheckHistoryEntry> Entries,
    IReadOnlyList<CheckHistoryWarning> Warnings);

/// <summary>The four warning codes of a history listing (DESIGN-A §2). No others.</summary>
public static class CheckHistoryWarningCodes
{
    /// <summary>Broken JSON, or required fields that disagree.</summary>
    public const string Unreadable = "history_unreadable";

    /// <summary>An unknown "format": a newer version wrote it; the file is kept as it is.</summary>
    public const string FutureFormat = "history_future_format";

    /// <summary>The file was moved aside (.bad-) by this listing.</summary>
    public const string Quarantined = "history_quarantined";

    /// <summary>The file could not be moved aside and was left.</summary>
    public const string QuarantineFailed = "history_quarantine_failed";

    public static IReadOnlyList<string> All { get; } = [Unreadable, FutureFormat, Quarantined, QuarantineFailed];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}
