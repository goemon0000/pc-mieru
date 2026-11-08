// Adapted from goemon0000/parts-life (MIT) src/PartsLife.Checks/Storage/{NvmeHealth,AtaSmart}.cs
// Layouts follow NVMe Base Specification (SMART / Health Information log, 0x02) and the ATA SMART attribute table.

using System.Buffers.Binary;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Hardware.Storage;

/// <summary>
/// Pure parsers from raw device bytes to <see cref="StorageHealth"/>. The Helper (elevated process) returns the raw bytes
/// and the app parses them here, so a short or empty buffer is reported as an error and never as "0 errors".
/// </summary>
public static class StorageHealthParsers
{
    public const int NvmeHealthLogLength = 512;
    public const int SmartPageLength = 512;
    private const int SmartSlots = 30;
    private const int SmartStride = 12;
    private const string NvmeSource = "nvme:log-page-0x02";
    private const string SmartSource = "ata:smart-read-data";

    /// <summary>Bytes per NVMe "data unit" (1000 x 512).</summary>
    public const uint NvmeDataUnitBytes = 512_000;

    private static U128 Read128(ReadOnlySpan<byte> page, int offset) =>
        new(new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset + 8, 8)), BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(offset, 8))));

    private static Value<U128> Counter(ReadOnlySpan<byte> page, int offset) => Value.Ok(Read128(page, offset), NvmeSource);

    private static Value<U128> Bytes(ReadOnlySpan<byte> page, int offset)
    {
        var units = Read128(page, offset).Value;
        try
        {
            return Value.Ok(new U128(checked(units * NvmeDataUnitBytes)), NvmeSource + " x 512000");
        }
        catch (OverflowException)
        {
            return Value.Failed<U128>(NvmeSource, "byte count does not fit in 128 bits");
        }
    }

    /// <summary>NVMe SMART / Health Information log page (512 bytes). Short input is an error value, not zeros.</summary>
    public static Value<StorageHealth> FromNvmeLogPage(ReadOnlySpan<byte> page)
    {
        if (page.Length < NvmeHealthLogLength)
        {
            return Value.Failed<StorageHealth>(NvmeSource, $"health log must be {NvmeHealthLogLength} bytes, got {page.Length}");
        }

        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(1, 2));
        var sensors = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            var k = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(200 + (i * 2), 2));
            if (k != 0)
            {
                sensors.Add(Math.Round(k - 273.15, 2));
            }
        }

        var warning = page[0];
        var warnings = new List<string>();
        if ((warning & 0x01) != 0) { warnings.Add("availableSpareLow"); }
        if ((warning & 0x02) != 0) { warnings.Add("temperatureOutOfRange"); }
        if ((warning & 0x04) != 0) { warnings.Add("reliabilityDegraded"); }
        if ((warning & 0x08) != 0) { warnings.Add("readOnly"); }
        if ((warning & 0x10) != 0) { warnings.Add("volatileBackupFailed"); }
        if ((warning & 0x20) != 0) { warnings.Add("persistentMemoryReadOnly"); }
        if ((warning & 0xC0) != 0) { warnings.Add("reservedBits"); }

        var health = new StorageHealth(
            StorageHealthKind.Nvme,
            TemperatureC: kelvin == 0 ? Value.Unsupported<double>(NvmeSource, "drive reported 0 K (no temperature)") : Value.Ok(Math.Round(kelvin - 273.15, 2), NvmeSource + " composite temperature"),
            PercentageUsed: Value.Ok((double)page[5], NvmeSource + " (vendor estimate, may exceed 100)"),
            AvailableSparePercent: Value.Ok((double)page[3], NvmeSource),
            AvailableSpareThresholdPercent: Value.Ok((double)page[4], NvmeSource),
            CriticalWarning: Value.Ok((int)warning, NvmeSource),
            DataWrittenBytes: Bytes(page, 48),
            DataReadBytes: Bytes(page, 32),
            PowerOnHours: Counter(page, 128),
            PowerCycles: Counter(page, 112),
            UnsafeShutdowns: Counter(page, 144),
            MediaErrors: Counter(page, 160),
            Attributes: [],
            Nvme: new NvmeHealthDetails(
                HostReadCommands: Counter(page, 64),
                HostWriteCommands: Counter(page, 80),
                ControllerBusyTimeMinutes: Counter(page, 96),
                ErrorLogEntries: Counter(page, 176),
                WarningCompositeTempMinutes: Value.Ok((long)BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(192, 4)), NvmeSource),
                CriticalCompositeTempMinutes: Value.Ok((long)BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(196, 4)), NvmeSource),
                TemperatureSensorsC: Value.Ok<IReadOnlyList<double>>(sensors, NvmeSource),
                CriticalWarningFlags: Value.Ok<IReadOnlyList<string>>(warnings, NvmeSource)));
        return Value.Ok(health, NvmeSource);
    }

    /// <summary>Reads the 12-byte attribute slots. Thresholds are optional (a second 512-byte page).</summary>
    public static IReadOnlyList<SmartAttribute> ParseSmartAttributes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds)
    {
        var list = new List<SmartAttribute>();
        if (data.Length < SmartPageLength)
        {
            return list;
        }

        var limits = new Dictionary<int, int>();
        if (thresholds.Length >= SmartPageLength)
        {
            for (var i = 0; i < SmartSlots; i++)
            {
                var t = thresholds.Slice(2 + (i * SmartStride), SmartStride);
                if (t[0] != 0)
                {
                    limits[t[0]] = t[1];
                }
            }
        }

        for (var i = 0; i < SmartSlots; i++)
        {
            var a = data.Slice(2 + (i * SmartStride), SmartStride);
            int id = a[0];
            if (id == 0)
            {
                continue;
            }

            ulong raw = 0;
            for (var b = 5; b >= 0; b--)
            {
                raw = (raw << 8) | a[5 + b];
            }

            list.Add(new SmartAttribute(id, SmartName(id), a[3], a[4], limits.GetValueOrDefault(id), new U128(raw), RawUnitIsStandard(id)));
        }

        return list;
    }

    /// <summary>
    /// SATA SMART from the two raw tables (data, then optionally thresholds). The Helper may return them concatenated
    /// (1024 bytes) or data only (512 bytes).
    /// </summary>
    public static Value<StorageHealth> FromSmartTables(ReadOnlySpan<byte> data, ReadOnlySpan<byte> thresholds)
    {
        if (data.Length < SmartPageLength)
        {
            return Value.Failed<StorageHealth>(SmartSource, $"SMART table must be {SmartPageLength} bytes, got {data.Length}");
        }

        if (data.IndexOfAnyExcept((byte)0) < 0)
        {
            return Value.Failed<StorageHealth>(SmartSource, "SMART table is all zeros (a bridge that does not pass SMART through)");
        }

        var attrs = ParseSmartAttributes(data, thresholds);
        SmartAttribute? Find(params int[] ids) => ids.Select(id => attrs.FirstOrDefault(a => a.Id == id)).FirstOrDefault(a => a is not null);

        Value<U128> RawOf(SmartAttribute? a, ulong mask, string what) =>
            a is null
                ? Value.Unsupported<U128>(SmartSource, "drive does not report " + what)
                : Value.Ok(new U128(a.Raw.Value & mask), $"{SmartSource} attribute {a.Id}");

        var temp = Find(194, 190);
        var health = new StorageHealth(
            StorageHealthKind.Sata,
            TemperatureC: temp is null ? Value.Unsupported<double>(SmartSource, "drive does not report temperature") : Value.Ok((double)(ulong)(temp.Raw.Value & 0xFF), $"{SmartSource} attribute {temp.Id} (low byte)"),
            PercentageUsed: Value.Unsupported<double>(SmartSource, "no standard SMART attribute for wear; see the attribute list"),
            AvailableSparePercent: Value.Unsupported<double>(SmartSource, "no standard SMART attribute for spare area"),
            AvailableSpareThresholdPercent: Value.Unsupported<double>(SmartSource, "no standard SMART attribute for spare area"),
            CriticalWarning: Value.Unsupported<int>(SmartSource, "NVMe-only field"),
            DataWrittenBytes: Value.Unsupported<U128>(SmartSource, "the unit of the written-data attribute differs per vendor"),
            DataReadBytes: Value.Unsupported<U128>(SmartSource, "the unit of the read-data attribute differs per vendor"),
            PowerOnHours: RawOf(Find(9), 0xFFFFFFFF, "power-on time"),
            PowerCycles: RawOf(Find(12), 0xFFFFFFFFFFFF, "power cycles"),
            UnsafeShutdowns: RawOf(Find(174, 192), 0xFFFFFFFFFFFF, "unsafe shutdowns"),
            MediaErrors: RawOf(Find(187, 198), 0xFFFFFFFFFFFF, "uncorrectable errors"),
            Attributes: attrs);
        return Value.Ok(health, SmartSource);
    }

    /// <summary>Helper blob: 512 bytes of SMART data, optionally followed by 512 bytes of thresholds.</summary>
    public static Value<StorageHealth> FromSmartBlob(ReadOnlySpan<byte> blob) =>
        blob.Length >= 2 * SmartPageLength
            ? FromSmartTables(blob[..SmartPageLength], blob.Slice(SmartPageLength, SmartPageLength))
            : FromSmartTables(blob, default);

    /// <summary>The attributes whose raw value means the same thing on every vendor.</summary>
    private static bool RawUnitIsStandard(int id) => id is 5 or 10 or 12 or 187 or 196 or 197 or 198 or 199;

    private static string SmartName(int id) => id switch
    {
        1 => "Read error rate",
        3 => "Spin-up time",
        4 => "Start/stop count",
        5 => "Reallocated sectors",
        7 => "Seek error rate",
        9 => "Power-on time",
        10 => "Spin retry count",
        12 => "Power cycle count",
        171 => "Program fail count",
        172 => "Erase fail count",
        173 => "Wear leveling count",
        174 => "Unexpected power loss",
        177 => "Wear range delta",
        181 => "Program fail total",
        182 => "Erase fail total",
        183 => "SATA downshift",
        184 => "End-to-end error",
        187 => "Reported uncorrectable errors",
        188 => "Command timeout",
        190 => "Airflow temperature",
        191 => "G-sense error rate",
        192 => "Power-off retract count",
        193 => "Load cycle count",
        194 => "Temperature",
        195 => "Hardware ECC recovered",
        196 => "Reallocation events",
        197 => "Current pending sectors",
        198 => "Offline uncorrectable sectors",
        199 => "UDMA CRC errors",
        200 => "Write error rate",
        231 => "SSD life left",
        232 => "Available reserved space",
        233 => "Media wearout indicator",
        241 => "Total LBAs written",
        242 => "Total LBAs read",
        _ => $"Attribute {id}",
    };
}
