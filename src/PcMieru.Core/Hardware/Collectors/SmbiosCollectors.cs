using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Smbios;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>Reads the raw SMBIOS table once (GetSystemFirmwareTable 'RSMB'). No administrator rights needed.</summary>
internal static class SmbiosReader
{
    public static SmbiosTable? Read(out string? error)
    {
        try
        {
            var size = Kernel32.GetSystemFirmwareTable(SmbiosTable.RsmbSignature, 0, null, 0);
            if (size == 0)
            {
                error = $"GetSystemFirmwareTable(RSMB): Win32 error {Marshal.GetLastWin32Error()}";
                return null;
            }

            var buffer = new byte[size];
            var written = Kernel32.GetSystemFirmwareTable(SmbiosTable.RsmbSignature, 0, buffer, size);
            if (written == 0 || written > size)
            {
                error = $"GetSystemFirmwareTable(RSMB): Win32 error {Marshal.GetLastWin32Error()}";
                return null;
            }

            var table = SmbiosTable.ParseRaw(buffer.AsSpan(0, (int)written));
            error = table is null ? "SMBIOS data was too short" : null;
            return table;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name;
            return null;
        }
    }
}

internal static class MemoryCollector
{
    public static Value<MemoryHardware> Collect(SmbiosTable? smbios, string? smbiosError)
    {
        try
        {
            var devices = smbios?.OfType(17).ToList() ?? [];
            var arrays = smbios?.OfType(16).ToList() ?? [];
            var smbiosDetail = smbiosError ?? "no memory structures in SMBIOS";
            const string s17 = "smbios:type17";
            const string s16 = "smbios:type16";

            Value<long> total;
            if (Kernel32.GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0)
            {
                total = Value.Ok((long)kb * 1024, "winapi:GetPhysicallyInstalledSystemMemory");
            }
            else
            {
                var status = new Kernel32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Kernel32.MemoryStatusEx>() };
                total = Kernel32.GlobalMemoryStatusEx(ref status)
                    ? Value.Ok((long)status.TotalPhys, "winapi:GlobalMemoryStatusEx (OS-visible, may be below installed)")
                    : Probe.FromWin32<long>(Marshal.GetLastWin32Error(), "winapi:GlobalMemoryStatusEx", "memory status");
            }

            Value<long> available;
            var ms = new Kernel32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Kernel32.MemoryStatusEx>() };
            available = Kernel32.GlobalMemoryStatusEx(ref ms)
                ? Value.Ok((long)ms.AvailPhys, "winapi:GlobalMemoryStatusEx")
                : Probe.FromWin32<long>(Marshal.GetLastWin32Error(), "winapi:GlobalMemoryStatusEx", "memory status");

            var modules = devices.Select(Module).ToList();
            var installed = devices.Count(d => InstalledBytes(d) is > 0);

            Value<int> slotsTotal = devices.Count > 0 ? Value.Ok(devices.Count, s17) : Value.Unsupported<int>(s17, smbiosDetail);
            Value<int> slotsUsed = devices.Count > 0 ? Value.Ok(installed, s17) : Value.Unsupported<int>(s17, smbiosDetail);

            Value<long> maxCapacity = Value.Unsupported<long>(s16, smbiosDetail);
            Value<string> ecc = Value.Unsupported<string>(s16, smbiosDetail);
            Value<string> location = Value.Unsupported<string>(s16, smbiosDetail);
            if (arrays.Count > 0)
            {
                var a = arrays[0];
                var max = a.U32(0x07);
                long? bytes = max is null ? null : max.Value == 0x80000000 ? (long?)a.U64(0x0F) : max.Value * 1024;
                maxCapacity = bytes is > 0 ? Value.Ok(bytes.Value, s16) : Value.Unsupported<long>(s16, "not reported");
                ecc = a.U8(0x06) is { } e ? Value.Ok(EccName(e), s16) : Value.Unsupported<string>(s16, "not reported");
                location = a.U8(0x04) is { } l ? Value.Ok(ArrayLocation(l), s16) : Value.Unsupported<string>(s16, "not reported");
            }

