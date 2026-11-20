namespace PcMieru.Checks;

/// <summary>
/// The thing a repeated item is about: a stable per-result number, never a path, drive letter, serial or device ID
/// (DESIGN-A §3). The id is "ram-N", "gpu-N", "volume-N", "pagefile-N", "disk-N" or "cpu-N", every kind numbered from 1
/// (see <see cref="SubjectKind"/>).
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

    // Value equality. The compiler-made one would compare Capacities (a list) by reference, so two evidences with the
    // same capacities would differ; and an item holding one would differ from its own round trip.
    public bool Equals(CheckEvidence? other) =>
        other is not null
        && Code == other.Code
        && string.Equals(Before, other.Before, StringComparison.Ordinal)
        && string.Equals(After, other.After, StringComparison.Ordinal)
        && SinceUtc == other.SinceUtc
        && NewBits == other.NewBits
        && string.Equals(TotalBytes, other.TotalBytes, StringComparison.Ordinal)
        && string.Equals(DiskId, other.DiskId, StringComparison.Ordinal)
        && EfficiencyClass == other.EfficiencyClass
        && Cv.Equals(other.Cv)
        && ClockSteadiness.Equals(other.ClockSteadiness)
        && string.Equals(WarningTempMinutes, other.WarningTempMinutes, StringComparison.Ordinal)
        && string.Equals(CriticalTempMinutes, other.CriticalTempMinutes, StringComparison.Ordinal)
        && RecentMediaErrors == other.RecentMediaErrors
        && RecentEntries == other.RecentEntries
        && SameCapacities(Capacities, other.Capacities);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Code);
        hash.Add(Before, StringComparer.Ordinal);
        hash.Add(After, StringComparer.Ordinal);
        hash.Add(SinceUtc);
        hash.Add(NewBits);
        hash.Add(TotalBytes, StringComparer.Ordinal);
        hash.Add(DiskId, StringComparer.Ordinal);
        hash.Add(EfficiencyClass);
        hash.Add(Cv);
        hash.Add(ClockSteadiness);
        hash.Add(WarningTempMinutes, StringComparer.Ordinal);
        hash.Add(CriticalTempMinutes, StringComparer.Ordinal);
        hash.Add(RecentMediaErrors);
        hash.Add(RecentEntries);
        if (Capacities is not null)
        {
            foreach (var capacity in Capacities)
            {
                hash.Add(capacity, StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }

    private static bool SameCapacities(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
    }
}

/// <summary>
/// One line of a result. <see cref="Unit"/> and the value type come from the registry
/// (<see cref="CheckItemRegistry"/>); <see cref="Create"/> fills the unit in.
/// <see cref="Reason"/> is null for a complete reading; a missing value always has a reason; a value that is present
/// with a reason is only allowed with <see cref="CheckReason.Partial"/>. Which verdicts go with which (value, reason)
/// pair is DESIGN-A §3.5-2 (an ok item has no reason; an unavailable judged item has no complete value).
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
