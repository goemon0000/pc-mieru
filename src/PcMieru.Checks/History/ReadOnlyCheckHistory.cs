namespace PcMieru.Checks;

/// <summary>
/// The history as the MCP server sees it (DESIGN-A §4, §8): it lists and reads, and that is all. It is built from
/// <see cref="ICheckHistoryFileReader"/> only, so it has no way to save, rename (quarantine), delete or clean up. A file it
/// cannot read is skipped and counted in the warnings (<see cref="CheckHistoryWarningCodes.Unreadable"/>,
/// <see cref="CheckHistoryWarningCodes.FutureFormat"/>, <see cref="CheckHistoryWarningCodes.Locked"/>,
/// <see cref="CheckHistoryWarningCodes.AccessDenied"/>, <see cref="CheckHistoryWarningCodes.ReadFailed"/>); it never
/// reports a quarantine, because it makes none.
/// </summary>
internal sealed class ReadOnlyCheckHistory : IReadOnlyCheckHistory
{
    private readonly CheckHistoryScanner _scanner;

    public ReadOnlyCheckHistory(string root, ICheckHistoryFileReader? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _scanner = new CheckHistoryScanner(files ?? PhysicalHistoryFileSystem.Instance, root);
    }

    public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Scan(query, ct).Page, ct);
    }

    public Task<CheckResult?> GetAsync(Guid runId, CancellationToken ct) => Task.Run(() => _scanner.Get(runId, quarantine: null, ct).Result, ct);

    /// <summary>The listing with the per-file reasons behind its warnings.</summary>
    internal CheckHistoryScan Scan(CheckHistoryQuery query, CancellationToken ct) => _scanner.Scan(query, quarantine: null, ct);
}
