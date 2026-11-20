namespace PcMieru.Checks;

/// <summary>
/// The thing a repeated item is about: a stable per-result number, never a path, drive letter, serial or device ID
/// (DESIGN-A §3). The id is "ram-1", "gpu-1", "volume-1", "pagefile-1", "disk-N" or "cpu-N" (see <see cref="SubjectKind"/>).
/// <see cref="Model"/> is a model name as read, never a serial.
/// </summary>
public sealed record CheckSubject(string Id, string? Model = null);

/// <summary>
/// The fixed evidence keys (DESIGN-A §14.0), in the order they are written. Every key is optional; an evidence object
/// has at least one. Free diagnostic text is never carried: only these keys, with these types.
/// </summary>
public sealed record CheckEvidence
{
    public CheckEvidenceCode? Code { get; init; }

    /// <summary>The counter before and after (unsigned decimal strings).</summary>
    public string? Before { get; init; }

    public string? After { get; init; }

    /// <summary>When the "before" was taken. UTC, millisecond precision.</summary>
    public DateTimeOffset? SinceUtc { get; init; }

    /// <summary>Warning bits newly set since the last comparison.</summary>
    public int? NewBits { get; init; }

    /// <summary>The volume size in bytes (unsigned decimal string).</summary>
    public string? TotalBytes { get; init; }

    /// <summary>"disk-N" of the disk a volume lives on, only when that is certain.</summary>
    public string? DiskId { get; init; }

    public int? EfficiencyClass { get; init; }

    /// <summary>Coefficient of variation times 100.</summary>
    public double? Cv { get; init; }

    /// <summary>Clock sustainability in percent.</summary>
    public double? ClockSteadiness { get; init; }

    public string? WarningTempMinutes { get; init; }

    public string? CriticalTempMinutes { get; init; }

    public int? RecentMediaErrors { get; init; }

    public int? RecentEntries { get; init; }

    /// <summary>Module capacities in bytes (unsigned decimal strings).</summary>
    public IReadOnlyList<string>? Capacities { get; init; }

    public bool IsEmpty =>
        Code is null && Before is null && After is null && SinceUtc is null && NewBits is null && TotalBytes is null
        && DiskId is null && EfficiencyClass is null && Cv is null && ClockSteadiness is null && WarningTempMinutes is null
        && CriticalTempMinutes is null && RecentMediaErrors is null && RecentEntries is null && Capacities is null;

    public static CheckEvidence OfCode(CheckEvidenceCode code) => new() { Code = code };
}

/// <summary>
/// One line of a result. <see cref="Unit"/> and the value type come from the registry
/// (<see cref="CheckItemRegistry"/>); <see cref="Create"/> fills the unit in.
/// <see cref="Reason"/> is null for a complete reading; a missing value always has a reason; a value that is present
/// with a reason is only allowed with <see cref="CheckReason.Partial"/>.
/// </summary>
public sealed record CheckItem(
    string Id,
    CheckSubject? Subject,
    CheckValue? Value,
    string? Unit,
    ItemVerdict Verdict,
    CheckSource Source,
    CheckReason? Reason,
    CheckEvidence? Evidence = null)
{
    /// <summary>An item with its unit taken from the registry. Throws for an ID the registry does not know.</summary>
    public static CheckItem Create(
        string id,
        CheckValue? value,
        ItemVerdict verdict,
        CheckSource source,
        CheckReason? reason = null,
        CheckSubject? subject = null,
        CheckEvidence? evidence = null)
    {
        var spec = CheckItemRegistry.Get(id);
        return new CheckItem(id, subject, value, spec.Unit, verdict, source, reason, evidence);
    }
}