            var memory = new MemoryHardware(
                total,
                slotsTotal,
                slotsUsed,
                maxCapacity,
                modules,
                new MemoryDetails(
                    available,
                    ecc,
                    location,
                    arrays.Count > 0 ? Value.Ok(arrays.Count, s16) : Value.Unsupported<int>(s16, smbiosDetail)));
            return Value.Ok(memory, "composite:memory");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<MemoryHardware>(ex, "composite:memory");
        }
    }

    /// <summary>Installed size in bytes: 0 = empty slot, null = unknown.</summary>
    internal static long? InstalledBytes(SmbiosStructure d)
    {
        var w = d.U16(0x0C);
        if (w is null || w == 0xFFFF)
        {
            return null;
        }

        if (w == 0)
        {
            return 0;
        }

        if (w == 0x7FFF)
        {
            return d.U32(0x1C) is { } mb ? (mb & 0x7FFFFFFF) * 1024L * 1024L : null;
        }

        return (w.Value & 0x8000) != 0 ? (w.Value & 0x7FFF) * 1024L : (w.Value & 0x7FFF) * 1024L * 1024L;
    }

    private static MemoryModule Module(SmbiosStructure d)
    {
        const string s = "smbios:type17";
        var size = InstalledBytes(d);
        var empty = size == 0;
        const string emptyDetail = "slot is empty";

        Value<T> Skip<T>(Func<Value<T>> read) => empty ? Value.Unsupported<T>(s, emptyDetail) : read();

        var slotName = d.Str(0x10) ?? $"Slot {d.Handle:X}";

        Value<int> Speed(int offset, int extOffset)
        {
            var v = d.U16(offset);
            if (v == 0xFFFF)
            {
                v = d.U32(extOffset) is { } ext and > 0 and < 100000 ? (int)ext : null;
            }

            return v is null or 0 ? Value.Unsupported<int>(s, "not reported") : Value.Ok(v.Value, s);
        }

        Value<int> Mv(int offset) => d.U16(offset) is { } mv and > 0 ? Value.Ok(mv, s) : Value.Unsupported<int>(s, "not reported");

        var configuredV = Mv(0x26);
        var nominalV = Mv(0x22);

        return new MemoryModule(
            Slot: Value.Ok(slotName, s),
            CapacityBytes: size is { } b ? Value.Ok(b, s) : Value.Unsupported<long>(s, "size unknown"),
            Type: Skip(() => Type(d)),
            SpeedMtPerSec: Skip(() => Speed(0x15, 0x54)),
            ConfiguredSpeedMtPerSec: Skip(() => Speed(0x20, 0x58)),
            Manufacturer: Skip(() => Probe.FirmwareText(s, d.Str(0x17))),
            PartNumber: Skip(() => Probe.FirmwareText(s, d.Str(0x1A))),
            SerialNumber: Skip(() => Probe.FirmwareText(s, d.Str(0x18))),
            Ranks: Skip(() => d.U8(0x1B) is { } r && (r & 0x0F) != 0 ? Value.Ok(r & 0x0F, s) : Value.Unsupported<int>(s, "not reported")),
            VoltageMillivolts: Skip(() => configuredV.IsOk ? configuredV : nominalV),
            FormFactor: Skip(() => d.U8(0x0E) is { } f ? Value.Ok(FormFactor(f), s) : Value.Unsupported<string>(s, "not reported")),
            Details: new MemoryModuleDetails(
                BankLocator: Skip(() => Probe.Text(s, d.Str(0x11))),
                DataWidthBits: Skip(() => d.U16(0x0A) is { } dw and > 0 and < 0xFFFF ? Value.Ok(dw, s) : Value.Unsupported<int>(s, "not reported")),
                TotalWidthBits: Skip(() => d.U16(0x08) is { } tw and > 0 and < 0xFFFF ? Value.Ok(tw, s) : Value.Unsupported<int>(s, "not reported")),
                MinVoltageMillivolts: Skip(() => nominalV),
                MaxVoltageMillivolts: Skip(() => Mv(0x24)),
                ConfiguredVoltageMillivolts: Skip(() => configuredV),
                MemoryTechnology: Skip(() => d.U8(0x28) is { } t ? Value.Ok(Technology(t), s) : Value.Unsupported<string>(s, "not reported"))));
    }

    private static Value<MemoryType> Type(SmbiosStructure d)
    {
        const string s = "smbios:type17";
        if (d.U8(0x12) is not { } t)
        {
            return Value.Unsupported<MemoryType>(s, "not reported");
        }

        return t switch
        {
            0x18 => Value.Ok(MemoryType.Ddr3, s),
            0x1A => Value.Ok(MemoryType.Ddr4, s),
            0x22 => Value.Ok(MemoryType.Ddr5, s),
            0x1E => Value.Ok(MemoryType.Lpddr4, s),
            0x23 => Value.Ok(MemoryType.Lpddr5, s),
            0x00 or 0x02 => Value.Ok(MemoryType.Unknown, s),
            _ => new Value<MemoryType>(MemoryType.Other, ValueStatus.Ok, s, "SMBIOS memory type 0x" + t.ToString("X2", CultureInfo.InvariantCulture)),
        };
    }

    private static string FormFactor(int f) => f switch
    {
        0x03 => "SIMM",
        0x09 => "DIMM",
        0x0B => "Row of chips",
        0x0D => "SODIMM",
        0x0F => "FB-DIMM",
        0x10 => "Die",
        0x01 => "Other",
        0x02 => "Unknown",
        _ => "Other (0x" + f.ToString("X2", CultureInfo.InvariantCulture) + ")",
    };

    private static string Technology(int t) => t switch
    {
        3 => "DRAM",
        4 => "NVDIMM-N",
        5 => "NVDIMM-F",
        6 => "NVDIMM-P",
        7 => "Intel Optane",
        1 => "Other",
        2 => "Unknown",
        _ => "Other (" + t.ToString(CultureInfo.InvariantCulture) + ")",
    };

    private static string EccName(int e) => e switch
    {
        3 => "None",
        4 => "Parity",
        5 => "Single-bit ECC",
        6 => "Multi-bit ECC",
        7 => "CRC",
        1 => "Other",
        2 => "Unknown",
        _ => "Other (" + e.ToString(CultureInfo.InvariantCulture) + ")",
    };

    private static string ArrayLocation(int l) => l switch
    {
        3 => "System board",
        1 => "Other",
        2 => "Unknown",
        4 => "ISA add-on card",
        5 => "EISA add-on card",
        6 => "PCI add-on card",
        7 => "MCA add-on card",
        8 => "PCMCIA add-on card",
        9 => "Proprietary add-on card",
        10 => "NuBus",
        _ => "Other (" + l.ToString(CultureInfo.InvariantCulture) + ")",
    };
}

