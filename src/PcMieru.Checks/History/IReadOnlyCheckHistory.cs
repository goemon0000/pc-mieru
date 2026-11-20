namespace PcMieru.Checks;

/// <summary>
/// What a reader of the check history is given (DESIGN-A §2). The MCP tool gets only this type (DESIGN-A §8): nothing here
/// saves, collects, migrates or deletes. Both members read with <see cref="CheckReadMode.Tolerant"/> (a file a later
/// version wrote is not an error), and neither blocks the caller's thread.
/// </summary>
public interface IReadOnlyCheckHistory
{
    /// <summary>
    /// The retained runs, newest end time first (equal times: by UUID), at most 200 per kind, at most
    /// <see cref="CheckHistoryQuery.Limit"/> in all (null: everything retained). The warnings say what could not be listed
    /// (<see cref="CheckHistoryWarningCodes"/>). A missing history directory is an empty page, not an error; a directory that
    /// cannot be opened at all throws the <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct);

    /// <summary>The stored result of a run, or null when there is no such run or its file cannot be read.</summary>
    Task<CheckResult?> GetAsync(Guid runId, CancellationToken ct);
}
