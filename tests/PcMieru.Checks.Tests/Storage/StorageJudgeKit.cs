using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// Builders for the judge fixtures: a drive of either protocol with any one value changed, a baseline outcome written by hand (so a test
/// states "compared, 3 before, 5 after" without a run), and the rows of one disk through the strict validator. The expected verdicts in the
/// tests are written from DESIGN-A §14.4, never computed with the code under test.
/// </summary>
internal static class J
{
    public static readonly DateTimeOffset Since = new(2026, 11, 19, 8, 30, 0, 456, TimeSpan.Zero);

    public static readonly CheckSubject Subject = new("disk-1", "Test Disk");

    public static Value<T> Ok<T>(T value) => Value.Ok(value, "test");

    public static Value<U128> Wide(ulong value) => Value.Ok(new U128(value), "test");

    // ---- drives ----

    public static StorageInspectionDrive Edit(this StorageInspectionDrive drive, Func<StorageHealth, StorageHealth> change) =>
        drive with { Health = Ok(change(drive.Health.Data!)) };

    public static StorageInspectionDrive EditCounters(this StorageInspectionDrive drive, Func<StorageInspectionCounters, StorageInspectionCounters> change) =>
        drive with { Counters = change(drive.Counters!) };

    /// <summary>
    /// A healthy NVMe drive with any of the values the judge reads changed. The temperature is whole degrees (as the screens' 41 °C): the
    /// page's Kelvin is that plus 273 and the shown value that minus the 0.15 (as Core reads it), unless <paramref name="kelvin"/> sets the
    /// raw Kelvin itself (then the shown value is <c>kelvin - 273.15</c> rounded to two places, exactly as <c>HealthParsers</c> does).
    /// </summary>
    public static StorageInspectionDrive Nvme(
        double temperature = 41,
        double used = 3,
        double spare = 100,
        double threshold = 10,
        int critical = 0,
        ulong media = 0,
        ulong errorLog = 0,
        int? kelvin = null)
    {
        var raw = kelvin ?? (int)Math.Round(temperature + 273);
        var shown = kelvin is { } k ? Math.Round(k - 273.15, 2) : temperature;
        return Drives.Nvme(used: used, spare: spare, critical: critical, media: media, errorLog: errorLog)
            .Edit(h => h with { TemperatureC = Ok(shown), AvailableSpareThresholdPercent = Ok(threshold) })
            .EditCounters(c => c with { NvmeCompositeKelvin = raw });
    }

    public static SmartAttribute Attr(int id, ulong raw, int current = 100, int worst = 100, int threshold = 0) =>
        new(id, "attribute " + id, current, worst, threshold, new U128(raw), true);

    /// <summary>A SATA drive with exactly these attributes (and a threshold table read, unless said otherwise).</summary>
    public static StorageInspectionDrive Ata(IReadOnlyList<SmartAttribute> attributes, bool? predictFailure = null, bool thresholdsRead = true) =>
        Drives.Sata().Edit(h => h with { Attributes = attributes }) with
        {
            Counters = new StorageInspectionCounters(StorageHealthKind.Sata, null, null, null, false, [], predictFailure, thresholdsRead),
        };

    /// <summary>A healthy SATA drive: temperature 33 and the attributes that matter at 0.</summary>
    public static StorageInspectionDrive HealthyAta(params SmartAttribute[] more)
    {
        SmartAttribute[] basis = [Attr(5, 0), Attr(9, 1000), Attr(194, 33), Attr(197, 0), Attr(199, 0)];
        var ids = more.Select(static a => a.Id).ToHashSet();
        return Ata([.. basis.Where(a => !ids.Contains(a.Id)), .. more]);
    }

    /// <summary>The same drive with a different attribute set: replaces the ones of these IDs.</summary>
    public static StorageInspectionDrive WithAttributes(StorageInspectionDrive drive, params SmartAttribute[] attributes)
    {
        var ids = attributes.Select(static a => a.Id).ToHashSet();
        return drive.Edit(h => h with { Attributes = [.. h.Attributes.Where(a => !ids.Contains(a.Id)), .. attributes] });
    }

    // ---- baseline outcomes ----

    /// <summary>A disk the baseline could not compare (the first run): no counters listed, so no ".delta" rows.</summary>
    public static BaselineDiskOutcome First(int index = 0) =>
        new(index, "disk-1", BaselineDiskState.NotComparable, CheckEvidenceCode.BaselineFirst, []);

    /// <summary>One compared counter. The critical warning's new bits are those set after and not before (as the engine computes them).</summary>
    public static BaselineCounterOutcome Counter(string name, ulong before, ulong after) =>
        new("storage." + name, null, before, after, Since, name == "nvme.critical_warning" ? (int)(after & ~before) : null);

    /// <summary>A counter the baseline lists but could not compare (the disk-wide code, or baseline_missing_counter).</summary>
    public static BaselineCounterOutcome Uncompared(string name, CheckEvidenceCode code) =>
        new("storage." + name, code, null, null, null, null);

    /// <summary>A disk compared with a previous record: these counters were compared.</summary>
    public static BaselineDiskOutcome Compared(params BaselineCounterOutcome[] counters) =>
        new(0, "disk-1", BaselineDiskState.Compared, null, counters);

    // ---- rows ----

    /// <summary>The rows of the drive by ID. They are also written as one result and must pass the strict validator.</summary>
    public static Dictionary<string, CheckItem> Rows(StorageInspectionDrive drive, BaselineDiskOutcome? baseline = null)
    {
        var rows = StorageJudge.Rows(drive, baseline ?? First(), Subject);
        AssertValid(rows);
        return rows.ToDictionary(static r => r.Id, StringComparer.Ordinal);
    }

    public static void AssertValid(IReadOnlyList<CheckItem> rows)
    {
        List<CheckItem> items =
        [
            CheckItem.Create("storage.devices", CheckValue.Int(1), ItemVerdict.Info, CheckSource.StorageQuery),
            .. rows,
        ];
        var result = CheckResult.Create(Samples.App, Samples.StorageRun, CheckKind.Storage, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, items);
        var problems = CheckResultValidator.Validate(result);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    /// <summary>The overall verdict of these rows as one result (the §3 ranking).</summary>
    public static CheckVerdict Overall(params IEnumerable<CheckItem>[] disks) =>
        CheckVerdicts.Overall(disks.SelectMany(static d => d).ToList());

    public static void Is(ItemVerdict verdict, CheckItem row) => Assert.Equal(verdict, row.Verdict);

    public static void IsNumber(double expected, CheckItem row) => Assert.Equal(CheckValue.Number(expected), row.Value);

    public static void IsInt(long expected, CheckItem row) => Assert.Equal(CheckValue.Int(expected), row.Value);

    public static void IsDec(ulong expected, CheckItem row) => Assert.Equal(CheckValue.Dec(expected), row.Value);

    /// <summary>No value, the reason, and the verdict "unavailable".</summary>
    public static void IsMissing(CheckReason reason, CheckItem row, ItemVerdict verdict = ItemVerdict.Unavailable)
    {
        Assert.Null(row.Value);
        Assert.Equal(reason, row.Reason);
        Assert.Equal(verdict, row.Verdict);
    }
}
