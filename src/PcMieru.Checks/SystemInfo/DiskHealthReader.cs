// Provenance: the verdict rule (HealthStatus 2 or an OperationalStatus of 3, 5, 6 or 7 is a check, 1 a caution, 0 healthy) is the one of the
// owner's pl-cloud SystemCheck.JudgeDisk (src/PartsLife.Checks/SystemInfo/SystemCheck.cs, MIT, same owner); the join to a drive number, the
// serial check and the deadline are new (DESIGN-A 14.1, 14.4, 6.1).

using System.Globalization;

namespace PcMieru.Checks.SystemInfo;

/// <summary>One row of <c>MSFT_PhysicalDisk</c> as the health check keeps it. Nothing in it is shown: it is only joined and judged.</summary>
/// <param name="DeviceId">The row's DeviceId, trimmed ("0", "1"...). Null when the row had none.</param>
/// <param name="Serial">The serial as Windows names it, trimmed. Null when empty. Never written to a result.</param>
/// <param name="HealthStatus">0 healthy, 1 warning, 2 unhealthy, 5 unknown; null when the property had no number.</param>
/// <param name="OperationalStatus">The OperationalStatus codes (3 degraded, 5 predictive failure, 6 error, 7 non-recoverable...); empty when there are none.</param>
internal sealed record PhysicalDiskRow(string? DeviceId, string? Serial, int? HealthStatus, IReadOnlyList<int> OperationalStatus);

/// <summary>
/// What Windows says of one disk, ready to be written as a row: a value (healthy, warning, critical) with its verdict, or no value and the
/// reason. Never a roll-up of the SMART rows of the same disk (DESIGN-A 14.4): the value comes from <c>MSFT_PhysicalDisk</c> alone.
/// </summary>
internal readonly record struct DiskHealthVerdict
{
    private DiskHealthVerdict(string? text, ItemVerdict verdict, CheckReason? reason)
    {
        Text = text;
        Verdict = verdict;
        Reason = reason;
    }

    /// <summary>"healthy", "warning" or "critical"; null when Windows' answer could not be used.</summary>
    public string? Text { get; }

    public ItemVerdict Verdict { get; }

    public CheckReason? Reason { get; }

    public bool HasValue => Text is not null;

    public static DiskHealthVerdict Healthy { get; } = new("healthy", ItemVerdict.Ok, null);

    public static DiskHealthVerdict Warning { get; } = new("warning", ItemVerdict.Caution, null);

    public static DiskHealthVerdict Critical { get; } = new("critical", ItemVerdict.Check, null);

    /// <summary>No usable answer for this disk, and why. A default value (no reading was made) is "error".</summary>
    public static DiskHealthVerdict Unavailable(CheckReason reason) => new(null, ItemVerdict.Unavailable, reason);

    /// <summary>The row of this disk: the value with its verdict, or no value, the reason and "unavailable". The source is always <c>wmi</c>.</summary>
    public CheckItem ToItem(string id, CheckSubject subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(subject);
        return Text is { } text
            ? CheckItem.Create(id, CheckValue.Text(text), Verdict, CheckSource.Wmi, null, subject)
            : CheckItem.Create(id, null, ItemVerdict.Unavailable, CheckSource.Wmi, Reason ?? CheckReason.Error, subject);
    }
}

/// <summary>
/// The one reading of the disks' health of a run (DESIGN-A 14.1, 14.4): <c>MSFT_PhysicalDisk</c> read through the WMI reader with its own
/// 5 s deadline (6.1). Each disk of the run asks it for its row by its drive number. A reading that was not made, or did not come, gives
/// every disk "unavailable" with the reading's reason.
/// </summary>
internal sealed class DiskHealthReading
{
    private readonly CheckReading<IReadOnlyList<PhysicalDiskRow>> _rows;

    public DiskHealthReading(CheckReading<IReadOnlyList<PhysicalDiskRow>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows;
    }

    /// <summary>A reading that has nothing (the question was not asked or its answer was thrown away), for a reason.</summary>
    public static DiskHealthReading Missing(CheckReason reason) => new(CheckReading<IReadOnlyList<PhysicalDiskRow>>.Missing(reason));

