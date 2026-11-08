using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core;

/// <summary>
/// The cheap, every-interval part of a process (everything SystemProcessInformation gives plus diffs).
/// Path, signature, owner and the like are fetched lazily into <see cref="ProcessDetails"/>.
/// Memory meaning: PrivateWorkingSetBytes = WorkingSetPrivateSize, WorkingSetBytes = WorkingSetSize,
/// CommitBytes = PrivatePageCount (bytes as-is, never multiplied by the page size).
/// </summary>
public sealed record ProcessInfo(
    ProcessKey Key,
    string Name,
    uint ParentPid,
    ProcessKey? ParentKey,
    uint SessionId,
    int ThreadCount,
    int HandleCount,
    int BasePriority,
    long WorkingSetBytes,
    long PrivateWorkingSetBytes,
    long CommitBytes,
    [property: JsonConverter(typeof(UInt64StringJsonConverter))] ulong IoReadBytesTotal,
    [property: JsonConverter(typeof(UInt64StringJsonConverter))] ulong IoWriteBytesTotal,
    Value<double> CpuPercent,
    Value<double> IoReadBytesPerSec,
    Value<double> IoWriteBytesPerSec,
    Value<double> GpuPercent,
    Value<ProcessState> State);

[JsonConverter(typeof(CamelCaseEnumConverter<ProcessState>))]
public enum ProcessState
{
    Running,
    NotResponding,
    Suspended,
}

/// <summary>Lazily fetched per-process facts, cached by ProcessKey.</summary>
public sealed record ProcessDetails(
    ProcessKey Key,
    Value<string> Path,
    Value<string> CommandLine,
    Value<string> Owner,
    Value<string> Product,
    Value<string> Description,
    Value<string> Company,
    Value<string> Architecture,
    Value<bool> EfficiencyMode,
    Value<SignatureInfo> Signature,
    IReadOnlyList<string> ServiceNames,
    KnownProcess? Known);

[JsonConverter(typeof(CamelCaseEnumConverter<SignatureState>))]
public enum SignatureState
{
    Unsigned,
    SignedEmbedded,
    SignedCatalog,
    Invalid,
}

/// <summary>Offline check only: revocation is not checked and the text shown must say so.</summary>
public sealed record SignatureInfo(SignatureState State, string? Signer);

/// <summary>An entry of the "what is this?" catalog (bundled JSON, ja/en text).</summary>
public sealed record KnownProcess(string Id, string DescriptionJa, string DescriptionEn, bool PathMatches, bool SignerMatches);
