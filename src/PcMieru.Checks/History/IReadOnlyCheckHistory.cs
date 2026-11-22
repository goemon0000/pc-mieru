namespace PcMieru.Checks;

/// <summary>
/// What a reader of the check history is given (DESIGN-A §2). The MCP tool gets only this type (DESIGN-A §8), and only from
/// <see cref="CheckHistory.OpenReadOnly"/>: nothing here saves, collects, migrates or deletes, and the object behind it has no
/// way to (no write-capable file system, no <see cref="CheckHistoryStore"/>, nothing to cast to). Both members read with
/// <see cref="CheckReadMode.Tolerant"/> (a file a later version wrote is not an error), and neither blocks the caller's thread.
/// </summary>
public interface IReadOnlyCheckHistory
{
    /// <summary>
    /// The retained runs, newest end time first (equal times: by UUID, ordinal order of the N-format string), at most 200 per kind,
    /// at most <see cref="CheckHistoryQuery.Limit"/> in all (null: everything retained). The warnings say what could not be
    /// listed (<see cref="CheckHistoryWarningCodes"/>); a page with warnings is still a successful listing. A missing history folder is
    /// an empty page, not an error. A folder that cannot be opened or enumerated at all throws <see cref="CheckHistoryFolderException"/>:
    /// that, and nothing else, is the folder-level failure.
    /// </summary>
    Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct);

    /// <summary>
    /// The stored result of a run: found, not found, or unreadable with the warning code that says why (a run whose file exists
    /// but cannot be read is not "not found"). A folder that cannot be opened throws <see cref="CheckHistoryFolderException"/>.
    /// </summary>
    Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct);
}

/// <summary>
/// What the GUI and the CLI use: list and read like <see cref="IReadOnlyCheckHistory"/> (but a corrupt file met on the way is moved
/// aside), and save a result. It is a separate type on purpose and does not extend <see cref="IReadOnlyCheckHistory"/>: a store can
/// never be handed to code that expects the read-only history, and the read-only history can never be cast back to a store.
/// </summary>
public interface ICheckHistoryMaintainer
{
    /// <summary>As <see cref="IReadOnlyCheckHistory.ListAsync"/>, and a corrupt file is moved aside (<c>.bad-</c>) and reported.</summary>
    Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct);

    /// <summary>As <see cref="IReadOnlyCheckHistory.GetAsync"/>, and a corrupt file of the run is moved aside (<c>history_quarantined</c>).</summary>
    Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Saves the result. It takes no <see cref="CancellationToken"/>: a stopped run is still saved, and the user's stop token is not
    /// passed to a write after the result is final (DESIGN-A §2). It always returns a <see cref="CheckSaveState"/> and never throws
    /// (only <see cref="OutOfMemoryException"/> is not caught).
    /// </summary>
    Task<CheckSaveState> SaveAsync(CheckResult result);
}

/// <summary>The one way to open the history for reading only (DESIGN-A §4.1, §8).</summary>
public static class CheckHistory
{
    /// <summary>
    /// The read-only history of this folder: list and get, and nothing else. It is built from the reading half of the file system
    /// alone, so there is no object behind it that can write, rename or delete. The folder is neither created nor touched.
    /// </summary>
    public static IReadOnlyCheckHistory OpenReadOnly(string root) => new ReadOnlyCheckHistory(root);
}
