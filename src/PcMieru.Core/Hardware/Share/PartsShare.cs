using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Hardware.Share;

/// <summary>
/// "pcm-parts/1": the model numbers of this PC and nothing else, for pasting into the web tool that works out power and compatibility.
/// The document is built by an allow-list: <see cref="Build"/> copies a fixed set of fields out of <see cref="HardwareInfo"/> into
/// the records below and <see cref="ToJson"/> writes only those records. Nothing is "removed" from a bigger document, so a field
/// that a later collector change adds to the hardware model cannot end up here by accident. It is built from the categories CPU,
/// memory, board, GPU, storage, display and battery (and USB with the flag) only; the network, audio and Windows categories are never read.
/// The document holds no serial number, MAC or IP address, SSID, PC or user name, path, date, driver / BIOS / firmware version,
/// raw EDID, USB device name or process information. Every text is cut down to printable characters, trimmed and capped at
/// <see cref="MaxText"/>. A field that is not known is left out (never an empty string, never 0).
/// </summary>
public static class PartsShare
{
    /// <summary>The value of the "format" field.</summary>
    public const string FormatId = "pcm-parts/1";

    /// <summary>The longest text any field may carry.</summary>
    public const int MaxText = 64;

    // A board default outside this range is a misread, not a graphics card.
    private const double MinPlausibleWatts = 10;
    private const double MaxPlausibleWatts = 1500;
    private const int MaxPlausibleMm = 10_000;

    /// <summary>The categories the share document is built from. Anything else (network, audio, Windows) is never asked for.</summary>
    public static IReadOnlyList<HardwareCategory> Categories(bool includeUsb) =>
        includeUsb
            ? [HardwareCategory.Cpu, HardwareCategory.Memory, HardwareCategory.Board, HardwareCategory.Gpu, HardwareCategory.Storage, HardwareCategory.Display, HardwareCategory.Battery, HardwareCategory.Usb]
            : [HardwareCategory.Cpu, HardwareCategory.Memory, HardwareCategory.Board, HardwareCategory.Gpu, HardwareCategory.Storage, HardwareCategory.Display, HardwareCategory.Battery];

    /// <summary>How many more times the GPU category is read when the sensor host had no first reading yet (each read waits for it itself).</summary>
    internal const int MaxSensorRetries = 1;

    /// <summary>
    /// Reads the categories of <see cref="Categories"/> one by one through <paramref name="read"/> and merges them into one
    /// <see cref="HardwareInfo"/> whose other categories are NotCollected. Nothing outside the list is requested, so the network
    /// adapters (MAC, IP, SSID) and the Windows facts (user and PC related) are never even read for this document.
    /// The board default power limit of an NVIDIA card comes from the sensor host, which can be slow with its very first reading: when
    /// that reading timed out, the GPU category is read once more (the hardware service waits for the first reading on every read), so
    /// <c>observed</c> is not missing only because the host was still starting. That is the only wait; if it still has no reading,
    /// <c>observed</c> is left out.
    /// </summary>
    public static async Task<HardwareInfo> CollectAsync(Func<HardwareCategory, CancellationToken, Task<HardwareInfo>> read, bool includeUsb, CancellationToken ct = default)
    {
        var categories = Categories(includeUsb);
        var infos = await Task.WhenAll(categories.Select(c => read(c, ct))).ConfigureAwait(false);
        var gpuAt = categories.ToList().IndexOf(HardwareCategory.Gpu);
        for (var retry = 0; retry < MaxSensorRetries && WaitsForSensorHost(infos[gpuAt]); retry++)
        {
            ct.ThrowIfCancellationRequested();
            infos[gpuAt] = await read(HardwareCategory.Gpu, ct).ConfigureAwait(false);
        }

        HardwareInfo Of(HardwareCategory category)
        {
            for (var i = 0; i < categories.Count; i++)
            {
                if (categories[i] == category)
                {
                    return infos[i];
                }
            }

            throw new InvalidOperationException("category is not part of the share document");
        }

        const string source = "parts-share";
        var notAsked = "not requested";
        return new HardwareInfo(
            HardwareInfo.CurrentSchemaVersion,
            infos[0].CollectedAtUtc,
            Of(HardwareCategory.Cpu).Cpu,
            Of(HardwareCategory.Memory).Memory,
            Of(HardwareCategory.Board).Board,
            Of(HardwareCategory.Gpu).Gpus,
            Of(HardwareCategory.Storage).Storage,
            Value.NotCollected<IReadOnlyList<NetworkAdapter>>(source, notAsked),
            Of(HardwareCategory.Display).Displays,
            Value.NotCollected<IReadOnlyList<AudioEndpoint>>(source, notAsked),
            includeUsb ? Of(HardwareCategory.Usb).Usb : Value.NotCollected<IReadOnlyList<UsbDevice>>(source, notAsked),
            Of(HardwareCategory.Battery).Battery,
            Value.NotCollected<OsHardware>(source, notAsked));
    }