internal static class BoardCollector
{
    public static Value<BoardHardware> Collect(SmbiosTable? smbios, string? smbiosError)
    {
        try
        {
            var bios = smbios?.First(0);
            var system = smbios?.First(1);
            var board = smbios?.First(2);
            var chassis = smbios?.First(3);

            Value<string> Pick(SmbiosStructure? st, string source, int offset, bool firmwareText = true)
            {
                if (st is null)
                {
                    return Value.Unsupported<string>(source, smbiosError ?? "structure not present in SMBIOS");
                }

                return firmwareText ? Probe.FirmwareText(source, st.Str(offset)) : Probe.Text(source, st.Str(offset));
            }

            string? biosRelease = null;
            if (bios?.U8(0x14) is { } major and not 0xFF && bios.U8(0x15) is { } minor and not 0xFF)
            {
                biosRelease = $"{major}.{minor}";
            }

            Value<long> romSize = Value.Unsupported<long>("smbios:type0", smbiosError ?? "not reported");
            if (bios?.U8(0x09) is { } rom)
            {
                if (rom != 0xFF)
                {
                    romSize = Value.Ok((rom + 1L) * 64 * 1024, "smbios:type0");
                }
                else if (bios.U16(0x18) is { } ext)
                {
                    var unitGb = ((ext >> 14) & 3) == 1;
                    romSize = Value.Ok((ext & 0x3FFFL) * (unitGb ? 1024L * 1024 * 1024 : 1024L * 1024), "smbios:type0");
                }
            }

            var details = new BoardDetails(
                new SystemIdentity(
                    Pick(system, "smbios:type1", 0x04),
                    Pick(system, "smbios:type1", 0x05),
                    Pick(system, "smbios:type1", 0x1A),
                    Pick(system, "smbios:type1", 0x19),
                    Pick(system, "smbios:type1", 0x06),
                    Pick(system, "smbios:type1", 0x07)),
                new ChassisIdentity(
                    chassis?.U8(0x05) is { } ct ? Value.Ok(ChassisType(ct & 0x7F), "smbios:type3") : Value.Unsupported<string>("smbios:type3", smbiosError ?? "not reported"),
                    Pick(chassis, "smbios:type3", 0x04),
                    Pick(chassis, "smbios:type3", 0x06),
                    Pick(chassis, "smbios:type3", 0x07)),
                smbios is null ? Value.Failed<string>("smbios:header", smbiosError) : Value.Ok(smbios.VersionText, "smbios:header"),
                romSize,
                biosRelease is null ? Value.Unsupported<string>("smbios:type0", smbiosError ?? "not reported") : Value.Ok(biosRelease, "smbios:type0"));

            var value = new BoardHardware(
                Manufacturer: Pick(board, "smbios:type2", 0x04),
                Product: Pick(board, "smbios:type2", 0x05),
                Revision: Pick(board, "smbios:type2", 0x06),
                SerialNumber: Pick(board, "smbios:type2", 0x07),
                BiosVendor: Pick(bios, "smbios:type0", 0x04, firmwareText: false),
                BiosVersion: Pick(bios, "smbios:type0", 0x05, firmwareText: false),
                BiosDate: Pick(bios, "smbios:type0", 0x08, firmwareText: false),
                UefiBoot: UefiBoot(),
                SecureBoot: SecureBoot(),
                TpmPresent: Tpm(out var tpmVersion),
                TpmVersion: tpmVersion,
                Details: details);
            return Value.Ok(value, "composite:board");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<BoardHardware>(ex, "composite:board");
        }
    }

