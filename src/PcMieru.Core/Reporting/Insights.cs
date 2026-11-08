using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>Read-only facade over the other services. The MCP server sees only this.</summary>
public sealed class Insights : IReadOnlyInsights
{
    private readonly ISnapshotSource _snapshots;
    private readonly IProcessDetailsProvider _details;
    private readonly IHardwareProvider _hardware;
    private readonly IStorageHealthProvider _storage;
    private readonly IObservationEngine _observations;
    private readonly IReportBuilder _reports;
    private readonly TimeSpan _defaultWindow;
    private readonly SamplerLoop? _sampler;
    private readonly SemaphoreSlim _observeGate = new(1, 1);

    public Insights(
        ISnapshotSource snapshots,
        IProcessDetailsProvider details,
        IHardwareProvider hardware,
        IStorageHealthProvider storage,
        IObservationEngine observations,
        IReportBuilder reports,
        TimeSpan defaultWindow,
        SamplerLoop? sampler = null)
    {
        _snapshots = snapshots;
        _details = details;
        _hardware = hardware;
        _storage = storage;
        _observations = observations;
        _reports = reports;
        _defaultWindow = defaultWindow;
        _sampler = sampler;
    }

    public async Task<SystemSummary> GetSystemSummaryAsync(CancellationToken ct = default)
    {
        await _observeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await SnapshotSampling.MeasureAsync(_snapshots, _defaultWindow, ct).ConfigureAwait(false);
            return ObservationRun.Summarize(snapshot, [snapshot], _observations, null);
        }
        finally
        {
            _observeGate.Release();
        }
    }

    public async Task<ProcessList> ListProcessesAsync(ProcessQuery query, CancellationToken ct = default)
    {
        await _observeGate.WaitAsync(ct).ConfigureAwait(false);
        Snapshot snapshot;
        try
        {
            snapshot = await SnapshotSampling.MeasureAsync(_snapshots, query.Sample ?? _defaultWindow, ct).ConfigureAwait(false);
        }
        finally
        {
            _observeGate.Release();
        }

        IEnumerable<ProcessInfo> rows = snapshot.Processes;
        if (!string.IsNullOrEmpty(query.NameContains))
        {
            rows = rows.Where(p => p.Name.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = rows.ToArray();
        IEnumerable<ProcessInfo> sorted = query.Sort switch
        {
            ProcessSort.Mem => filtered.OrderByDescending(p => p.PrivateWorkingSetBytes),
            ProcessSort.Io => filtered.OrderByDescending(p => p.IoReadBytesPerSec.GetOr(0) + p.IoWriteBytesPerSec.GetOr(0)),
            ProcessSort.Gpu => filtered.OrderByDescending(p => p.GpuPercent.GetOr(-1)),
            ProcessSort.Name => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(CpuKey),
        };

        if (query.Sort != ProcessSort.Name)
        {
            sorted = ((IOrderedEnumerable<ProcessInfo>)sorted).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        }

        var list = (query.Top is { } top and > 0 ? sorted.Take(top) : sorted).ToArray();
        return new ProcessList(ProcessList.CurrentSchemaVersion, snapshot.TakenAtUtc, snapshot.IntervalMs, query.Sort, filtered.Length, list);
    }

    /// <summary>The idle pseudo-process (PID 0) reports the idle share; it must not top a "heaviest first" list.</summary>
    private static double CpuKey(ProcessInfo p) => p.Key.Pid == 0 ? -2 : p.CpuPercent.GetOr(-1);

    public async Task<ProcessDetail?> GetProcessAsync(uint pid, CancellationToken ct = default)
    {
        Snapshot snapshot;
        await _observeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = await SnapshotSampling.MeasureAsync(_snapshots, _defaultWindow, ct).ConfigureAwait(false);
        }
        finally
        {
            _observeGate.Release();
        }

        var process = snapshot.Processes.FirstOrDefault(p => p.Key.Pid == pid);
        return process is null ? null : new ProcessDetail(process, await _details.GetAsync(process.Key, ct).ConfigureAwait(false));
    }

    public Task<HardwareInfo> GetHardwareAsync(HardwareCategory category, CancellationToken ct = default) =>
        _hardware.GetAsync(category, ct);

    public async Task<StorageHealthReport> GetStorageHealthAsync(CancellationToken ct = default) =>
        new(StorageHealthReport.CurrentSchemaVersion, await _storage.ReadAsync(false, ct).ConfigureAwait(false));

    public async Task<DiagnoseResult> DiagnoseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (!await _observeGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("another observation is already running");
        }

        try
        {
            var health = await _storage.ReadAsync(false, ct).ConfigureAwait(false);
            var seconds = (int)Math.Round(duration.TotalSeconds);
            var (_, summary) = await ObservationRun.RunAsync(_snapshots, _observations, duration, health, ct, _sampler).ConfigureAwait(false);
            return new DiagnoseResult(DiagnoseResult.CurrentSchemaVersion, seconds, summary);
        }
        finally
        {
            _observeGate.Release();
        }
    }

    public Task<ReportDocument> GetReportAsync(ReportOptions options, CancellationToken ct = default) =>
        _reports.BuildAsync(options, ct);
}