    /// <summary>True when an NVIDIA card has no power limit only because the sensor host had not produced its first reading in time.</summary>
    private static bool WaitsForSensorHost(HardwareInfo gpuInfo) =>
        gpuInfo.Gpus.TryGet(out var gpus)
        && gpus.Any(g => g.Vendor.TryGet(out var vendor) && vendor == "NVIDIA" && g.PowerLimitWatts.Status == ValueStatus.Timeout);

    /// <summary>Reads this PC through <paramref name="read"/> and returns the finished JSON text (see <see cref="ToJson"/>).</summary>
    public static async Task<string> CreateJsonAsync(Func<HardwareCategory, CancellationToken, Task<HardwareInfo>> read, string appVersion, bool includeUsb, CancellationToken ct = default) =>
        ToJson(Build(await CollectAsync(read, includeUsb, ct).ConfigureAwait(false), appVersion, includeUsb));

    /// <summary>The allow-list projection. <paramref name="appVersion"/> is the version of this program (not of anything on the PC).</summary>
    public static PartsDocument Build(HardwareInfo info, string appVersion, bool includeUsb)
    {
        var formFactor = FormFactorOf(info);
        return new PartsDocument(
            "PCMieru " + (Clean(appVersion) ?? "0.0.0"),
            Cpus(info),
            Board(info),
            Memory(info),
            Gpus(info, formFactor),
            Storage(info),
            Displays(info),
            includeUsb ? Usb(info) : null);
    }

