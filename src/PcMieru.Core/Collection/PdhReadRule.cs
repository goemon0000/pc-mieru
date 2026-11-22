// Provenance: the package-instance pick (the "_PKG" suffix, mW summed and divided by 1000, 0.5 to 1500 W as plausible) is the rule of the owner's
// pl-sensors Sources.System (PackageWatts, PlausibleWatts; MIT, same owner). The "first valid item" read and frequency x performance rule are
// the ones PdhSystemSampler already had, moved here so the always-on sampler and the CPU check read a counter the same way.
namespace PcMieru.Core.Collection;

/// <summary>
/// How a PDH counter array is read, in one place. Both the always-on <see cref="PdhSystemSampler"/> and the CPU check's own query
/// (<see cref="CpuCheckPdhSession"/>) use it, each with a query of its own: the rule is shared, the query is not (DESIGN-A 6.2).
/// The rules are pure functions over the bytes of one <c>PdhGetFormattedCounterArrayW</c> answer, so they are tested without PDH.
/// </summary>
internal static class PdhReadRule
{
    /// <summary>The Energy Meter instances that are the whole CPU package end in this (<c>RAPL_Package0_PKG</c>); <c>_DRAM</c>, <c>_PP0</c> and the like are parts of it.</summary>
    internal const string PackageSuffix = "_PKG";

    /// <summary>A package power outside this is not a power reading (a unit or counter that was not what it was taken for), and is not used.</summary>
    internal const double MinPlausibleWatts = 0.5;

    internal const double MaxPlausibleWatts = 1500;

    /// <summary>Reads <paramref name="counter"/> and takes the first item with valid data. On false, <paramref name="why"/> says what was wrong.</summary>
    public static bool TryReadTotal(PdhArrayReader reader, nint counter, out double value, out string why)
    {
        value = 0;
        why = "counter returned no data";
        if (!reader.Read(counter))
        {
            why = $"counter read failed (PDH 0x{reader.Status:X8})";
            return false;
        }

        return TryPickFirstValid(reader.Bytes, reader.BaseAddress, reader.Count, out value, out why);
    }

    /// <summary>The value of the first item whose status says it holds data (valid or new). No such item: false, with the status of the last one tried.</summary>
    public static bool TryPickFirstValid(ReadOnlySpan<byte> buffer, ulong baseAddress, int count, out double value, out string why)
    {
        value = 0;
        why = "counter returned no data";
        for (var i = 0; i < count; i++)
        {
            if (!PdhArray.TryGetItem(buffer, baseAddress, i, count, out _, out var v, out var cstatus))
            {
                continue;
            }

            if (cstatus is Pdh.CStatusValidData or Pdh.CStatusNewData)
            {
                value = v;
                return true;
            }

            why = $"counter returned no data (status 0x{cstatus:X8})";
        }

        return false;
    }

    /// <summary>
    /// Task Manager's "Speed": the nominal frequency scaled by how far the cores are above or below it (boost goes past 100 %). Both values must
    /// come from the same collection. Null when the product is not a positive finite number: a missing or invalid sample is never 0 MHz.
    /// </summary>
    public static double? EffectiveMhz(double frequencyMhz, double performancePercent)
    {
        var mhz = frequencyMhz * performancePercent / 100.0;
        return mhz > 0 && double.IsFinite(mhz) ? mhz : null;
    }

    /// <summary>
    /// The CPU package power from the items of <c>\Energy Meter(*)\Power</c>: the instances that end in <c>_PKG</c> only, their milliwatts
    /// summed and divided by 1000. Every package instance must hold a valid, finite, non-negative value, or the sample is not used: a part
    /// of a multi-socket total would be a wrong number that looks right. A sum outside 0.5 to 1500 W is not used either.
    /// </summary>
    public static PackagePowerReading PackagePower(ReadOnlySpan<byte> buffer, ulong baseAddress, int count)
    {
        var instances = 0;
        var usable = true;
        var milliwatts = 0.0;
        for (var i = 0; i < count; i++)
        {
            if (!PdhArray.TryGetItem(buffer, baseAddress, i, count, out var name, out var value, out var cstatus))
            {
                continue; // an item whose name cannot be read is not known to be a package
            }

            if (!name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            instances++;
            if (cstatus is not (Pdh.CStatusValidData or Pdh.CStatusNewData) || !double.IsFinite(value) || value < 0)
            {
                usable = false;
                continue;
            }

            milliwatts += value;
        }

        if (instances == 0)
        {
            return new PackagePowerReading(PackagePowerState.NoPackageInstance, 0);
        }

        if (!usable)
        {
            return new PackagePowerReading(PackagePowerState.NoValidSample, 0);
        }

        var watts = milliwatts / 1000.0;
        return watts is >= MinPlausibleWatts and <= MaxPlausibleWatts
            ? new PackagePowerReading(PackagePowerState.Valid, watts)
            : new PackagePowerReading(PackagePowerState.NoValidSample, 0);
    }
}

/// <summary>Why a package power sample is or is not there.</summary>
internal enum PackagePowerState
{
    /// <summary>The Energy Meter counter could not be added: this Windows has none.</summary>
    NoCounter,

    /// <summary>The counter is there but lists no package (<c>_PKG</c>) instance: the enumeration cannot be confirmed.</summary>
    NoPackageInstance,

    /// <summary>A package instance is there but this sample is not usable (invalid status, not a number, implausible watts, or the read failed).</summary>
    NoValidSample,

    Valid,
}

/// <summary>The package power of one sample. <see cref="Watts"/> is meaningful only when <see cref="State"/> is <see cref="PackagePowerState.Valid"/>.</summary>
internal readonly record struct PackagePowerReading(PackagePowerState State, double Watts);
