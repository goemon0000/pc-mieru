// Provenance: PartsLife.Checks.Build.BuildRules and the Raw* records (pl-cloud 25e4571, src/PartsLife.Checks/Build/BuildRules.cs), MIT, same owner.
// Every rule, table, regex and limit is the old one, byte for byte in what it produces (the configuration code is a compatibility contract with
// the receiver, DESIGN-A 9); the types are internal and the comments are in English.
using System.Text;
using System.Text.RegularExpressions;

namespace PcMieru.Checks.Build;

/// <summary>One module of Win32_PhysicalMemory (the values as they came).</summary>
/// <param name="Speed">Speed (the module's rated speed, not the XMP/EXPO speed).</param>
/// <param name="ConfiguredSpeed">ConfiguredClockSpeed (the speed it runs at now); 0 when it could not be read.</param>
internal sealed record RawMemory(string PartNumber, string Manufacturer, ulong Capacity, uint Speed, int SmbiosType, int MemoryType,
                                 uint ConfiguredSpeed = 0);

/// <summary>One GPU: from DXGI (which gets the VRAM right), or from Win32_VideoController when DXGI gives nothing.</summary>
/// <param name="OnPciBus">It has a PCI address (so it is a real board). A virtual display or a remote GPU has none.</param>
internal sealed record RawGpu(string Name, ulong DedicatedBytes, bool OnPciBus);

/// <summary>One storage device. <see cref="BusType"/> and <see cref="MediaType"/> are kept as the numbers of MSFT_PhysicalDisk.</summary>
internal sealed record RawDisk(string Name, int BusType, int MediaType, ulong Size);

/// <summary>One monitor (WmiMonitorID).</summary>
internal sealed record RawMonitor(string Name, bool Active);

/// <summary>One optical drive (Win32_CDROMDrive).</summary>
internal sealed record RawOptical(string Name, string PnpDeviceId);

/// <summary>
/// The rules that turn what was read into the parts of the configuration code. **All pure functions** (they never touch WMI), so that a test can
/// check the content without the real machine.
/// </summary>
internal static class BuildRules
{
    // =====================================================================
    // Names
    // =====================================================================

