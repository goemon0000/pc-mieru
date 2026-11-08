using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Smbios;

namespace PcMieru.Core.Hardware.Collectors;

internal static class CpuCollector
{
    private const string CpuidSource = "cpuid";
    private const string TopologySource = "winapi:GetLogicalProcessorInformationEx";
    private const string SmbiosSource = "smbios:type4";

    public static Value<CpuHardware> Collect(SmbiosTable? smbios)
    {
        try
        {
            var topology = SafeTopology(out var topologyError);
            var type4 = smbios?.First(4);

            Value<T> Topo<T>(Func<CpuTopology, T> pick, string detail)
                where T : struct =>
                topology is null
                    ? Value.Failed<T>(TopologySource, topologyError ?? detail)
                    : Value.Ok(pick(topology), TopologySource);

            Value<int> Hybrid(Func<CpuTopology, int> pick) =>
                topology is null
                    ? Value.Failed<int>(TopologySource, topologyError)
                    : topology.IsHybrid
                        ? Value.Ok(pick(topology), TopologySource)
                        : Value.Unsupported<int>(TopologySource, "not a hybrid CPU: every core reports the same efficiency class");

            Value<long> Cache(int level) =>
                topology is null
                    ? Value.Failed<long>(TopologySource, topologyError)
                    : topology.Caches.Any(c => c.Level == level)
                        ? Value.Ok(topology.TotalCacheBytes(level), TopologySource)
                        : Value.Unsupported<long>(TopologySource, "no such cache level reported");

            var cpu = new CpuHardware(
                Name: BrandString(),
                Vendor: Vendor(),
                Family: Cpuid(static i => i.Family),
                Model: Cpuid(static i => i.Model),
                Stepping: Cpuid(static i => i.Stepping),
                PhysicalCores: Topo(static t => t.PhysicalCores, "topology unavailable"),
                LogicalProcessors: Topo(static t => t.LogicalProcessors, "topology unavailable"),
                PerformanceCores: Hybrid(static t => t.PerformanceCores),
                EfficiencyCores: Hybrid(static t => t.EfficiencyCores),
                BaseClockMhz: BaseClock(),
                Socket: Probe.Text(SmbiosSource, type4?.Str(0x04), smbios is null ? "SMBIOS not readable" : "not reported"),
                L1CacheBytes: Cache(1),
                L2CacheBytes: Cache(2),
                L3CacheBytes: Cache(3),
                InstructionSets: InstructionSets(),
                Details: new CpuDetails(
                    MaxClockMhz: SmbiosMhz(type4, 0x14, "firmware-declared maximum speed", smbios is null),
                    CurrentClockMhz: SmbiosMhz(type4, 0x16, "firmware-declared current speed", smbios is null),
                    ThreadsPerCore: Topo(static t => t.ThreadsPerCore, "topology unavailable"),
                    NumaNodes: Topo(static t => t.NumaNodes, "topology unavailable"),
                    ProcessorGroups: Topo(static t => t.ProcessorGroups, "topology unavailable"),
                    VirtualizationFirmwareEnabled: Virtualization(),
                    HypervisorPresent: Hypervisor(),
                    Caches: topology?.Caches ?? []));
            return Value.Ok(cpu, "composite:cpu");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<CpuHardware>(ex, "composite:cpu");
        }
    }

    private static CpuTopology? SafeTopology(out string? error)
    {
        try
        {
            var t = CpuTopology.Read();
            error = t is null ? "GetLogicalProcessorInformationEx returned nothing" : null;
            return t;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name;
            return null;
        }
    }

    private static Value<int> SmbiosMhz(SmbiosStructure? type4, int offset, string what, bool smbiosMissing)
    {
        if (type4 is null)
        {
            return smbiosMissing ? Value.Failed<int>(SmbiosSource, "SMBIOS not readable") : Value.Unsupported<int>(SmbiosSource, "no Type 4 structure");
        }

        var mhz = type4.U16(offset);
        return mhz is null or 0 ? Value.Unsupported<int>(SmbiosSource, "not reported") : Value.Ok(mhz.Value, SmbiosSource + " " + what);
    }

    private sealed record CpuIdentity(string Vendor, int Family, int Model, int Stepping, bool Hypervisor);

    private static CpuIdentity? Identity()
    {
        if (!X86Base.IsSupported)
        {
            return null;
        }

        var leaf0 = X86Base.CpuId(0, 0);
        var vendor = Encoding.ASCII.GetString([.. BitConverter.GetBytes(leaf0.Ebx), .. BitConverter.GetBytes(leaf0.Edx), .. BitConverter.GetBytes(leaf0.Ecx)]);
        var leaf1 = X86Base.CpuId(1, 0);
        var eax = (uint)leaf1.Eax;
        var stepping = (int)(eax & 0xF);
        var model = (int)((eax >> 4) & 0xF);
        var family = (int)((eax >> 8) & 0xF);
        var extModel = (int)((eax >> 16) & 0xF);
        var extFamily = (int)((eax >> 20) & 0xFF);
        var displayFamily = family == 0xF ? family + extFamily : family;
        var displayModel = family is 0x6 or 0xF ? (extModel << 4) + model : model;
        var hypervisor = ((uint)leaf1.Ecx & (1u << 31)) != 0;
        return new CpuIdentity(vendor, displayFamily, displayModel, stepping, hypervisor);
    }

