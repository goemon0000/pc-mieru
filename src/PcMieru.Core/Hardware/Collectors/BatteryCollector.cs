using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>
/// The laptop battery: charge, capacity now against capacity when new (wear), cycles, voltage. A PC with no battery gets one Unsupported
/// value, never a row of zeros. A UPS that shows up as a battery is not a system battery and is ignored. Nothing is written to the battery.
/// </summary>
internal static class BatteryCollector
{
    private const string PowerSrc = "kernel32:GetSystemPowerStatus";

    public static Value<BatteryHardware> Collect()
    {
        try
        {
            var power = BatteryNative.ReadPowerStatus(out var error);
            if (power is null)
            {
                return Probe.FromWin32<BatteryHardware>(error, PowerSrc, "power status");
            }

            // The scan is cheap and also finds out whether a battery-class device is a UPS rather than a laptop battery.
            List<RawBattery> batteries;
            try
            {
                batteries = BatteryNative.ReadBatteries() ?? [];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                batteries = [];
            }

            return BatteryBuilder.Build(power, batteries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<BatteryHardware>(ex, PowerSrc);
        }
    }
}

/// <summary>The pure part of the battery collector. Unit tested with hand-made readings.</summary>
internal static class BatteryBuilder
{
    private const string PowerSrc = "kernel32:GetSystemPowerStatus";
    private const string InfoSrc = "battery-ioctl:IOCTL_BATTERY_QUERY_INFORMATION";
    private const string StatusSrc = "battery-ioctl:IOCTL_BATTERY_QUERY_STATUS";
    private const byte FlagNoBattery = 128;
    private const byte Unknown = 255;
    private const uint Online = 1;
    private const uint Discharging = 2;
    private const uint Charging = 4;
    private const uint Critical = 8;

    public static Value<BatteryHardware> Build(RawPowerStatus power, IReadOnlyList<RawBattery> batteries)
    {
        var system = batteries.Where(b => (b.Capabilities & BatteryNative.CapSystemBattery) != 0).ToList();
        // 128 = "no system battery"; 255 = unknown (also has bit 128 set), which is not proof of a battery either.
        var flagSaysNone = (power.BatteryFlag & FlagNoBattery) != 0;
        if (system.Count == 0 && flagSaysNone)
        {
            return Value.Unsupported<BatteryHardware>(PowerSrc, "no battery in this PC");
        }

        var battery = system.Count > 0 ? system[0] : null;
        var noteMany = system.Count > 1 ? $"this PC has {system.Count} batteries; capacity and cycles are for the first" : null;

        var hardware = new BatteryHardware(
            power.BatteryPercent == Unknown || power.BatteryPercent > 100
                ? Value.Unsupported<int>(PowerSrc, "charge level not reported")
                : Value.Ok((int)power.BatteryPercent, PowerSrc),
            power.AcLine switch
            {
                0 => Value.Ok(false, PowerSrc),
                1 => Value.Ok(true, PowerSrc),
                _ => Value.Unsupported<bool>(PowerSrc, "power source not reported"),
            },
            Capacity(battery, battery?.DesignedCapacity, noteMany),
            Capacity(battery, battery?.FullChargedCapacity, noteMany),
            Wear(battery),
            Cycles(battery),
            new BatteryDetails(
                Chemistry(battery),
                StateText(battery, power, noteMany),
                battery?.Voltage is { } v && v != BatteryNative.Unknown && v > 0 && v < 100_000
                    ? Value.Ok((int)v, StatusSrc)
                    : Value.Unsupported<int>(StatusSrc, battery is null ? NoInterface : "voltage not reported"),
                power.LifeSeconds == BatteryNative.Unknown
                    ? Value.Unsupported<int>(PowerSrc, "only known while running on battery")
                    : Value.Ok((int)((power.LifeSeconds + 30) / 60), PowerSrc)));
        return Value.Ok(hardware, "composite:battery");
    }

    private const string NoInterface = "the battery driver gives no details to apps";

