using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The history of <c>--fakes</c>, kept in memory: the sample runs, plus every run the fake runner "saved". It lists like the real history (newest
/// end first, equal times by the run's UUID, at most the limit asked for) and reads and writes nothing on disk.
/// </summary>
internal sealed class InMemoryCheckHistory : IReadOnlyCheckHistory
{
    private readonly object _gate = new();
    private readonly List<CheckResult> _results;

    public InMemoryCheckHistory(IEnumerable<CheckResult>? seed = null)
    {
        _results = [.. seed ?? FakeCheckData.Seed];
    }

    /// <summary>A run was saved.</summary>
    public void Add(CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _results.RemoveAll(r => r.RunId == result.RunId);
            _results.Add(result);
        }
    }

    public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "limit must be 1 or more.");
        }

        ct.ThrowIfCancellationRequested();
        CheckResult[] all;
        lock (_gate)
        {
            all = [.. _results];
        }

        var entries = all
            .Where(r => query.Kind is null || r.Kind == query.Kind)
            .OrderByDescending(static r => r.EndedAtUtc)
            .ThenBy(static r => r.RunId.ToString("N"), StringComparer.Ordinal)
            .Take(query.Limit ?? int.MaxValue)
            .Select(CheckHistoryEntry.From)
            .ToArray();
        return Task.FromResult(new CheckHistoryPage(entries, []));
    }

    public Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckResult? found;
        lock (_gate)
        {
            found = _results.FirstOrDefault(r => r.RunId == runId);
        }

        return Task.FromResult(found is null ? CheckHistoryGetResult.NotFound : CheckHistoryGetResult.Found(found));
    }
}