    private static Value<int> Cpuid(Func<CpuIdentity, int> pick)
    {
        try
        {
            var id = Identity();
            return id is null ? Value.Unsupported<int>(CpuidSource, "CPUID is x86/x64 only") : Value.Ok(pick(id), CpuidSource);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<int>(ex, CpuidSource);
        }
    }

    private static Value<string> Vendor()
    {
        try
        {
            var id = Identity();
            return id is null
                ? Value.Unsupported<string>(CpuidSource, "CPUID is x86/x64 only")
                : Value.Ok(
                    id.Vendor switch
                    {
                        "GenuineIntel" => "Intel",
                        "AuthenticAMD" => "AMD",
                        _ => id.Vendor,
                    },
                    CpuidSource);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<string>(ex, CpuidSource);
        }
    }

    private static Value<string> BrandString()
    {
        try
        {
            if (X86Base.IsSupported && (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax >= 0x80000004)
            {
                var bytes = new List<byte>(48);
                for (var leaf = 0x80000002u; leaf <= 0x80000004u; leaf++)
                {
                    var r = X86Base.CpuId(unchecked((int)leaf), 0);
                    bytes.AddRange(BitConverter.GetBytes(r.Eax));
                    bytes.AddRange(BitConverter.GetBytes(r.Ebx));
                    bytes.AddRange(BitConverter.GetBytes(r.Ecx));
                    bytes.AddRange(BitConverter.GetBytes(r.Edx));
                }

                var text = Encoding.ASCII.GetString([.. bytes]).TrimEnd('\0', ' ').Trim();
                if (text.Length > 0)
                {
                    return Value.Ok(text, CpuidSource);
                }
            }

            const string key = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
            using var reg = Registry.LocalMachine.OpenSubKey(key);
            var name = reg?.GetValue("ProcessorNameString") as string;
            return Probe.Text("registry:CentralProcessor\\0\\ProcessorNameString", name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<string>(ex, CpuidSource);
        }
    }

    /// <summary>Windows' own nominal frequency (~MHz). Not a measurement of the current clock.</summary>
    private static Value<int> BaseClock()
    {
        const string source = "registry:CentralProcessor\\0\\~MHz (nominal)";
        try
        {
            using var reg = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return reg?.GetValue("~MHz") is int mhz and > 0
                ? Value.Ok(mhz, source)
                : Value.Unsupported<int>(source, "not reported");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<int>(ex, source);
        }
    }

    private static Value<bool> Virtualization()
    {
        const string source = "winapi:IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED)";
        try
        {
            return Value.Ok(Kernel32.IsProcessorFeaturePresent(21), source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<bool>(ex, source);
        }
    }

    private static Value<bool> Hypervisor()
    {
        try
        {
            var id = Identity();
            return id is null ? Value.Unsupported<bool>(CpuidSource, "CPUID is x86/x64 only") : Value.Ok(id.Hypervisor, CpuidSource + " leaf1.ecx[31]");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Probe.FromException<bool>(ex, CpuidSource);
        }
    }

    /// <summary>
    /// .NET's IsSupported flags check CPUID and the OS-enabled state (XGETBV), so "AVX" here means "usable on this Windows",
    /// not only "the silicon has it".
    /// </summary>
    private static Value<IReadOnlyList<string>> InstructionSets()
    {
        const string source = "cpuid+xgetbv";
        if (!X86Base.IsSupported)
        {
            return Value.Unsupported<IReadOnlyList<string>>(source, "x86/x64 instruction sets only");
        }

        var list = new List<string>();

        void Add(bool supported, string name)
        {
            if (supported)
            {
                list.Add(name);
            }
        }

        Add(Sse.IsSupported, "SSE");
        Add(Sse2.IsSupported, "SSE2");
        Add(Sse3.IsSupported, "SSE3");
        Add(Ssse3.IsSupported, "SSSE3");
        Add(Sse41.IsSupported, "SSE4.1");
        Add(Sse42.IsSupported, "SSE4.2");
        Add(Popcnt.IsSupported, "POPCNT");
        Add(Lzcnt.IsSupported, "LZCNT");
        Add(Bmi1.IsSupported, "BMI1");
        Add(Bmi2.IsSupported, "BMI2");
        Add(Pclmulqdq.IsSupported, "PCLMULQDQ");
        Add(Aes.IsSupported, "AES-NI");
        Add(Avx.IsSupported, "AVX");
        Add(Avx2.IsSupported, "AVX2");
        Add(Fma.IsSupported, "FMA3");
        Add(AvxVnni.IsSupported, "AVX-VNNI");
        Add(Avx512F.IsSupported, "AVX-512F");
        Add(Avx512BW.IsSupported, "AVX-512BW");
        Add(Avx512CD.IsSupported, "AVX-512CD");
        Add(Avx512DQ.IsSupported, "AVX-512DQ");
        Add(Avx512F.VL.IsSupported, "AVX-512VL");
        Add(Avx512Vbmi.IsSupported, "AVX-512VBMI");
        Add(Avx10v1.IsSupported, "AVX10.1");
        return Value.Ok<IReadOnlyList<string>>(list, source);
    }
}