    /// <summary>
    /// The health of the disk with this drive number. The row is found by <c>DeviceId == drive number</c>. When the disk's serial (as this
    /// side read it) and the row's serial are both there, they must be the same, or the row is another disk's and nothing is said of this
    /// one (reason error): a drive number is only a label of one moment, so it is not trusted alone when there is more to check. A disk
    /// with no row, or with two, is an error too.
    /// </summary>
    public DiskHealthVerdict Of(int driveNumber, string? serial)
    {
        if (!_rows.HasValue)
        {
            return DiskHealthVerdict.Unavailable(_rows.Reason ?? CheckReason.Error);
        }

        var key = driveNumber.ToString(CultureInfo.InvariantCulture);
        PhysicalDiskRow? found = null;
        foreach (var row in _rows.Value)
        {
            if (!string.Equals(row.DeviceId, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return DiskHealthVerdict.Unavailable(CheckReason.Error); // two rows for one number: which one is this disk is not known
            }

            found = row;
        }

        if (found is null)
        {
            return DiskHealthVerdict.Unavailable(CheckReason.Error);
        }

        var ours = DiskHealthReader.NormalizeSerial(serial);
        var theirs = DiskHealthReader.NormalizeSerial(found.Serial);
        if (ours is not null && theirs is not null && !string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase))
        {
            return DiskHealthVerdict.Unavailable(CheckReason.Error);
        }

        return DiskHealthReader.Judge(found.HealthStatus, found.OperationalStatus);
    }
}

/// <summary>The question, its mapper and the verdict rule of the disk health (DESIGN-A 14.1).</summary>
internal static class DiskHealthReader
{
    /// <summary>The deadline of the question (DESIGN-A 6.1: each WMI question has its own 5 s).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The fixed question. DeviceId is the drive number as text, SerialNumber is only for the check that the row is the disk's; there is no
    /// model, location or unique id in it.
    /// </summary>
    public static readonly WmiQuery<PhysicalDiskRow> Query = new(
        WmiScopes.Storage,
        "SELECT DeviceId, SerialNumber, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk",
        Map);

    /// <summary>
    /// One read, with its own deadline. It never throws for a read that failed: the reason is in the reading (a stop by the caller is the only
    /// exception, and it is passed on).
    /// </summary>
    public static async Task<DiskHealthReading> ReadAsync(IWmiReader wmi, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            var reading = await wmi.ReadAsync(Query, QueryDeadline.After(time, Budget), ct).ConfigureAwait(false);
            return new DiskHealthReading(reading);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DiskHealthReading.Missing(CheckReason.Error);
        }
    }

    /// <summary>
    /// DESIGN-A 14.1: HealthStatus 2, or an OperationalStatus that has 3, 5, 6 or 7, is critical (a check); HealthStatus 1 is a warning (a
    /// caution); 0 is healthy. The operational codes are looked at whatever the HealthStatus says. A HealthStatus of 5 (unknown) or any other
    /// number is "unsupported" (Windows answered that it does not know), a missing one is an error.
    /// </summary>
    public static DiskHealthVerdict Judge(int? healthStatus, IReadOnlyList<int> operationalStatus)
    {
        ArgumentNullException.ThrowIfNull(operationalStatus);
        if (healthStatus == 2 || operationalStatus.Any(static code => code is 3 or 5 or 6 or 7))
        {
            return DiskHealthVerdict.Critical;
        }

        return healthStatus switch
        {
            1 => DiskHealthVerdict.Warning,
            0 => DiskHealthVerdict.Healthy,
            null => DiskHealthVerdict.Unavailable(CheckReason.Error),
            _ => DiskHealthVerdict.Unavailable(CheckReason.Unsupported),
        };
    }

    /// <summary>A serial as it is compared: trimmed, without the dots Windows pads NVMe serials with, and null when nothing is left.</summary>
    internal static string? NormalizeSerial(string? serial)
    {
        var trimmed = serial?.Trim().TrimEnd('.').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>A tolerant mapper: a property that is missing or not what it should be is null (or empty), never an exception, so one odd row cannot cost the answer of the others.</summary>
    internal static PhysicalDiskRow Map(IWmiRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new PhysicalDiskRow(WmiValues.Text(row["DeviceId"]), WmiValues.Text(row["SerialNumber"]), WmiValues.Int(row["HealthStatus"]), WmiValues.Ints(row["OperationalStatus"]));
    }
}
