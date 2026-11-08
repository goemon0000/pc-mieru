using System.Text.Json.Serialization;
using PcMieru.Core.Hardware.Model;
using PcMieru.Wire;

namespace PcMieru.Core;

[JsonConverter(typeof(CamelCaseEnumConverter<ProcessSort>))]
public enum ProcessSort
{
    Cpu,
    Mem,
    Io,
    Gpu,
    Name,
}

/// <summary>Query for list_processes / ps. Sample is the observation window needed for CPU numbers.</summary>
public sealed record ProcessQuery(
    ProcessSort Sort = ProcessSort.Cpu,
    int? Top = null,
    string? NameContains = null,
    TimeSpan? Sample = null);

public sealed record ProcessList(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime TakenAtUtc,
    int IntervalMs,
    ProcessSort Sort,
    int TotalProcesses,
    IReadOnlyList<ProcessInfo> Processes)
{
    public const string CurrentSchemaVersion = "1";
}

public sealed record ProcessDetail(ProcessInfo Process, ProcessDetails? Details);

/// <summary>Whole-machine numbers plus the observations and the heaviest processes.</summary>
public sealed record SystemSummary(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime TakenAtUtc,
    int IntervalMs,
    SystemInfo System,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<ProcessInfo> TopProcesses)
{
    public const string CurrentSchemaVersion = "1";
}

public sealed record StorageHealthReport(string SchemaVersion, IReadOnlyList<DriveHealth> Drives)
{
    public const string CurrentSchemaVersion = "1";
}

public sealed record DiagnoseResult(
    string SchemaVersion,
    int DurationSec,
    SystemSummary Summary)
{
    public const string CurrentSchemaVersion = "1";
}

public sealed record ReportOptions(int DurationSec = 10, bool IncludeHardware = true, bool IncludeStorageHealth = true);

/// <summary>The "diagnostic report" of the GUI tab 8, the CLI report command and the MCP get_report tool.</summary>
public sealed record ReportDocument(
    string SchemaVersion,
    [property: JsonConverter(typeof(UtcDateTimeJsonConverter))] DateTime GeneratedAtUtc,
    bool Redacted,
    int DurationSec,
    SystemSummary Summary,
    HardwareInfo? Hardware,
    StorageHealthReport? Storage)
{
    public const string CurrentSchemaVersion = "1";
}

/// <summary>
/// The ONLY surface the MCP server may call. Every method reads; none changes anything, none raises UAC.
/// Do not add a state-changing method here (a test pins the member list).
/// </summary>
public interface IReadOnlyInsights
{
    Task<SystemSummary> GetSystemSummaryAsync(CancellationToken ct = default);

    Task<ProcessList> ListProcessesAsync(ProcessQuery query, CancellationToken ct = default);

    Task<ProcessDetail?> GetProcessAsync(uint pid, CancellationToken ct = default);

    Task<HardwareInfo> GetHardwareAsync(HardwareCategory category, CancellationToken ct = default);

    Task<StorageHealthReport> GetStorageHealthAsync(CancellationToken ct = default);

    /// <summary>Observes for <paramref name="duration"/> (the caller caps it at 30 s) and returns findings.</summary>
    Task<DiagnoseResult> DiagnoseAsync(TimeSpan duration, CancellationToken ct = default);

    Task<ReportDocument> GetReportAsync(ReportOptions options, CancellationToken ct = default);
}