    /// <summary>
    /// The text that is shown to the user and copied: one part per line, valid JSON. Only the records of <see cref="PartsDocument"/> are
    /// written, so what you see is what is shared.
    /// </summary>
    public static string ToJson(PartsDocument document)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"format\": ").Append(Compact(w => w.WriteStringValue(FormatId))).Append(",\n");
        sb.Append("  \"app\": ").Append(Compact(w => w.WriteStringValue(document.App))).Append(",\n");
        Section(sb, "cpu", document.Cpu.Select(c => Compact(w => WriteCpu(w, c))), last: false);
        if (document.Board is { } board)
        {
            sb.Append("  \"board\": ").Append(Compact(w => WriteBoard(w, board))).Append(",\n");
        }

        Section(sb, "memory", document.Memory.Select(m => Compact(w => WriteMemory(w, m))), last: false);
        Section(sb, "gpus", document.Gpus.Select(g => Compact(w => WriteGpu(w, g))), last: false);
        Section(sb, "storage", document.Storage.Select(s => Compact(w => WriteStorage(w, s))), last: false);
        Section(sb, "displays", document.Displays.Select(d => Compact(w => WriteDisplay(w, d))), last: document.Usb is null);
        if (document.Usb is { } usb)
        {
            Section(sb, "usb", usb.Select(u => Compact(w => WriteUsb(w, u))), last: true);
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// A text cut down to printable characters: control, format, private-use, unassigned and every surrogate character are dropped
    /// (so a character outside the Basic Multilingual Plane, such as an emoji, goes too, even when its surrogate pair is whole),
    /// runs of white space become one space, the ends are trimmed and the result is cut at <see cref="MaxText"/>. Null when nothing is left.
    /// </summary>
    internal static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var sb = new StringBuilder(Math.Min(text.Length, MaxText));
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (!Printable(ch))
            {
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
            if (sb.Length >= MaxText)
            {
                break;
            }
        }

        // A pending space and the next character can step over the cap together, so cut once more at the end.
        var result = sb.ToString();
        if (result.Length > MaxText)
        {
            result = result[..MaxText];
        }

        result = result.TrimEnd();
        return result.Length == 0 ? null : result;
    }

    private static bool Printable(char ch) => char.GetUnicodeCategory(ch) is not
        (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    /// <summary>The text of a value that was read (status Ok), cleaned. Null for anything else. Source and Detail are never looked at.</summary>
    private static string? Text(Value<string> value) => value.TryGet(out var text) ? Clean(text) : null;

    private static long? Positive(Value<long> value) => value.TryGet(out var number) && number > 0 ? number : null;

    private static bool IsHex(string? text, int length)
    {
        if (text is null || text.Length != length)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    // ---- projection ----

    private static IReadOnlyList<PartsCpu> Cpus(HardwareInfo info)
    {
        if (!info.Cpu.TryGet(out var cpu))
        {
            return [];
        }

        var name = Text(cpu.Name);
        var vendor = Text(cpu.Vendor);
        // Decimal "family-model-stepping", the way Windows names them ("Family 25 Model 97 Stepping 2" is 25-97-2).
        var id = cpu.Family.TryGet(out var family) && cpu.Model.TryGet(out var model) && cpu.Stepping.TryGet(out var stepping)
                 && family >= 0 && model >= 0 && stepping >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{family}-{model}-{stepping}")
            : null;
        return name is null && vendor is null && id is null ? [] : [new PartsCpu(name, vendor, id)];
    }

    private static PartsBoard? Board(HardwareInfo info)
    {
        if (!info.Board.TryGet(out var board))
        {
            return null;
        }

        // The revision is not exported: SMBIOS puts anything in that field, an EC firmware version ("KBC Version 08.4F.00") or a per-unit id.
        var result = new PartsBoard(Text(board.Manufacturer), Text(board.Product));
        return result.Manufacturer is null && result.Product is null ? null : result;
    }

    private static IReadOnlyList<PartsMemory> Memory(HardwareInfo info)
    {
        if (!info.Memory.TryGet(out var memory))
        {
            return [];
        }

        var groups = new List<PartsMemory>();
        foreach (var module in memory.Modules)
        {
            // An empty socket reads as exactly 0 bytes; it is not a module.
            if (module.CapacityBytes.TryGet(out var bytes) && bytes <= 0)
            {
                continue;
            }

            var item = new PartsMemory(
                Text(module.Manufacturer),
                Text(module.PartNumber),
                module.Type.TryGet(out var type) ? MemoryTypeName(type) : null,
                Positive(module.CapacityBytes),
                1);
            if (item.Manufacturer is null && item.PartNumber is null && item.Type is null && item.CapacityBytes is null)
            {
                continue;
            }

            var at = groups.FindIndex(g => g with { Count = 1 } == item);
            if (at < 0)
            {
                groups.Add(item);
            }
            else
            {
                groups[at] = groups[at] with { Count = groups[at].Count + 1 };
            }
        }

        return groups;
    }

    private static string? MemoryTypeName(MemoryType type) => type switch
    {
        MemoryType.Ddr3 => "DDR3",
        MemoryType.Ddr4 => "DDR4",
        MemoryType.Ddr5 => "DDR5",
        MemoryType.Lpddr4 => "LPDDR4",
        MemoryType.Lpddr5 => "LPDDR5",
        _ => null,
    };

    private static IReadOnlyList<PartsGpu> Gpus(HardwareInfo info, string formFactor)
    {
        if (!info.Gpus.TryGet(out var gpus))
        {
            return [];
        }

        var list = new List<PartsGpu>();
        foreach (var gpu in gpus)
        {
            // The software adapter ("Microsoft Basic Render Driver") is Windows, not a card.
            if (gpu.Details is { } d && d.Kind.TryGet(out var kind) && kind == "Software")
            {
                continue;
            }

            // An adapter with no PCI id (Remote Desktop, an indirect display driver, a virtual adapter) is not a card in a slot.
            var pci = gpu.PciId.TryGet(out var pciId) && IsPciPair(pciId) ? pciId.ToUpperInvariant() : null;
            if (pci is null)
            {
                continue;
            }

            var name = Text(gpu.Name);
            var sub = gpu.Details is { } det && det.SubsystemId.TryGet(out var subId) && IsPciPair(subId) ? subId.ToUpperInvariant() : null;
            list.Add(new PartsGpu(name, pci, sub, Observed(gpu, formFactor)));
        }

        // The order Windows lists the adapters in can change from one run to the next; the document must not.
        return [.. list
            .OrderBy(g => g.PciId, StringComparer.Ordinal)
            .ThenBy(g => g.SubsystemId, StringComparer.Ordinal)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ThenBy(g => g.Observed?.NvmlDefaultPowerLimitW ?? -1)];
    }

    /// <summary>"XXXX:XXXX", four hex digits each side (vendor and device, or subsystem vendor and device).</summary>
    private static bool IsPciPair(string? text) =>
        text is { Length: 9 } && text[4] == ':' && IsHex(text[..4], 4) && IsHex(text[5..], 4);

    private static PartsGpuObserved? Observed(GpuHardware gpu, string formFactor)
    {
        if (!gpu.Vendor.TryGet(out var vendor) || vendor != "NVIDIA")
        {
            return null;
        }

        if (!gpu.PowerLimitWatts.IsOk || !GpuSensorMerge.TryParseBoardDefaultWatts(gpu.PowerLimitWatts.Detail, out var watts)
            || watts < MinPlausibleWatts || watts > MaxPlausibleWatts)
        {
            return null;
        }

        return new PartsGpuObserved(watts, formFactor);
    }

    private static IReadOnlyList<PartsStorage> Storage(HardwareInfo info)
    {
        if (!info.Storage.TryGet(out var drives))
        {
            return [];
        }

        var groups = new List<PartsStorage>();
        foreach (var drive in drives)
        {
            if (!IsBuiltIn(drive))
            {
                continue;
            }

            var item = new PartsStorage(
                Text(drive.Model),
                drive.Bus.TryGet(out var bus) ? BusName(bus) : null,
                Positive(drive.CapacityBytes),
                1);
            if (item.Model is null && item.CapacityBytes is null)
            {
                continue;
            }

            var at = groups.FindIndex(g => g with { Count = 1 } == item);
            if (at < 0)
            {
                groups.Add(item);
            }
            else
            {
                groups[at] = groups[at] with { Count = groups[at].Count + 1 };
            }
        }

        return [.. groups
            .OrderBy(s => s.Bus, StringComparer.Ordinal)
            .ThenBy(s => s.Model, StringComparer.Ordinal)
            .ThenBy(s => s.CapacityBytes ?? -1)];
    }

    /// <summary>
    /// A drive that is a part of this PC. A USB stick or an external disk, any removable medium, a virtual disk (a mounted VHD, a
    /// hypervisor's disk), an iSCSI target and a Storage Spaces disk are not: they are what the user plugged in or mounted, not what was
    /// built in. A drive whose bus cannot be read is left out as well, because nothing says it is built in.
    /// </summary>
    private static bool IsBuiltIn(StorageDevice drive)
    {
        if (drive.Details is { } details && details.Removable.TryGet(out var removable) && removable)
        {
            return false;
        }

        if (!drive.Bus.TryGet(out var bus) || bus is StorageBus.Unknown or StorageBus.Usb or StorageBus.Virtual or StorageBus.Iscsi or StorageBus.Spaces)
        {
            return false;
        }

        // "Msft Virtual Disk", "VMware Virtual disk": a virtual disk that a driver reported on an ordinary bus (a Hyper-V guest says SCSI).
        return !(Text(drive.Model) is { } model && model.Contains("virtual", StringComparison.OrdinalIgnoreCase));
    }

    private static string? BusName(StorageBus bus) => bus switch
    {
        StorageBus.Nvme => "NVMe",
        StorageBus.Sata => "SATA",
        StorageBus.Raid => "RAID",
        StorageBus.Scsi => "SCSI",
        _ => null,
    };

    /// <summary>
    /// What identifies the monitor model, from the EDID fields the collector already decoded: the three-letter maker id, the numeric
    /// product code and the monitor-name descriptor (0xFC) only. The serial-number descriptor (0xFF), the unspecified-text descriptor
    /// (0xFE) and the 32-bit serial of the base block are not in <see cref="DisplayDetails"/> that this reads, and
    /// <see cref="DisplayDevice.Model"/> (which can fall back to a Windows-side name) is not used.
    /// </summary>
    private static IReadOnlyList<PartsDisplay> Displays(HardwareInfo info)
    {
        if (!info.Displays.TryGet(out var displays))
        {
            return [];
        }

        var list = new List<PartsDisplay>();
        foreach (var display in displays)
        {
            string? maker = null;
            string? code = null;
            string? model = null;
            if (display.Details is { } d)
            {
                maker = d.PnpId.TryGet(out var pnp) && pnp.Length == 3 && pnp.All(char.IsAsciiLetterUpper) ? pnp : null;
                code = d.ProductCode.TryGet(out var product) && product is >= 0 and <= 0xFFFF
                    ? product.ToString("X4", CultureInfo.InvariantCulture)
                    : null;
                model = Text(d.MonitorName);
            }

            int? width = display.PhysicalWidthMm.TryGet(out var w) && w is > 0 and <= MaxPlausibleMm ? w : null;
            int? height = display.PhysicalHeightMm.TryGet(out var h) && h is > 0 and <= MaxPlausibleMm ? h : null;
            if (maker is null && code is null && model is null)
            {
                // No EDID identity: a display that is only a size (or nothing) tells the tool nothing about the model.
                continue;
            }

            list.Add(new PartsDisplay(maker, code, model, width, height));
        }

        // Windows does not list the monitors in a fixed order; the document does.
        return [.. list
            .OrderBy(d => d.EdidManufacturer, StringComparer.Ordinal)
            .ThenBy(d => d.EdidProductCode, StringComparer.Ordinal)
            .ThenBy(d => d.EdidModel, StringComparer.Ordinal)
            .ThenBy(d => d.PhysicalWidthMm ?? -1)
            .ThenBy(d => d.PhysicalHeightMm ?? -1)];
    }

    private static IReadOnlyList<PartsUsb> Usb(HardwareInfo info)
    {
        if (!info.Usb.TryGet(out var devices))
        {
            return [];
        }

        var seen = new SortedSet<(string Vid, string Pid)>();
        foreach (var device in devices)
        {
            if (device.VendorId.TryGet(out var vid) && device.ProductId.TryGet(out var pid) && IsHex(vid, 4) && IsHex(pid, 4))
            {
                seen.Add((vid.ToUpperInvariant(), pid.ToUpperInvariant()));
            }
        }

        return [.. seen.Select(s => new PartsUsb(s.Vid, s.Pid))];
    }

    // ---- form factor ----

    /// <summary>
    /// "laptop", "desktop" or "unknown". A known SMBIOS chassis type decides: a laptop-like one is laptop, a desktop-like one is desktop
    /// (a desktop with a USB UPS stays a desktop, because Windows lists a UPS as a battery), and any other known one (server, embedded)
    /// is unknown. Only when the chassis says nothing ("Other", "Unknown", not readable) does a system battery decide: laptop if a
    /// battery device that reports itself as the system battery was seen. A battery value that is merely Ok is not enough, since that
    /// is also what a UPS gives.
    /// </summary>
    internal static string FormFactorOf(HardwareInfo info)
    {
        string? chassis = null;
        if (info.Board.TryGet(out var board) && board.Details is { } details && details.Chassis.Type.TryGet(out var type))
        {
            chassis = type;
        }

        if (chassis is not null && !IsUnspecifiedChassis(chassis))
        {
            if (LaptopChassis.Contains(chassis))
            {
                return "laptop";
            }

            return DesktopChassis.Contains(chassis) ? "desktop" : "unknown";
        }

        return info.Battery.TryGet(out var battery) && battery.Details is { } d && d.SystemBattery.TryGet(out var system) && system
            ? "laptop"
            : "unknown";
    }

    /// <summary>The chassis names that carry no information: SMBIOS 1 and 2, and the types the collector has no name for.</summary>
    internal static bool IsUnspecifiedChassis(string chassis) =>
        chassis is "Other" or "Unknown" || chassis.StartsWith("Other (", StringComparison.Ordinal);

    // The names are the ones BoardCollector.ChassisType gives; a test walks every SMBIOS chassis type through it so a new name cannot be missed.
    internal static readonly HashSet<string> LaptopChassis = new(StringComparer.Ordinal)
    {
        "Portable", "Laptop", "Notebook", "Hand held", "Sub notebook", "Tablet", "Convertible", "Detachable",
    };

    internal static readonly HashSet<string> DesktopChassis = new(StringComparer.Ordinal)
    {
        "Desktop", "Low profile desktop", "Pizza box", "Mini tower", "Tower", "All in one", "Space-saving", "Lunch box", "Sealed-case PC", "Mini PC",
    };

    /// <summary>Every name <see cref="BoardCollector.ChassisType"/> can return for the SMBIOS values 1 to 36, to check against the two sets.</summary>
    internal static IEnumerable<string> AllChassisNames() => Enumerable.Range(1, 36).Select(BoardCollector.ChassisType);

    // ---- JSON ----

    private static string Compact(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(writer);
        }

        // Indented output puts a new line only between tokens: a string cannot hold a raw new line (it is written as \n), so
        // folding every "new line and its spaces" into one space leaves valid JSON with the whole object on one line.
        var text = Encoding.UTF8.GetString(stream.ToArray());
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                sb.Append(' ');
                while (i + 1 < text.Length && text[i + 1] == ' ')
                {
                    i++;
                }
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string name, IEnumerable<string> items, bool last)
    {
        var rows = items.ToList();
        sb.Append("  \"").Append(name).Append("\": ");
        if (rows.Count == 0)
        {
            sb.Append("[]");
        }
        else
        {
            sb.Append("[\n");
            for (var i = 0; i < rows.Count; i++)
            {
                sb.Append("    ").Append(rows[i]).Append(i + 1 < rows.Count ? ",\n" : "\n");
            }

            sb.Append("  ]");
        }

        sb.Append(last ? "\n" : ",\n");
    }

    private static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteCpu(Utf8JsonWriter w, PartsCpu c)
    {
        w.WriteStartObject();
        Str(w, "name", c.Name);
        Str(w, "vendor", c.Vendor);
        Str(w, "cpuid", c.CpuId);
        w.WriteEndObject();
    }

    private static void WriteBoard(Utf8JsonWriter w, PartsBoard b)
    {
        w.WriteStartObject();
        Str(w, "manufacturer", b.Manufacturer);
        Str(w, "product", b.Product);
        w.WriteEndObject();
    }

    private static void WriteMemory(Utf8JsonWriter w, PartsMemory m)
    {
        w.WriteStartObject();
        Str(w, "manufacturer", m.Manufacturer);
        Str(w, "partNumber", m.PartNumber);
        Str(w, "type", m.Type);
        if (m.CapacityBytes is { } bytes)
        {
            w.WriteNumber("capacityBytes", bytes);
        }

        w.WriteNumber("count", m.Count);
        w.WriteEndObject();
    }

    private static void WriteGpu(Utf8JsonWriter w, PartsGpu g)
    {
        w.WriteStartObject();
        Str(w, "name", g.Name);
        Str(w, "pciId", g.PciId);
        Str(w, "subsystemId", g.SubsystemId);
        if (g.Observed is { } o)
        {
            w.WriteStartObject("observed");
            w.WriteNumber("nvmlDefaultPowerLimitW", o.NvmlDefaultPowerLimitW);
            w.WriteString("formFactor", o.FormFactor);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteStorage(Utf8JsonWriter w, PartsStorage s)
    {
        w.WriteStartObject();
        Str(w, "model", s.Model);
        Str(w, "bus", s.Bus);
        if (s.CapacityBytes is { } bytes)
        {
            w.WriteNumber("capacityBytes", bytes);
        }

        w.WriteNumber("count", s.Count);
        w.WriteEndObject();
    }

    private static void WriteDisplay(Utf8JsonWriter w, PartsDisplay d)
    {
        w.WriteStartObject();
        Str(w, "edidManufacturer", d.EdidManufacturer);
        Str(w, "edidProductCode", d.EdidProductCode);
        Str(w, "edidModel", d.EdidModel);
        if (d.PhysicalWidthMm is { } width)
        {
            w.WriteNumber("physicalWidthMm", width);
        }

        if (d.PhysicalHeightMm is { } height)
        {
            w.WriteNumber("physicalHeightMm", height);
        }

        w.WriteEndObject();
    }

    private static void WriteUsb(Utf8JsonWriter w, PartsUsb u)
    {
        w.WriteStartObject();
        w.WriteString("vid", u.Vid);
        w.WriteString("pid", u.Pid);
        w.WriteEndObject();
    }
}

/// <summary>The "pcm-parts/1" document. Every field is nullable or a list: not known means left out. Nothing else lives here, on purpose.</summary>
public sealed record PartsDocument(
    string App,
    IReadOnlyList<PartsCpu> Cpu,
    PartsBoard? Board,
    IReadOnlyList<PartsMemory> Memory,
    IReadOnlyList<PartsGpu> Gpus,
    IReadOnlyList<PartsStorage> Storage,
    IReadOnlyList<PartsDisplay> Displays,
    IReadOnlyList<PartsUsb>? Usb);

public sealed record PartsCpu(string? Name, string? Vendor, string? CpuId);

public sealed record PartsBoard(string? Manufacturer, string? Product);

/// <summary>Modules of the same maker, part number, type and size are one entry with a count.</summary>
public sealed record PartsMemory(string? Manufacturer, string? PartNumber, string? Type, long? CapacityBytes, int Count);

/// <summary>What was measured on this PC rather than looked up from a model number. Only NVIDIA, only with a valid board default power limit.</summary>
public sealed record PartsGpuObserved(double NvmlDefaultPowerLimitW, string FormFactor);

public sealed record PartsGpu(string? Name, string? PciId, string? SubsystemId, PartsGpuObserved? Observed);

public sealed record PartsStorage(string? Model, string? Bus, long? CapacityBytes, int Count);

public sealed record PartsDisplay(string? EdidManufacturer, string? EdidProductCode, string? EdidModel, int? PhysicalWidthMm, int? PhysicalHeightMm);

public sealed record PartsUsb(string Vid, string Pid);
