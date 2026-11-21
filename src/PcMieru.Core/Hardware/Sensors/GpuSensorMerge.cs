using System.Globalization;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Sensors;

/// <summary>
/// Puts what the vendor API knows (VBIOS, enforced power limit, the vendor's driver version) next to the GPU facts collected from
/// Windows. The merged list is made fresh on every call from the cached GPU list and the client's latest reading; it is never cached,
/// so a SensorHost that was slow the first time does not leave "unknown" in place for the rest of the session.
/// Joins by adapter LUID first and by PCI bus/device second. Never by name: two cards of one model would be swapped.
/// </summary>
internal static class GpuSensorMerge
{
    private const string Src = "sensorhost";

    public static Value<IReadOnlyList<GpuHardware>> Apply(Value<IReadOnlyList<GpuHardware>> gpus, Value<IReadOnlyList<GpuSensorReading>> sensors)
    {
        if (gpus.Status != ValueStatus.Ok || gpus.Data is null)
        {
            return gpus;
        }

        var merged = gpus.Data.Select(g => One(g, sensors)).ToList();
        return Value.Ok<IReadOnlyList<GpuHardware>>(merged, gpus.Source);
    }

    private static GpuHardware One(GpuHardware gpu, Value<IReadOnlyList<GpuSensorReading>> sensors)
    {
        if (sensors.Status != ValueStatus.Ok || sensors.Data is null)
        {
            // The real reason, instead of the "comes from the sensor host" placeholder.
            var status = sensors.Status == ValueStatus.Ok ? ValueStatus.Error : sensors.Status;
            var detail = sensors.Detail ?? sensors.Source;
            return gpu with
            {
                Vbios = Value.Missing<string>(status, Src, detail),
                PowerLimitWatts = Value.Missing<double>(status, Src, detail),
            };
        }

        var reading = Find(gpu, sensors.Data);
        if (reading is null)
        {
            var vendor = gpu.Vendor.Data;
            var why = vendor is "NVIDIA" or "AMD"
                ? "the vendor library did not list this GPU"
                : "only NVIDIA and AMD GPUs are read through a vendor library";
            return gpu with
            {
                Vbios = Value.Unsupported<string>(Src, why),
                PowerLimitWatts = Value.Unsupported<double>(Src, why),
            };
        }

        var extras = reading.Extras;
        var driver = gpu.DriverVersion;
        var pcieGen = gpu.PcieGeneration;
        var pcieWidth = gpu.PcieWidth;
        var details = gpu.Details;
        if (extras is not null)
        {
            if (extras.DriverVersion.Status == ValueStatus.Ok)
            {
                var vendorVersion = $"{gpu.Vendor.Data ?? "vendor"} driver version {extras.DriverVersion.Data}";
                driver = driver.Status == ValueStatus.Ok
                    ? Probe.Note(driver.Data!, driver.Source, vendorVersion)
                    : Value.Ok(extras.DriverVersion.Data!, extras.DriverVersion.Source);
            }

            // Windows' own reading stays the main one (it is stable between calls); the vendor's fills in only what Windows could not give.
            if (pcieGen.Status != ValueStatus.Ok && extras.PcieGeneration.Status == ValueStatus.Ok)
            {
                pcieGen = Probe.Note(extras.PcieGeneration.Data, extras.PcieGeneration.Source, "current link; the GPU lowers it when idle");
            }

            if (pcieWidth.Status != ValueStatus.Ok && extras.PcieWidth.Status == ValueStatus.Ok)
            {
                pcieWidth = Probe.Note(extras.PcieWidth.Data, extras.PcieWidth.Source, "current link; the GPU lowers it when idle");
            }

            if (details is not null)
            {
                details = details with
                {
                    PcieMaxGeneration = details.PcieMaxGeneration.Status == ValueStatus.Ok ? details.PcieMaxGeneration : extras.PcieMaxGeneration,
                    PcieMaxWidth = details.PcieMaxWidth.Status == ValueStatus.Ok ? details.PcieMaxWidth : extras.PcieMaxWidth,
                };
            }
        }

        return gpu with
        {
            DriverVersion = driver,
            Vbios = extras?.Vbios ?? Value.Unsupported<string>(Src, "the vendor library gave no VBIOS version"),
            PowerLimitWatts = LimitWithRange(reading.PowerLimitWatts, extras),
            PcieGeneration = pcieGen,
            PcieWidth = pcieWidth,
            Details = details,
        };
    }

    private static Value<double> LimitWithRange(Value<double> limit, GpuSensorExtras? extras)
    {
        if (limit.Status != ValueStatus.Ok || extras is null)
        {
            return limit;
        }

        var parts = new List<string>();
        if (extras.PowerLimitMinWatts.Status == ValueStatus.Ok && extras.PowerLimitMaxWatts.Status == ValueStatus.Ok)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"the driver allows {extras.PowerLimitMinWatts.Data:0.#} to {extras.PowerLimitMaxWatts.Data:0.#} W"));
        }

        if (extras.PowerLimitDefaultWatts.Status == ValueStatus.Ok)
        {
            parts.Add(BoardDefaultNote(extras.PowerLimitDefaultWatts.Data));
        }

        return parts.Count == 0 ? limit : Probe.Note(limit.Data, limit.Source, "limit in force now; " + string.Join(", ", parts));
    }

    /// <summary>The words that carry the board's default power limit in the Detail of <c>GpuHardware.PowerLimitWatts</c>. Written by <see cref="BoardDefaultNote"/> and read back by <see cref="TryParseBoardDefaultWatts"/>.</summary>
    private const string BoardDefaultLead = "board default ";

    /// <summary>"board default 250 W". The typed model has no field for the default limit, so it travels in the Detail text; the parser below is its only reader.</summary>
    internal static string BoardDefaultNote(double watts) =>
        string.Create(CultureInfo.InvariantCulture, $"{BoardDefaultLead}{watts:0.#} W");

    /// <summary>
    /// Reads the number back out of a Detail written by <see cref="BoardDefaultNote"/>. False when the text has no such note, the number is
    /// not a plain non-negative decimal, or the unit is not W. A round-trip test keeps this and the writer in step.
    /// </summary>
    internal static bool TryParseBoardDefaultWatts(string? detail, out double watts)
    {
        watts = 0;
        if (detail is null)
        {
            return false;
        }

        var at = detail.IndexOf(BoardDefaultLead, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var rest = detail.AsSpan(at + BoardDefaultLead.Length);
        var end = rest.IndexOf(" W", StringComparison.Ordinal);
        if (end <= 0 || !double.TryParse(rest[..end], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return false;
        }

        watts = value;
        return true;
    }

    private static GpuSensorReading? Find(GpuHardware gpu, IReadOnlyList<GpuSensorReading> readings)
    {
        if (gpu.Luid.Status == ValueStatus.Ok && gpu.Luid.Data is { } luid)
        {
            var byLuid = readings.Where(r => string.Equals(r.Id, luid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byLuid.Count == 1)
            {
                return byLuid[0];
            }
        }

        if (gpu.Details is { } details && PciBusIds.TryParseLocation(details.PciLocation.Data, out var bus, out var device))
        {
            var byBus = readings.Where(r => PciBusIds.TryParse(r.Extras?.PciBusId, out var b, out var d) && b == bus && d == device).ToList();
            if (byBus.Count == 1)
            {
                return byBus[0];
            }
        }

        return null;
    }
}