    private static Value<long> Capacity(RawBattery? battery, uint? raw, string? note)
    {
        if (battery is null || raw is null)
        {
            return Value.Unsupported<long>(InfoSrc, NoInterface);
        }

        if ((battery.Capabilities & BatteryNative.CapRelative) != 0)
        {
            return Value.Unsupported<long>(InfoSrc, "this battery reports a relative scale, not milliwatt-hours");
        }

        if (raw is 0 or BatteryNative.Unknown)
        {
            return Value.Unsupported<long>(InfoSrc, "this battery does not report its capacity");
        }

        return Probe.Note((long)raw.Value, InfoSrc, note ?? "milliwatt-hours as the battery reports them");
    }

    /// <summary>How much of the original capacity is gone: (1 - full / design) as a percentage, never below zero.</summary>
    public static Value<double> Wear(RawBattery? battery)
    {
        if (battery is null)
        {
            return Value.Unsupported<double>(InfoSrc, NoInterface);
        }

        var design = Capacity(battery, battery.DesignedCapacity, null);
        var full = Capacity(battery, battery.FullChargedCapacity, null);
        if (design.Status != ValueStatus.Ok || full.Status != ValueStatus.Ok)
        {
            return Value.Unsupported<double>(InfoSrc, "needs both the design and the full-charge capacity");
        }

        var wear = (1.0 - ((double)full.Data / design.Data)) * 100.0;
        return wear <= 0
            ? Probe.Note(0.0, InfoSrc, "full-charge capacity is at or above the design capacity")
            : Probe.Note(Math.Round(wear, 1), InfoSrc, "capacity now compared with capacity when new");
    }

    private static Value<int> Cycles(RawBattery? battery)
    {
        if (battery is null)
        {
            return Value.Unsupported<int>(InfoSrc, NoInterface);
        }

        // 0 is what many batteries send when they simply do not count cycles, so it is "not reported", not "brand new".
        return battery.CycleCount is 0 or BatteryNative.Unknown || battery.CycleCount > 100_000
            ? Value.Unsupported<int>(InfoSrc, "this battery does not report a cycle count")
            : Value.Ok((int)battery.CycleCount, InfoSrc);
    }

    private static Value<string> Chemistry(RawBattery? battery)
    {
        if (battery is null)
        {
            return Value.Unsupported<string>(InfoSrc, NoInterface);
        }

        return ChemistryName(battery.Chemistry) is { } name
            ? Value.Ok(name, InfoSrc)
            : string.IsNullOrWhiteSpace(battery.Chemistry)
                ? Value.Unsupported<string>(InfoSrc, "this battery does not report its type")
                : Value.Ok(battery.Chemistry, InfoSrc);
    }

    /// <summary>The four-letter chemistry code in BATTERY_INFORMATION as plain words. Null for a code this table does not know.</summary>
    public static string? ChemistryName(string code) => code.Trim().ToUpperInvariant() switch
    {
        "LION" => "Lithium-ion",
        "LIP" => "Lithium polymer",
        "NIMH" => "Nickel metal hydride",
        "NICD" => "Nickel cadmium",
        "PBAC" => "Lead acid",
        "ZNAR" => "Zinc air",
        "RAM" => "Rechargeable alkaline-manganese",
        _ => null,
    };

    private static Value<string> StateText(RawBattery? battery, RawPowerStatus power, string? note)
    {
        if (battery?.PowerState is not { } state)
        {
            // Fall back to the coarse flags GetSystemPowerStatus gives (8 = charging).
            var coarse = (power.BatteryFlag & 8) != 0 && power.BatteryFlag != Unknown ? "charging" : power.AcLine == 1 ? "plugged in" : power.AcLine == 0 ? "on battery" : null;
            return coarse is null
                ? Value.Unsupported<string>(PowerSrc, "charging state not reported")
                : Value.Ok(coarse, PowerSrc);
        }

        var text = StateName(state);
        return note is null ? Value.Ok(text, StatusSrc) : Probe.Note(text, StatusSrc, note);
    }

    /// <summary>BATTERY_STATUS power-state flags as words.</summary>
    public static string StateName(uint state)
    {
        var text = (state & Charging) != 0
            ? "charging"
            : (state & Discharging) != 0
                ? "discharging"
                : (state & Online) != 0
                    ? "plugged in, not charging"
                    : "idle";
        return (state & Critical) != 0 ? text + " (critical)" : text;
    }
}
