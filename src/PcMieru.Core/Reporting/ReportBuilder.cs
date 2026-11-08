using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>The data side of the report: summary, hardware, storage health. Redaction happens at the output edge.</summary>
public sealed class ReportBuilder : IReportBuilder
{
    /// <summary>Longest observation a report may ask for (SPEC section 10 limits the MCP side the same way).</summary>
    public const int MaxDurationSec = 30;

    private readonly ISnapshotSource _snapshots;
    private readonly IObservationEngine _observations;
    private readonly IHardwareProvider _hardware;
    private readonly IStorageHealthProvider _storage;
    private readonly IRedactor _redactor;
    private readonly Func<DateTime> _clock;

    public ReportBuilder(
        ISnapshotSource snapshots,
        IObservationEngine observations,
        IHardwareProvider hardware,
        IStorageHealthProvider storage,
        IRedactor redactor,
        Func<DateTime> clock)
    {
        _snapshots = snapshots;
        _observations = observations;
        _hardware = hardware;
        _storage = storage;
        _redactor = redactor;
        _clock = clock;
    }

    public async Task<ReportDocument> BuildAsync(ReportOptions options, CancellationToken ct = default)
    {
        var health = options.IncludeStorageHealth ? await _storage.ReadAsync(false, ct).ConfigureAwait(false) : null;
        // The report says how long it really observed, so the number written is the clamped one.
        var seconds = Math.Clamp(options.DurationSec, 0, MaxDurationSec);
        var (_, summary) = await ObservationRun.RunAsync(_snapshots, _observations, TimeSpan.FromSeconds(seconds), health, ct).ConfigureAwait(false);
        var hardware = options.IncludeHardware ? await _hardware.GetAsync(HardwareCategory.All, ct).ConfigureAwait(false) : null;
        return new ReportDocument(
            ReportDocument.CurrentSchemaVersion,
            _clock(),
            _redactor.Enabled,
            seconds,
            summary,
            hardware,
            health is null ? null : new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, health));
    }
}