    /// <summary>Trims, collapses runs of white space to one, drops control characters (NUL ...), and cuts at 80 characters.</summary>
    public static string CleanName(string? s, int max = 80)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        string r = sb.ToString();
        return r.Length <= max ? r : r[..max].TrimEnd();
    }

    /// <summary>
    /// The stock phrases a BIOS or a driver puts in place of "no value". They mean nothing as a model name.
    /// </summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To be filled by O.E.M.", "To Be Filled By O.E.M.", "Default string", "System Product Name", "System manufacturer",
        "Base Board Product Name", "Base Board Manufacturer", "Not Applicable", "Not Specified", "Not Available",
        "None", "Unknown", "Undefined", "N/A", "NA", "Manufacturer", "Product Name", "Type2 - Board Product Name1",
        "Type2 - Board Vendor Name1", "OEM", "O.E.M.", "Default",
    };

    private static readonly Regex Zeros = new("^(0x)?[0F]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Not usable as a value: empty, a stock phrase, or a run of 0 or F.</summary>
    public static bool IsPlaceholder(string? s)
    {
        string c = CleanName(s);
        return c.Length == 0 || Placeholders.Contains(c) || Zeros.IsMatch(c);
    }

    /// <summary>
    /// Whether it looks like a serial number. **Some models report the serial in place of the model**, so this is the last net.
    ///
    /// No white space, longer than 16 characters, and either
    /// - 90 % or more of the characters (separators - _ . : left out) are hex digits (an NVMe serial, a UUID, a serial turned into hex), or
    /// - it holds a run of 10 or more digits.
    ///
    /// Model names can be long without white space too (Seagate ST2000DM008-2FR102, Corsair CMK64GX5M2B5600Z40). Those hold non-hex
    /// letters and no long run of digits, so they stay.
    /// </summary>
    public static bool LooksLikeSerial(string? s)
    {
        string c = CleanName(s, int.MaxValue);
        if (c.Length <= 16 || c.Contains(' ')) return false;
        int hex = 0, total = 0, run = 0, longest = 0;
        foreach (char ch in c)
        {
            if (char.IsAsciiDigit(ch)) { run++; longest = Math.Max(longest, run); }
            else run = 0;
            if (ch is '-' or '_' or '.' or ':') continue;
            total++;
            if (char.IsAsciiHexDigit(ch)) hex++;
        }
        return longest >= 10 || (total > 0 && hex >= total * 0.9);
    }

    // =====================================================================
    // CPU and motherboard
    // =====================================================================

    public static List<BuildPart> Cpus(IEnumerable<string> names) =>
        names.Select(n => CleanName(n)).Where(n => !IsPlaceholder(n)).Select(n => new BuildPart(n)).ToList();

    /// <summary>The name is "maker model" (the receiver matches in that form). A stock phrase as the model gives no board.</summary>
    public static List<BuildPart> Motherboard(string? manufacturer, string? product)
    {
        string p = CleanName(product);
        if (IsPlaceholder(p)) return new();
        string m = IsPlaceholder(manufacturer) ? "" : CleanName(manufacturer, 40);
        string name = m.Length > 0 && !p.StartsWith(m, StringComparison.OrdinalIgnoreCase) ? $"{m} {p}" : p;
        return new() { new BuildPart(CleanName(name), Mfr: m.Length > 0 ? m : null) };
    }

    // =====================================================================
    // Memory
    // =====================================================================

    /// <summary>
    /// The Memory Type of the SMBIOS Memory Device (Type 17), in Win32_PhysicalMemory.SMBIOSMemoryType. For 0 or 2 (Unknown) the old
    /// MemoryType is used (it only knows up to DDR3).
    /// </summary>
    public static string? RamType(int smbiosType, int memoryType = 0) => smbiosType switch
    {
        0x12 => "DDR",
        0x13 or 0x14 => "DDR2",
        0x18 or 0x19 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => memoryType switch
        {
            20 => "DDR",
            21 or 22 => "DDR2",
            24 or 25 => "DDR3",
            26 => "DDR4",
            _ => null,
        },
    };

    /// <summary>
    /// Some models report the maker as a JEDEC number ("80CE" or "CE00000000000000"). The common ones are turned into a name; an unknown
    /// number is not written (a number the receiver cannot read is no use to it).
    /// </summary>
    private static readonly Dictionary<string, string> Jedec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung", ["CE"] = "Samsung",
        ["80AD"] = "SK hynix", ["AD"] = "SK hynix",
        ["802C"] = "Micron", ["2C"] = "Micron",
        ["0198"] = "Kingston", ["9801"] = "Kingston",
        ["029E"] = "Corsair", ["9E02"] = "Corsair",
        ["04CD"] = "G.Skill", ["CD04"] = "G.Skill",
        ["859B"] = "Crucial", ["059B"] = "Crucial", ["9B05"] = "Crucial",
        ["04CB"] = "ADATA", ["CB04"] = "ADATA",
        ["04EF"] = "Team Group", ["EF04"] = "Team Group",
    };

    private static readonly Regex HexOnly = new("^[0-9A-F]{2,16}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? RamManufacturer(string? raw)
    {
        string m = CleanName(raw, 40);
        if (IsPlaceholder(m)) return null;
        if (!HexOnly.IsMatch(m)) return m;
        string key = m.TrimEnd('0');
        if (key.Length % 2 == 1) key += "0";     // do not trim the trailing 0 of "9E02000..." too far
        return Jedec.TryGetValue(key, out var name) ? name : Jedec.GetValueOrDefault(m.Length >= 4 ? m[..4] : m);
    }

    /// <summary>
    /// The speed written as <c>spd</c>. The speed it runs at now when that is faster than the rated one (XMP/EXPO is on), otherwise the rated
    /// one. The rated speed (Speed) is the JEDEC value, so even a DDR5-5600 XMP kit is reported as 4800. When it runs slower than rated (set
    /// down in the BIOS) the rated speed is written, as the performance of the part.
    /// </summary>
    public static int? RamSpeed(uint rated, uint configured)
    {
        uint v = configured > rated ? configured : rated;
        return v > 0 ? (int)v : null;
    }

    /// <summary>
    /// One part per module (merging the same ones is <see cref="BuildCode"/>'s job). When the part number cannot be read or looks like a
    /// serial the name is made in the form "DDR4 16GB" (and when there is no capacity either, the module is left out).
    /// </summary>
    public static List<BuildPart> Ram(IEnumerable<RawMemory> modules)
    {
        var list = new List<BuildPart>();
        foreach (var m in modules)
        {
            int? cap = m.Capacity > 0 ? (int)Math.Round(m.Capacity / 1073741824.0) : null;
            if (cap == 0) cap = null;
            string? type = RamType(m.SmbiosType, m.MemoryType);
            string name = CleanName(m.PartNumber);
            if (IsPlaceholder(name) || LooksLikeSerial(name))
            {
                if (cap is null) continue;
                name = $"{type ?? "RAM"} {cap}GB".Trim();
            }
            list.Add(new BuildPart(name, Cap: cap, Speed: RamSpeed(m.Speed, m.ConfiguredSpeed), Type: type, Mfr: RamManufacturer(m.Manufacturer)));
        }
        return list;
    }

    // =====================================================================
    // GPU
    // =====================================================================

    /// <summary>
    /// Display devices that are not real boards: virtual displays (VR streaming, remote desktop), the standard driver, the screen of a
    /// virtual machine. ("基本ディスプレイ" is the Japanese name of the Basic Display Adapter: it is part of what is matched.)
    /// </summary>
    private static readonly string[] VirtualWords =
    {
        "basic display", "basic render", "基本ディスプレイ", "hyper-v", "virtual", "vmware", "virtualbox", "parsec",
        "remote display", "remote desktop", "indirect display", "iddsample", "spacedesk", "citrix", "qxl", "red hat",
        "splashtop", "displaylink", "mirage driver", "oculus",
    };

    public static bool IsVirtualDevice(string name)
    {
        string n = name.ToLowerInvariant();
        return VirtualWords.Any(w => n.Contains(w));
    }

    /// <summary>
    /// A GPU inside the CPU (AMD Radeon(TM) Graphics and 780M, Intel UHD, Iris and Arc(TM) Graphics). **It is not a board, so it does not
    /// go into the GPU list** (it would be mistaken for "has a GPU" when comparing build-to-order PCs). The name of a discrete board has
    /// no "Graphics" after the model (Radeon RX 7900 XTX; Arc A770 has it but the number comes first).
    /// </summary>
    private static readonly Regex Integrated = new(
        @"(Radeon(\s*\(TM\))?\s+((\d{3,4}M|Vega\s*\d*|R[2-7])\s+)?Graphics\s*$)" +
        @"|(Intel.*\b(U?HD|Iris)\b.*Graphics)" +
        @"|(Intel.*Arc(\s*\(TM\))?\s+(Graphics\s*$|\d{3}[VTH]\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsIntegratedGpu(string name) => Integrated.IsMatch(CleanName(name));

    public static List<BuildPart> Gpus(IEnumerable<RawGpu> gpus)
    {
        var list = new List<BuildPart>();
        foreach (var g in gpus)
        {
            string name = CleanName(g.Name);
            if (!g.OnPciBus || IsPlaceholder(name) || IsVirtualDevice(name) || IsIntegratedGpu(name)) continue;
            int vram = (int)Math.Round(g.DedicatedBytes / 1073741824.0);
            list.Add(new BuildPart(name, Vram: vram > 0 ? vram : null));
        }
        return list;
    }

    // =====================================================================
    // Storage
    // =====================================================================

    /// <summary>The BusType values of MSFT_PhysicalDisk that count as a part inside the machine, and how the code writes them.</summary>
    public static string? StorageInterface(int busType) => busType switch
    {
        17 => "nvme",
        11 or 3 => "sata",   // 3 = ATA (a SATA drive run in IDE compatibility mode comes here too)
        10 => "sas",
        _ => null,
    };

    /// <summary>
    /// Connections that do not count as a part: USB (external drives, USB sticks), SD/MMC, virtual disks, Storage Spaces, iSCSI and Fibre
    /// Channel (beyond the network), ATAPI (optical drives).
    /// </summary>
    public static bool IsExcludedBus(int busType) => busType is 2 or 6 or 7 or 9 or 12 or 13 or 14 or 15 or 16;

    /// <summary>
    /// Maps the connection of a Win32_DiskDrive (the stand-in when MSFT_PhysicalDisk cannot be read) onto the numbers of BusType.
    /// </summary>
    public static int BusFromWin32(string? interfaceType, string? pnpDeviceId, string? mediaType)
    {
        string pnp = pnpDeviceId ?? "";
        if (pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)) return 7;
        if ((mediaType ?? "").Contains("External", StringComparison.OrdinalIgnoreCase)
            || (mediaType ?? "").Contains("Removable", StringComparison.OrdinalIgnoreCase)) return 7;
        if (pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase)) return 17;
        return (interfaceType ?? "").ToUpperInvariant() switch
        {
            "USB" or "1394" => 7,
            "IDE" => 11,
            "SCSI" => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Decimal GB. When it is close to a size that is sold (within 5 %) it is moved to that size (a "1TB" of 1,024,209,543,168 bytes is 1000,
    /// 512,110,190,592 bytes is 512).
    /// </summary>
    public static int StorageGb(ulong bytes)
    {
        double gb = bytes / 1e9;
        int best = 0;
        double bestDiff = double.MaxValue;
        foreach (int s in MarketSizes)
        {
            double diff = Math.Abs(gb - s) / s;
            if (diff <= 0.05 && diff < bestDiff) { best = s; bestDiff = diff; }
        }
        return best > 0 ? best : (int)Math.Round(gb);
    }

    private static readonly int[] MarketSizes =
    {
        32, 64, 120, 128, 240, 250, 256, 320, 480, 500, 512, 750, 960, 1000, 1500, 1920, 2000, 3000, 3840, 4000,
        5000, 6000, 7680, 8000, 10000, 12000, 14000, 16000, 18000, 20000, 22000, 24000, 26000, 28000, 30000, 32000,
    };

    /// <summary>
    /// SSD or HDD. By MediaType (4 = SSD, 5 = SCM, 3 = HDD) when it is known; otherwise NVMe is an SSD, a name with SSD / NVMe / Solid State
    /// is an SSD, and anything else is an HDD.
    /// </summary>
    public static bool IsSsd(RawDisk d)
    {
        if (d.MediaType is 4 or 5) return true;
        if (d.MediaType == 3) return false;
        if (d.BusType == 17) return true;
        string n = d.Name.ToUpperInvariant();
        return n.Contains("SSD") || n.Contains("NVME") || n.Contains("SOLID STATE");
    }

    public static (List<BuildPart> Ssd, List<BuildPart> Hdd) Disks(IEnumerable<RawDisk> disks)
    {
        var ssd = new List<BuildPart>();
        var hdd = new List<BuildPart>();
        foreach (var d in disks)
        {
            string name = CleanName(d.Name);
            if (IsExcludedBus(d.BusType) || d.Size == 0 || IsPlaceholder(name) || IsVirtualDevice(name)) continue;
            int gb = StorageGb(d.Size);
            var part = new BuildPart(name, Cap: gb > 0 ? gb : null, Type: StorageInterface(d.BusType));
            (IsSsd(d) ? ssd : hdd).Add(part);
        }
        return (ssd, hdd);
    }

    // =====================================================================
    // Monitors and optical drives
    // =====================================================================

    /// <summary>UserFriendlyName and the like of WmiMonitorID are a UInt16 array (one character per element, ending in 0).</summary>
    public static string DecodeWmiString(ushort[]? raw)
    {
        if (raw is null) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (ushort u in raw)
        {
            if (u == 0) break;
            sb.Append((char)u);
        }
        return CleanName(sb.ToString());
    }

    public static List<BuildPart> Monitors(IEnumerable<RawMonitor> monitors) =>
        monitors.Where(m => m.Active)
                .Select(m => CleanName(m.Name))
                .Where(n => !IsPlaceholder(n) && !IsVirtualDevice(n))
                .Select(n => new BuildPart(n)).ToList();

    /// <summary>Optical drives. The virtual drive made when an ISO is opened (Msft Virtual DVD-ROM) and USB ones are not counted.</summary>
    public static List<BuildPart> Opticals(IEnumerable<RawOptical> drives) =>
        drives.Where(d => !d.PnpDeviceId.Contains("MSFT", StringComparison.OrdinalIgnoreCase)
                          && !d.PnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
              .Select(d => CleanName(d.Name))
              .Where(n => !IsPlaceholder(n) && !IsVirtualDevice(n))
              .Select(n => new BuildPart(n)).ToList();
}
