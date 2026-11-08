using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core;

[JsonConverter(typeof(CamelCaseEnumConverter<ServiceState>))]
public enum ServiceState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
    Unknown,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ServiceStartType>))]
public enum ServiceStartType
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Boot,
    System,
    Unknown,
}

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    ServiceState State,
    ServiceStartType StartType,
    uint? Pid,
    Value<string> Description,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Dependents);

[JsonConverter(typeof(CamelCaseEnumConverter<StartupState>))]
public enum StartupState
{
    Enabled,
    Disabled,
    Unknown,
}

/// <summary>
/// One startup entry. Id is stable ("&lt;provider&gt;:&lt;name&gt;") and is what the CLI and GUI pass back.
/// CanToggle is false for list-only providers and for entries whose approval value has an unknown type or state.
/// </summary>
public sealed record StartupItem(
    string Id,
    StartupProvider Provider,
    string Name,
    string Command,
    StartupState State,
    bool CanToggle,
    bool NeedsHelper,
    string? Detail = null);

[JsonConverter(typeof(CamelCaseEnumConverter<SessionConnectState>))]
public enum SessionConnectState
{
    Active,
    Connected,
    Disconnected,
    Idle,
    Other,
}

public sealed record SessionInfo(
    uint SessionId,
    string UserName,
    SessionConnectState State,
    string? WindowStation);

/// <summary>Result of any state-changing operation. Code maps 1:1 to the CLI exit code and the Helper exit code.</summary>
public sealed record OperationResult(
    OperationCode Code,
    string Message,
    IReadOnlyList<OperationDetail>? Details = null)
{
    public bool IsSuccess => Code == OperationCode.Ok;

    public static OperationResult Success(string message = "") => new(OperationCode.Ok, message);

    public static OperationResult Fail(OperationCode code, string message) => new(code, message);
}

/// <summary>Per-target outcome, used for tree termination and other partial successes.</summary>
public sealed record OperationDetail(string Target, OperationCode Code, string Message);

[JsonConverter(typeof(CamelCaseEnumConverter<ProtectionKind>))]
public enum ProtectionKind
{
    /// <summary>The process may be acted on.</summary>
    None,

    /// <summary>PC Mieru itself, critical, protected, PID 0/4 or a Windows core process.</summary>
    Protected,

    /// <summary>The query failed. Treated like Protected: no operation is offered.</summary>
    Unknown,
}

public sealed record ProtectionVerdict(ProtectionKind Kind, string? Reason)
{
    public bool MayOperate => Kind == ProtectionKind.None;
}

/// <summary>What "terminate tree" will touch, shown to the user before confirmation. Descendants are fixed at plan time.</summary>
public sealed record TerminatePlan(ProcessKey Root, IReadOnlyList<ProcessKey> Targets, IReadOnlyList<ProcessKey> SkippedProtected);

/// <summary>What happened to a Helper call (UAC declined, exit code, result file contents when trustworthy).</summary>
public sealed record HelperOutcome(OperationCode Code, bool UacDeclined, HelperResult? Result, string? Detail = null);