    private static Value<bool> UefiBoot()
    {
        const string source = "winapi:GetFirmwareType";
        if (!Kernel32.GetFirmwareType(out var type))
        {
            return Probe.FromWin32<bool>(Marshal.GetLastWin32Error(), source, "firmware type");
        }

        return type switch
        {
            1 => Value.Ok(false, source),
            2 => Value.Ok(true, source),
            _ => Value.Unsupported<bool>(source, "firmware type unknown"),
        };
    }

    /// <summary>The registry value Windows keeps for this; the API route (GetFirmwareEnvironmentVariable) needs privileges.</summary>
    private static Value<bool> SecureBoot()
    {
        const string source = @"registry:SecureBoot\State\UEFISecureBootEnabled";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int v)
            {
                return Value.Ok(v != 0, source);
            }

            return Value.Unsupported<bool>(source, "not reported (legacy BIOS boot, or the platform has no Secure Boot)");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<bool>(ex, source);
        }
    }

    private static Value<bool> Tpm(out Value<string> version)
    {
        const string source = "tbs:Tbsi_GetDeviceInfo";
        try
        {
            var rc = Tbs.Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<Tbs.TpmDeviceInfo>(), out var info);
            if (rc == 0)
            {
                version = info.TpmVersion switch
                {
                    1 => Value.Ok("1.2", source),
                    2 => Value.Ok("2.0", source),
                    _ => Value.Unsupported<string>(source, "unknown TPM version " + info.TpmVersion.ToString(CultureInfo.InvariantCulture)),
                };
                return Value.Ok(true, source);
            }

            if (rc == Tbs.TbsETpmNotFound)
            {
                version = Value.Unsupported<string>(source, "no TPM");
                return Value.Ok(false, source);
            }

            version = Value.Failed<string>(source, "TBS result 0x" + rc.ToString("X8", CultureInfo.InvariantCulture));
            return Value.Failed<bool>(source, "TBS result 0x" + rc.ToString("X8", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            version = Probe.FromException<string>(ex, source);
            return Probe.FromException<bool>(ex, source);
        }
    }

    private static string ChassisType(int t) => t switch
    {
        3 => "Desktop",
        4 => "Low profile desktop",
        5 => "Pizza box",
        6 => "Mini tower",
        7 => "Tower",
        8 => "Portable",
        9 => "Laptop",
        10 => "Notebook",
        11 => "Hand held",
        13 => "All in one",
        14 => "Sub notebook",
        15 => "Space-saving",
        16 => "Lunch box",
        17 => "Main server chassis",
        23 => "Rack mount",
        24 => "Sealed-case PC",
        30 => "Tablet",
        31 => "Convertible",
        32 => "Detachable",
        33 => "IoT gateway",
        34 => "Embedded PC",
        35 => "Mini PC",
        36 => "Stick PC",
        1 => "Other",
        2 => "Unknown",
        _ => "Other (" + t.ToString(CultureInfo.InvariantCulture) + ")",
    };
}
