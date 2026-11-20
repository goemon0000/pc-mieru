// Provenance: the counter names, the ATA bit masks, the growth verdicts and the continuity rule (counters that never go down, the NVMe
// hours against the clock) are those of the owner's pl-sensors Baseline (src/PartsLife.Checks/Storage/Baseline.cs), MIT, same owner.

using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The counters of one inspected drive as the baseline keeps them: the names v0.7.4 stored (so a record written here is read by
/// v0.7.4 and the other way round), each a ulong. A value that does not fit in 64 bits is stored as ulong.MaxValue (what v0.7.4 stored)
/// and is listed in <see cref="Inexact"/>: it must never be compared, because "grew" cannot be told from a clipped number.
/// </summary>
internal sealed class CounterSet
{
    public SortedDictionary<string, ulong> Values { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Inexact { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Values.Count == 0;

    /// <summary>
    /// The last value set for a name wins, as it did in v0.7.4 for a repeated SMART attribute. ulong.MaxValue itself counts as
    /// "does not fit": it is what a clipped number looks like, so it can never be told from a real one.
    /// </summary>
    public void Set(string name, UInt128 value, bool exact = true)
    {
        var fits = value < ulong.MaxValue;
        Values[name] = fits ? (ulong)value : ulong.MaxValue;
        if (exact && fits)
        {
            Inexact.Remove(name);
        }
        else
        {
            Inexact.Add(name);
        }
    }
}

internal static class BaselineCounters
{
    public const string MediaErrors = StorageLegacyCounterKeys.MediaErrors;
    public const string ErrorLogEntries = StorageLegacyCounterKeys.ErrorLogEntries;
    public const string UnsafeShutdowns = StorageLegacyCounterKeys.UnsafeShutdowns;
    public const string PowerOnHours = StorageLegacyCounterKeys.PowerOnHours;
    public const string DataUnitsWritten = StorageLegacyCounterKeys.DataUnitsWritten;
    public const string PercentageUsed = "nvme.percentage_used";
    public const string AvailableSpare = "nvme.available_spare";
    public const string CriticalWarning = "nvme.critical_warning";

    public static string Ata(int id) => $"ata.{id}";

    /// <summary>The ID of the counter's row in a result: "storage." and the stored name.</summary>
    public static string ItemId(string name) => "storage." + name;

    /// <summary>The 15 counters that have a ".delta" row, in the order of the item registry.</summary>
    public static IReadOnlyList<string> DeltaCounters { get; } =
    [
        CriticalWarning, AvailableSpare, PercentageUsed, MediaErrors, ErrorLogEntries, UnsafeShutdowns, PowerOnHours, DataUnitsWritten,
        Ata(5), Ata(187), Ata(196), Ata(197), Ata(198), Ata(199), Ata(9),
    ];

    public static bool HasDelta(string name) => DeltaCounters.Contains(name);

    /// <summary>The ATA attributes whose value is stored: the old <c>AtaSmart.KeyIds</c> and the three that are read for other reasons (9 hours, 190 and 194 temperature, which have no ".delta").</summary>
    private static readonly HashSet<int> StoredAtaIds = [5, 187, 196, 197, 198, 199, 9, 190, 194];

    /// <summary>
    /// What growing means (v0.7.4 <c>Counters.GrowthVerdict</c>): <see cref="ItemVerdict.Check"/> or <see cref="ItemVerdict.Caution"/> for a
    /// counter that should not grow, null for the ones that grow all the time and are only information (DESIGN-A §14.5).
    /// </summary>
    public static ItemVerdict? GrowthVerdict(string name) => name switch
    {
        MediaErrors or CriticalWarning => ItemVerdict.Check,
        "ata.5" or "ata.196" or "ata.197" or "ata.198" or "ata.187" => ItemVerdict.Check,
        ErrorLogEntries => ItemVerdict.Caution,
        "ata.199" => ItemVerdict.Caution, // CRC errors: the cable or the plug, more often than the disk
        _ => null,
    };

    /// <summary>The counters that never go down: going down means the record is another disk's (v0.7.4 <c>Monotonic</c>).</summary>
    public static bool IsMonotonic(string name) => name is PowerOnHours or DataUnitsWritten or "ata.9";

    /// <summary>
    /// Counters the baseline takes from a drive. Nothing for a drive whose health could not be read. Which counters are there depends on
    /// what the drive reports; the caller decides separately whether the health is known to be this drive's.
    /// </summary>
    public static CounterSet From(StorageInspectionDrive drive)
    {
        var set = new CounterSet();
        if (!drive.Health.TryGet(out var health))
        {
            return set;
        }

        if (health.Kind == StorageHealthKind.Nvme)
        {
            var unrepresentable = drive.Counters?.UnrepresentableCounters ?? [];
            AddWide(set, MediaErrors, health.MediaErrors, unrepresentable);
            AddWide(set, UnsafeShutdowns, health.UnsafeShutdowns, unrepresentable);
            AddWide(set, PowerOnHours, health.PowerOnHours, unrepresentable);

            if (drive.Counters?.ErrorLogEntries is { } errorLog)
            {
                Add(set, ErrorLogEntries, errorLog.Value, unrepresentable);
            }
            else if (health.Nvme is { } details)
            {
                AddWide(set, ErrorLogEntries, details.ErrorLogEntries, unrepresentable);
            }

            if (drive.Counters?.DataUnitsWritten is { } written)
            {
                Add(set, DataUnitsWritten, written.Value, unrepresentable);
            }

            AddWhole(set, PercentageUsed, health.PercentageUsed);
            AddWhole(set, AvailableSpare, health.AvailableSparePercent);
            if (health.CriticalWarning.TryGet(out var warning) && warning >= 0)
            {
                set.Set(CriticalWarning, (ulong)warning);
            }
        }
        else
        {
            foreach (var attribute in health.Attributes)
            {
                if (!StoredAtaIds.Contains(attribute.Id))
                {
                    continue;
                }

                var raw = attribute.Raw.Value;
                var value = attribute.Id switch
                {
                    190 or 194 => raw & 0xFF, // temperature: many models pack the lowest and highest in the upper bytes
                    9 => raw & 0xFFFFFFFF, // hours: some models pack milliseconds above; the unit is not converted (minutes, seconds exist)
                    _ => raw,
                };
                set.Set(Ata(attribute.Id), value);
            }
        }

        return set;
    }

    private static void Add(CounterSet set, string name, UInt128 value, IReadOnlyList<string> unrepresentable) =>
        set.Set(name, value, exact: !unrepresentable.Contains(name));

    private static void AddWide(CounterSet set, string name, Value<U128> value, IReadOnlyList<string> unrepresentable)
    {
        if (value.TryGet(out var wide))
        {
            Add(set, name, wide.Value, unrepresentable);
        }
    }

    /// <summary>A percentage that is a whole number. A fraction or a negative number is not a counter; the row is left out rather than rounded.</summary>
    private static void AddWhole(CounterSet set, string name, Value<double> value)
    {
        if (value.TryGet(out var number) && double.IsFinite(number) && number >= 0 && number < 1e18 && Math.Floor(number) == number)
        {
            set.Set(name, (ulong)number);
        }
    }

    // ---- continuity ----

    /// <summary>Whether a previous record can be the same disk's (v0.7.4 <c>BaselineStore.Continuity</c>).</summary>
    internal enum Continuity
    {
        Same,

        /// <summary>A counter that never goes down went down.</summary>
        WentBackwards,

        /// <summary>The NVMe power-on hours advanced by more than the time that passed since the record (and the slack).</summary>
        AheadOfClock,
    }

    /// <summary>Power-on hours are whole hours, cut down, and refreshed at the drive's own pace.</summary>
    internal static readonly TimeSpan HoursSlack = TimeSpan.FromHours(2);

    /// <summary>The recorded time is this machine's clock, which jumps at start-up and at a time sync.</summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the previous record can be this disk's: no counter that never goes down went down, and the NVMe power-on hours did not
    /// advance by more than the elapsed hours + <see cref="HoursSlack"/> + <see cref="ClockSkew"/>. Only the NVMe hours are in hours; ATA 9 is
    /// minutes, halves or seconds depending on the model, so it is only checked for going backwards. A counter that is not exact (this run's,
    /// or a stored ulong.MaxValue) takes no part: a clipped number says nothing.
    /// </summary>
    public static Continuity Check(BaselineEntry previous, CounterSet current, DateTimeOffset now)
    {
        foreach (var name in new[] { PowerOnHours, DataUnitsWritten, Ata(9) })
        {
            if (TryBoth(previous, current, name, out var before, out var after) && after < before)
            {
                return Continuity.WentBackwards;
            }
        }

        var elapsed = Math.Max(0, (now - previous.At).TotalHours);
        var allowed = elapsed + HoursSlack.TotalHours + ClockSkew.TotalHours;
        if (TryBoth(previous, current, PowerOnHours, out var hoursBefore, out var hoursAfter) && (double)(hoursAfter - hoursBefore) > allowed)
        {
            return Continuity.AheadOfClock;
        }

        return Continuity.Same;
    }

    /// <summary>Both records have the counter, and both values are exact. (A stored ulong.MaxValue is v0.7.4's clipped value.)</summary>
    public static bool TryBoth(BaselineEntry previous, CounterSet current, string name, out ulong before, out ulong after)
    {
        after = 0;
        if (!previous.Counters.TryGetValue(name, out before) || before == ulong.MaxValue
            || !current.Values.TryGetValue(name, out after) || current.Inexact.Contains(name))
        {
            return false;
        }

        return true;
    }
}
