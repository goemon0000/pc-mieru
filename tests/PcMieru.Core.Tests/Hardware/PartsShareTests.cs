using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;
using PcMieru.Core.Hardware.Share;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>
/// "pcm-parts/1" (model numbers only). The privacy tests plant a unique marker in every identifying field of a fully populated
/// hardware tree (and in the Source/Detail of the fields that are exported) and require that no marker reaches the document.
/// </summary>
public partial class PartsShareTests
{
    private const string AppVersion = "9.9.9";

    // ---- builders ----

    /// <summary>Hands out unique, JSON-safe marker words ("ZZMK07DIMMSERIAL") and remembers them.</summary>
    private sealed class Marks
    {
        private readonly List<string> _all = [];

        public IReadOnlyList<string> All => _all;

        public string Next(string what)
        {
            var mark = $"ZZMK{_all.Count + 1:00}{what}";
            _all.Add(mark);
            return mark;
        }
    }

    private static Value<T> Ok<T>(T data, Marks marks) => new(data, ValueStatus.Ok, marks.Next("SRC"), marks.Next("DETAIL"));

    private static Value<T> Plain<T>(T data) => Value.Ok(data, "test");

    private static Value<T> Bad<T>() => Value.Unsupported<T>("test", "not available");

    private static Value<IReadOnlyList<string>> Strings(params string[] items) => Value.Ok<IReadOnlyList<string>>(items, "test");

    /// <summary>A fully populated tree: the exported fields hold real model numbers, every other text holds a marker.</summary>
    private static HardwareInfo Loaded(Marks m, DateTime? collectedAtUtc = null)
    {
        var cpu = new CpuHardware(
            Ok("AMD Ryzen 9 7950X 16-Core Processor", m), Ok("AMD", m), Ok(25, m), Ok(97, m), Ok(2, m), Plain(16), Plain(32), Bad<int>(), Bad<int>(), Plain(4500),
            Plain(m.Next("CPUSOCKET")), Plain(1L), Plain(2L), Plain(3L), Strings(m.Next("CPUFEATURE")),
            new CpuDetails(Plain(5700), Plain(4500), Plain(2), Plain(1), Plain(1), Plain(true), Plain(false), []));

        MemoryModule Module(string slot, string maker, string part, long bytes) => new(
            Plain(slot), Ok(bytes, m), Ok(MemoryType.Ddr5, m), Plain(5600), Plain(4800), Ok(maker, m), Ok(part, m), Plain(m.Next("DIMMSERIAL")), Plain(2), Plain(1100),
            Plain(m.Next("DIMMFORM")), new MemoryModuleDetails(Plain(m.Next("BANKLOCATOR")), Plain(64), Plain(64), Plain(1100), Plain(1100), Plain(1100), Plain(m.Next("DIMMTECH"))));

        var memory = new MemoryHardware(
            Plain(64L * 1024 * 1024 * 1024), Plain(4), Plain(2), Plain(256L * 1024 * 1024 * 1024),
            [
                Module(m.Next("SLOTA"), "Corsair", "CMK64GX5M2B5600Z40", 34_359_738_368),
                Module(m.Next("SLOTB"), "Corsair", "CMK64GX5M2B5600Z40", 34_359_738_368),
            ],
            new MemoryDetails(Plain(1L), Plain(m.Next("ECC")), Plain(m.Next("MEMLOCATION")), Plain(1)));

        var board = new BoardHardware(
            Ok("ASUSTeK COMPUTER INC.", m), Ok("TUF GAMING X670E-PLUS WIFI", m), Ok("Rev 1.xx", m), Plain(m.Next("BOARDSERIAL")),
            Plain(m.Next("BIOSVENDOR")), Plain(m.Next("BIOSVERSION")), Plain(m.Next("BIOSDATE")), Plain(true), Plain(true), Plain(true), Plain(m.Next("TPMVERSION")),
            new BoardDetails(
                new SystemIdentity(Plain(m.Next("SYSMAKER")), Plain(m.Next("SYSPRODUCT")), Plain(m.Next("SYSFAMILY")), Plain(m.Next("SYSSKU")), Plain(m.Next("SYSVERSION")), Plain(m.Next("SYSSERIAL"))),
                new ChassisIdentity(Plain("Desktop"), Plain(m.Next("CHASSISMAKER")), Plain(m.Next("CHASSISVERSION")), Plain(m.Next("CHASSISSERIAL"))),
                Plain(m.Next("SMBIOSVERSION")), Plain(33_554_432L), Plain(m.Next("BIOSRELEASE"))));

        GpuHardware Gpu(string name, string vendor, string pci, string sub, string kind, double? defaultW) => new(
            Ok(name, m), Ok(vendor, m), Plain(m.Next("DRIVERVERSION")), Plain(m.Next("DRIVERDATE")), Ok(pci, m), Plain(m.Next("LUID")), Plain(12L), Plain(32L),
            Plain(m.Next("VBIOS")), Plain(5), Plain(16),
            defaultW is { } w
                ? new Value<double>(250, ValueStatus.Ok, m.Next("PWRSRC"), m.Next("PWRDETAIL") + $" limit in force now; the driver allows 175 to 300 W, board default {w} W")
                : Bad<double>(),
            new GpuDetails(Plain(kind), Plain(sub), Plain(161), Plain(m.Next("DRIVERPROVIDER")), Plain(0L), Plain(m.Next("PCILOCATION")), Plain(5), Plain(16)));

        var gpus = new List<GpuHardware>
        {
            Gpu("NVIDIA GeForce RTX 5070", "NVIDIA", "10de:2f04", "1043:89e6", "discrete", 250),
            Gpu("AMD Radeon(TM) Graphics", "AMD", "1002:164E", "1043:8877", "integrated", null),
            Gpu("Microsoft Basic Render Driver", "Microsoft", "1414:008C", "0000:0000", "Software", null),
        };

        StorageDevice Drive(int number, string model, StorageBus bus, long bytes) => new(
            number, Ok(model, m), Plain(m.Next("DRIVESERIAL")), Plain(m.Next("DRIVEFIRMWARE")), Ok(bus, m), Ok(bytes, m), Plain(true), Plain("GPT"),
            [new StorageVolume(m.Next("VOLUMELETTER"), m.Next("VOLUMELABEL"), "NTFS", Plain(1L), Plain(1L))],
            new StorageDetails(
                Plain("SSD"), Plain(512), Plain(4096), Plain(false), [new StoragePartition(1, 0, 1, m.Next("PARTITIONTYPE"), "GPT")],
                Plain(m.Next("STORAGEDRIVER")), Plain(4), Plain(4), Plain(4), Plain(4)));

        var storage = new List<StorageDevice>
        {
            Drive(0, "Samsung SSD 980 PRO 2TB", StorageBus.Nvme, 2_000_398_934_016),
            Drive(1, "Samsung SSD 980 PRO 2TB", StorageBus.Nvme, 2_000_398_934_016),
            Drive(2, "TOSHIBA MG04ACA200E", StorageBus.Sata, 2_000_398_934_016),
        };

        var network = new List<NetworkAdapter>
        {
            new(Plain(m.Next("ADAPTERNAME")), Plain(NetworkKind.Wifi), Plain(1L), Plain(m.Next("MACADDRESS")), Strings(m.Next("IPADDRESS")), Strings(m.Next("DNSSERVER")),
                Plain(m.Next("NICDRIVER")), Plain(m.Next("SSIDNAME")), Plain(m.Next("WIFIBAND")), Plain(80),
                new NetworkDetails(Plain(m.Next("NICDESCRIPTION")), Plain(true), Plain(1500), Plain(true), Plain(m.Next("WIFISTANDARD")), Plain(1L), Plain(1L))),
        };

        var displays = new List<DisplayDevice>
        {
            new(Plain(m.Next("DISPLAYMAKERTEXT")), Plain(m.Next("DISPLAYMODELTEXT")), Plain(2024), Plain(14), Plain(597), Plain(336),
                Plain("2560x1440 @ 165 Hz"), Plain(m.Next("CURRENTRES")), Plain(165.0), Plain(false), Plain(DisplayConnection.DisplayPort), Plain(m.Next("DISPLAYGPUNAME")),
                new DisplayDetails(
                    Plain("WAM"), Plain(0x2700), Plain(m.Next("EDIDSERIAL")), Plain(m.Next("EDIDVERSION")), Plain(true), Plain(1), Plain(27.0), Plain(m.Next("INPUTTYPE")),
                    Plain(8), Plain("UG27PJ"), Plain(48.0), Plain(165.0), Plain(true), Plain(true), Plain(400))),
        };

        var audio = new List<AudioEndpoint>
        {
            new(Plain(m.Next("AUDIONAME")), Plain(AudioDirection.Output), Plain(true), Plain(48000), Plain(24),
                new AudioDetails(Plain(m.Next("AUDIOSTATE")), Plain(m.Next("AUDIOFORM")), Plain(m.Next("AUDIOADAPTER")), Plain(2))),
        };

        var usb = new List<UsbDevice>
        {
            new(Plain(m.Next("USBNAME")), Plain("046d"), Plain("c52b"), Plain(m.Next("USBMAKER")), Plain(m.Next("USBPARENT")),
                new UsbDeviceDetails(Plain(m.Next("USBCLASS")), Plain(m.Next("USBSERVICE")), Plain(m.Next("USBLOCATION")), Plain(1))),
            new(Plain(m.Next("USBNAME2")), Plain("046D"), Plain("C52B"), Plain(m.Next("USBMAKER2")), Plain(m.Next("USBPARENT2"))),
            new(Plain(m.Next("USBNAME3")), Plain("1532"), Plain("0003"), Plain(m.Next("USBMAKER3")), Plain(m.Next("USBPARENT3"))),
        };

        var battery = new BatteryHardware(Plain(80), Plain(true), Plain(1L), Plain(1L), Plain(1.0), Plain(100),
            new BatteryDetails(Plain(m.Next("CHEMISTRY")), Plain(m.Next("BATTERYSTATUS")), Plain(1), Plain(1)));

        var os = new OsHardware(Plain(m.Next("OSNAME")), Plain(m.Next("OSVERSION")), Plain(m.Next("OSBUILD")), Plain(m.Next("OSARCH")), Plain(123_456L), Plain(m.Next("POWERPLAN")), Plain(true), Plain(true),
            new OsDetails(Plain(m.Next("OSEDITION")), Plain(m.Next("OSDISPLAYVERSION")), Plain(1), Plain(m.Next("BOOTTIME")), Plain(m.Next("LOCALE"))));

        return new HardwareInfo(
            HardwareInfo.CurrentSchemaVersion,
            collectedAtUtc ?? new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            Plain(cpu), Plain(memory), Plain(board),
            Value.Ok<IReadOnlyList<GpuHardware>>(gpus, "test"),
            Value.Ok<IReadOnlyList<StorageDevice>>(storage, "test"),
            Value.Ok<IReadOnlyList<NetworkAdapter>>(network, "test"),
            Value.Ok<IReadOnlyList<DisplayDevice>>(displays, "test"),
            Value.Ok<IReadOnlyList<AudioEndpoint>>(audio, "test"),
            Value.Ok<IReadOnlyList<UsbDevice>>(usb, "test"),
            Plain(battery), Plain(os));
    }

    private static string Json(HardwareInfo info, bool includeUsb = false) =>
        PartsShare.ToJson(PartsShare.Build(info, AppVersion, includeUsb));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static bool Leaks(string json, IEnumerable<string> markers) => markers.Any(marker => json.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records which categories were asked for, and answers from the fake data.</summary>
    private sealed class SpyProvider : IHardwareProvider
    {
        private readonly object _lock = new();

        public List<HardwareCategory> Asked { get; } = [];

        public Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default)
        {
            lock (_lock)
            {
                Asked.Add(category);
            }

            return Task.FromResult(FakeHardwareData.Create(new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc), category));
        }

        public void Invalidate()
        {
        }
    }

    // ---- privacy ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoIdentifyingMarkerReachesTheDocument(bool includeUsb)
    {
        var marks = new Marks();
        var json = Json(Loaded(marks), includeUsb);

        Assert.True(marks.All.Count > 80, "the tree should carry many markers");
        var leaked = marks.All.Where(marker => json.Contains(marker, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(leaked.Count == 0, "markers in the document: " + string.Join(", ", leaked));
    }

    [Fact]
    public void TheDetectorSeesEveryMarkerInTheFullHardwareJson()
    {
        // If a marker could not be found even in the full dump, "none found in the share document" would prove nothing.
        var marks = new Marks();
        var full = PcMieruJson.Serialize(Loaded(marks));

        var missing = marks.All.Where(marker => !full.Contains(marker, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "markers the full dump does not carry: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheDetectorFlagsALeakedMarker()
    {
        var marks = new Marks();
        var clean = Json(Loaded(marks));
        Assert.False(Leaks(clean, marks.All));

        var first = marks.All[0];
        Assert.True(Leaks(clean + first, marks.All));
        Assert.True(Leaks(clean.Replace("\"format\"", "\"" + first.ToLowerInvariant() + "\"", StringComparison.Ordinal), marks.All));
    }

    [Fact]
    public void NoTimestampOrDateIsInTheDocument()
    {
        var json = Json(Loaded(new Marks()), includeUsb: true);

        Assert.DoesNotContain("2031", json, StringComparison.Ordinal);
        Assert.DoesNotContain("04:05:06", json, StringComparison.Ordinal);
        Assert.False(DateLike().IsMatch(json), "a date-like text is in the document");
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}|\d{1,2}:\d{2}:\d{2}")]
    private static partial Regex DateLike();

    [Fact]
    public void OnlyTheModelNumberFieldsAreWrittenForTheLoadedTree()
    {
        var json = Json(Loaded(new Marks()), includeUsb: true);
        var root = Parse(json);

        var names = new Dictionary<string, string[]>
        {
            ["cpu"] = ["name", "vendor", "cpuid"],
            ["memory"] = ["manufacturer", "partNumber", "type", "capacityBytes", "count"],
            ["gpus"] = ["name", "pciId", "subsystemId", "observed"],
            ["storage"] = ["model", "bus", "capacityBytes", "count"],
            ["displays"] = ["edidManufacturer", "edidProductCode", "edidModel", "physicalWidthMm", "physicalHeightMm"],
            ["usb"] = ["vid", "pid"],
        };
        foreach (var (section, allowed) in names)
        {
            foreach (var item in root.GetProperty(section).EnumerateArray())
            {
                foreach (var property in item.EnumerateObject())
                {
                    Assert.Contains(property.Name, allowed);
                }
            }
        }

        Assert.Equal(["manufacturer", "product", "revision"], root.GetProperty("board").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["nvmlDefaultPowerLimitW", "formFactor"], root.GetProperty("gpus")[0].GetProperty("observed").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["format", "app", "cpu", "board", "memory", "gpus", "storage", "displays", "usb"], root.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task FakeHardwareThroughTheProviderLeaksNothing()
    {
        // The shipped fake data has a board serial, drive serials, MAC, IPs, a USB parent id and a Windows build; none may show.
        var spy = new SpyProvider();
        var json = await PartsShare.CreateJsonAsync((c, ct) => spy.GetAsync(c, ct), AppVersion, includeUsb: true);

        string[] banned =
        [
            "SN-FAKE-0000", "S6B0NG0R000000X", "ZFN00000", "00-00-5E", "192.0.2", "fe80", "luid_", "999.99", "98.05", "Fake BIOS Vendor", "F31",
            "ROOT_HUB", "Speakers", "Microphone", "10.0.26300", "Windows 11", "25H2", "Balanced", "2025-10-01", "2026-10-01",
            "NTFS", "2031", "Fake Peripherals", "USB Composite Device",
        ];
        var leaked = banned.Where(text => json.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.True(leaked.Count == 0, "texts in the document: " + string.Join(", ", leaked) + Environment.NewLine + json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheModelCategoriesAreEverRead(bool includeUsb)
    {
        var spy = new SpyProvider();
        _ = await PartsShare.CreateJsonAsync((c, ct) => spy.GetAsync(c, ct), AppVersion, includeUsb);

        Assert.DoesNotContain(HardwareCategory.All, spy.Asked);
        Assert.DoesNotContain(HardwareCategory.Network, spy.Asked);
        Assert.DoesNotContain(HardwareCategory.Audio, spy.Asked);
        Assert.DoesNotContain(HardwareCategory.Os, spy.Asked);
        Assert.Equal(includeUsb, spy.Asked.Contains(HardwareCategory.Usb));
        Assert.Equal(
            PartsShare.Categories(includeUsb).OrderBy(c => c),
            spy.Asked.OrderBy(c => c));
    }

    [Fact]
    public async Task TheMergedInfoOnlyHoldsWhatWasAsked()
    {
        var spy = new SpyProvider();
        var info = await PartsShare.CollectAsync((c, ct) => spy.GetAsync(c, ct), includeUsb: false);

        Assert.True(info.Cpu.IsOk);
        Assert.True(info.Displays.IsOk);
        Assert.Equal(ValueStatus.NotCollected, info.Network.Status);
        Assert.Equal(ValueStatus.NotCollected, info.Audio.Status);
        Assert.Equal(ValueStatus.NotCollected, info.Os.Status);
        Assert.Equal(ValueStatus.NotCollected, info.Usb.Status);
    }

    [Fact]
    public void TextsWithControlCharactersAndLongTextsAreCleaned()
    {
        Assert.Equal("AB C DE", PartsShare.Clean("A\0B\tC  D​E"));
        Assert.Equal("a b", PartsShare.Clean("  a \r\n b \u0007 "));
        Assert.Null(PartsShare.Clean(null));
        Assert.Null(PartsShare.Clean(""));
        Assert.Null(PartsShare.Clean(" \t\r\n \0 "));
        Assert.Equal(new string('x', 64), PartsShare.Clean(new string('x', 200)));
        // The pending space and the next character together would step over the cap.
        Assert.Equal(new string('x', 63), PartsShare.Clean(new string('x', 63) + " y"));
        Assert.Equal(64, PartsShare.Clean(new string('x', 62) + " yz")!.Length);
        // An unpaired surrogate and a private-use character are not printable text.
        Assert.Equal("ab", PartsShare.Clean("a\ud800b"));
    }

    [Fact]
    public void EveryTextInTheDocumentIsCappedAndPrintable()
    {
        var loaded = Loaded(new Marks());
        var tooLong = new string('M', 90) + "\u0001";
        var info = loaded with
        {
            Cpu = Plain(loaded.Cpu.Data! with { Name = Plain(tooLong + "\n second line") }),
            Board = Plain(loaded.Board.Data! with { Product = Plain("Board\r\nName\t\"quoted\" \\ back") }),
        };

        var root = Parse(Json(info));
        Assert.Equal(64, root.GetProperty("cpu")[0].GetProperty("name").GetString()!.Length);
        Assert.Equal("Board Name \"quoted\" \\ back", root.GetProperty("board").GetProperty("product").GetString());

        foreach (var text in AllStrings(root))
        {
            Assert.True(text.Length <= 64, text);
            Assert.DoesNotContain(text, ch => char.IsControl(ch));
        }
    }

    private static IEnumerable<string> AllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var text in AllStrings(item))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var text in AllStrings(property.Value))
                    {
                        yield return text;
                    }
                }

                break;
        }
    }

    // ---- shape ----

    [Fact]
    public void TheDocumentIsValidJsonWithOnePartPerLine()
    {
        var json = Json(Loaded(new Marks()), includeUsb: true);
        var root = Parse(json);

        Assert.Equal("pcm-parts/1", root.GetProperty("format").GetString());
        Assert.Equal("PCMieru 9.9.9", root.GetProperty("app").GetString());
        Assert.DoesNotContain('\r', json);
        Assert.False(json.EndsWith('\n'));

        // Every array item is a whole object on a single line.
        foreach (var line in json.Split('\n').Where(l => l.StartsWith("    ", StringComparison.Ordinal)))
        {
            var trimmed = line.Trim();
            Assert.StartsWith("{", trimmed, StringComparison.Ordinal);
            Assert.True(trimmed.EndsWith('}') || trimmed.EndsWith("},", StringComparison.Ordinal), line);
        }
    }

    [Fact]
    public void WithoutTheFlagThereIsNoUsbSectionAndWithItThereIsASortedSetOfIds()
    {
        var info = Loaded(new Marks());
        Assert.False(Parse(Json(info)).TryGetProperty("usb", out _));

        var usb = Parse(Json(info, includeUsb: true)).GetProperty("usb");
        // 046d/c52b and 046D/C52B are one entry; the list is sorted; the ids are upper case.
        Assert.Equal(
            ["046D:C52B", "1532:0003"],
            usb.EnumerateArray().Select(u => u.GetProperty("vid").GetString() + ":" + u.GetProperty("pid").GetString()));
    }

    [Fact]
    public void UsbIdsThatAreNotFourHexDigitsAreDropped()
    {
        var m = new Marks();
        var devices = new List<UsbDevice>
        {
            new(Plain("n"), Plain("046D"), Plain("C52B"), Plain("m"), Plain("p")),
            new(Plain("n"), Plain("46D"), Plain("C52B"), Plain("m"), Plain("p")),
            new(Plain("n"), Plain("046D"), Plain("zzzz"), Plain("m"), Plain("p")),
            new(Plain("n"), Plain("USB\\VID_1"), Plain("C52B"), Plain("m"), Plain("p")),
        };
        var info = Loaded(m) with { Usb = Value.Ok<IReadOnlyList<UsbDevice>>(devices, "test") };

        var usb = Parse(Json(info, includeUsb: true)).GetProperty("usb");
        Assert.Equal(1, usb.GetArrayLength());
    }

    [Fact]
    public void MemoryAndStorageAreGroupedWithACount()
    {
        var root = Parse(Json(Loaded(new Marks())));

        var memory = Assert.Single(root.GetProperty("memory").EnumerateArray());
        Assert.Equal("Corsair", memory.GetProperty("manufacturer").GetString());
        Assert.Equal("CMK64GX5M2B5600Z40", memory.GetProperty("partNumber").GetString());
        Assert.Equal("DDR5", memory.GetProperty("type").GetString());
        Assert.Equal(34_359_738_368, memory.GetProperty("capacityBytes").GetInt64());
        Assert.Equal(2, memory.GetProperty("count").GetInt32());

        var storage = root.GetProperty("storage").EnumerateArray().ToList();
        Assert.Equal(2, storage.Count);
        Assert.Equal("Samsung SSD 980 PRO 2TB", storage[0].GetProperty("model").GetString());
        Assert.Equal("NVMe", storage[0].GetProperty("bus").GetString());
        Assert.Equal(2_000_398_934_016, storage[0].GetProperty("capacityBytes").GetInt64());
        Assert.Equal(2, storage[0].GetProperty("count").GetInt32());
        Assert.Equal("TOSHIBA MG04ACA200E", storage[1].GetProperty("model").GetString());
        Assert.Equal("SATA", storage[1].GetProperty("bus").GetString());
        Assert.Equal(1, storage[1].GetProperty("count").GetInt32());
    }

    [Fact]
    public void AnEmptyMemorySocketIsNotAModuleAndUnknownFieldsAreLeftOut()
    {
        var loaded = Loaded(new Marks());
        var memory = loaded.Memory.Data!;
        var empty = memory.Modules[0] with { CapacityBytes = Plain(0L), Manufacturer = Plain("Empty Maker"), PartNumber = Plain("EMPTY-PART") };
        var partial = memory.Modules[0] with { Manufacturer = Bad<string>(), PartNumber = Plain("   "), CapacityBytes = Bad<long>() };
        var nothing = memory.Modules[0] with { Manufacturer = Bad<string>(), PartNumber = Plain(" "), Type = Plain(MemoryType.Other), CapacityBytes = Bad<long>() };
        var info = loaded with { Memory = Plain(memory with { Modules = [empty, partial, nothing, memory.Modules[1]] }) };

        var json = Json(info);
        var items = Parse(json).GetProperty("memory").EnumerateArray().ToList();
        // The empty socket and the module with nothing known are not listed; what is not known is left out of the one that is.
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain("Empty Maker", json, StringComparison.Ordinal);
        Assert.Equal(["type", "count"], items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["manufacturer", "partNumber", "type", "capacityBytes", "count"], items[1].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void CpuIdIsDecimalFamilyModelStepping()
    {
        var cpu = Parse(Json(Loaded(new Marks()))).GetProperty("cpu")[0];
        Assert.Equal("AMD Ryzen 9 7950X 16-Core Processor", cpu.GetProperty("name").GetString());
        Assert.Equal("AMD", cpu.GetProperty("vendor").GetString());
        Assert.Equal("25-97-2", cpu.GetProperty("cpuid").GetString());
    }

    [Fact]
    public void CpuIdNeedsAllThreePartsAndAnUnreadableCpuIsAnEmptyList()
    {
        var m = new Marks();
        var loaded = Loaded(m);
        var noStepping = loaded with { Cpu = Plain(loaded.Cpu.Data! with { Stepping = Bad<int>() }) };
        Assert.False(Parse(Json(noStepping)).GetProperty("cpu")[0].TryGetProperty("cpuid", out _));

        var none = loaded with { Cpu = Value.Unsupported<CpuHardware>("test", "no cpu") };
        Assert.Equal(0, Parse(Json(none)).GetProperty("cpu").GetArrayLength());
    }

    [Fact]
    public void ABoardThatIsNotKnownIsLeftOut()
    {
        var loaded = Loaded(new Marks());
        var none = loaded with { Board = Value.Failed<BoardHardware>("test", "no smbios") };

        var root = Parse(Json(none));
        Assert.False(root.TryGetProperty("board", out _));
    }

    [Fact]
    public void GpusKeepTheIdsAndSkipTheSoftwareAdapter()
    {
        var gpus = Parse(Json(Loaded(new Marks()))).GetProperty("gpus").EnumerateArray().ToList();

        Assert.Equal(2, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 5070", gpus[0].GetProperty("name").GetString());
        Assert.Equal("10DE:2F04", gpus[0].GetProperty("pciId").GetString());
        Assert.Equal("1043:89E6", gpus[0].GetProperty("subsystemId").GetString());
        Assert.Equal("AMD Radeon(TM) Graphics", gpus[1].GetProperty("name").GetString());
        Assert.Equal("1002:164E", gpus[1].GetProperty("pciId").GetString());
        Assert.DoesNotContain("Basic Render", Json(Loaded(new Marks())), StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedIsOnlyForNvidiaWithAValidBoardDefault()
    {
        // A desktop: the loaded tree has a battery (for the privacy markers), and a battery would make it a laptop.
        var loaded = Loaded(new Marks()) with { Battery = Value.Unsupported<BatteryHardware>("test", "no battery") };
        var gpus = loaded.Gpus.Data!.ToList();
        var nvidia = gpus[0];

        // As built: NVIDIA with "board default 250 W".
        var observed = Parse(Json(loaded)).GetProperty("gpus")[0].GetProperty("observed");
        Assert.Equal(250, observed.GetProperty("nvmlDefaultPowerLimitW").GetDouble());
        Assert.Equal("desktop", observed.GetProperty("formFactor").GetString());

        // AMD, even with a power limit and the same words, is not an NVML reading.
        var amd = gpus[1] with { PowerLimitWatts = nvidia.PowerLimitWatts };
        Assert.False(OneGpu(loaded, amd).TryGetProperty("observed", out _));

        // No default in the Detail, no default read, an implausible default, or a limit that is not Ok.
        Assert.False(OneGpu(loaded, nvidia with { PowerLimitWatts = Value.Ok(250.0, "test") }).TryGetProperty("observed", out _));
        Assert.False(OneGpu(loaded, nvidia with { PowerLimitWatts = Bad<double>() }).TryGetProperty("observed", out _));
        Assert.False(OneGpu(loaded, nvidia with { PowerLimitWatts = new Value<double>(250, ValueStatus.Ok, "t", "board default 5 W") }).TryGetProperty("observed", out _));
        Assert.False(OneGpu(loaded, nvidia with { PowerLimitWatts = new Value<double>(250, ValueStatus.Ok, "t", "board default 5000 W") }).TryGetProperty("observed", out _));
        Assert.False(OneGpu(loaded, nvidia with { PowerLimitWatts = new Value<double>(250, ValueStatus.Denied, "t", "board default 250 W") }).TryGetProperty("observed", out _));
        Assert.True(OneGpu(loaded, nvidia with { PowerLimitWatts = new Value<double>(250, ValueStatus.Ok, "t", "board default 285.5 W") }).TryGetProperty("observed", out var o));
        Assert.Equal(285.5, o.GetProperty("nvmlDefaultPowerLimitW").GetDouble());
    }

    private static JsonElement OneGpu(HardwareInfo info, GpuHardware gpu) =>
        Parse(Json(info with { Gpus = Value.Ok<IReadOnlyList<GpuHardware>>([gpu], "test") })).GetProperty("gpus")[0];

    [Fact]
    public void AMalformedPciIdIsLeftOutNotPassedOn()
    {
        var loaded = Loaded(new Marks());
        var gpu = loaded.Gpus.Data![0] with { PciId = Plain("PCI\\VEN_10DE&DEV_0000") };
        var element = OneGpu(loaded, gpu with { Details = gpu.Details! with { SubsystemId = Plain("12345") } });

        Assert.False(element.TryGetProperty("pciId", out _));
        Assert.False(element.TryGetProperty("subsystemId", out _));
        Assert.Equal("NVIDIA GeForce RTX 5070", element.GetProperty("name").GetString());
    }

    [Fact]
    public void AGpuWithNothingToSayIsNotListed()
    {
        var loaded = Loaded(new Marks());
        var blank = loaded.Gpus.Data![0] with { Name = Bad<string>(), PciId = Bad<string>(), Details = null };
        var info = loaded with { Gpus = Value.Ok<IReadOnlyList<GpuHardware>>([blank], "test") };

        Assert.Equal(0, Parse(Json(info)).GetProperty("gpus").GetArrayLength());
    }

    [Theory]
    [InlineData("Desktop", false, "desktop")]
    [InlineData("Tower", false, "desktop")]
    [InlineData("Mini PC", false, "desktop")]
    [InlineData("Laptop", false, "laptop")]
    [InlineData("Notebook", false, "laptop")]
    [InlineData("Convertible", false, "laptop")]
    [InlineData("Desktop", true, "laptop")]
    [InlineData("Main server chassis", false, "unknown")]
    [InlineData("Other", false, "unknown")]
    [InlineData("Other (12)", false, "unknown")]
    [InlineData(null, false, "unknown")]
    [InlineData(null, true, "laptop")]
    public void FormFactorComesFromTheChassisAndTheBattery(string? chassis, bool battery, string expected)
    {
        var loaded = Loaded(new Marks());
        var board = loaded.Board.Data!;
        var details = board.Details! with
        {
            Chassis = board.Details.Chassis with { Type = chassis is null ? Bad<string>() : Plain(chassis) },
        };
        var info = loaded with
        {
            Board = Plain(board with { Details = details }),
            Battery = battery ? loaded.Battery : Value.Unsupported<BatteryHardware>("test", "no battery"),
        };

        Assert.Equal(expected, PartsShare.FormFactorOf(info));
        Assert.Equal(expected, OneGpu(info, info.Gpus.Data![0]).GetProperty("observed").GetProperty("formFactor").GetString());
    }

    [Fact]
    public void EveryChassisTypeNameIsClassifiedOnPurpose()
    {
        // BoardCollector.ChassisType can gain a name; this fails until the new name is put in one of the lists on purpose.
        string[] unknownOnPurpose = ["Main server chassis", "Rack mount", "IoT gateway", "Embedded PC", "Stick PC", "Other", "Unknown"];
        var unclassified = PartsShare.AllChassisNames()
            .Where(name => !PartsShare.LaptopChassis.Contains(name) && !PartsShare.DesktopChassis.Contains(name)
                           && !unknownOnPurpose.Contains(name) && !name.StartsWith("Other (", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.True(unclassified.Count == 0, "chassis names with no form factor: " + string.Join(", ", unclassified));

        var known = PartsShare.AllChassisNames().ToHashSet(StringComparer.Ordinal);
        Assert.All(PartsShare.LaptopChassis.Concat(PartsShare.DesktopChassis), name => Assert.Contains(name, known));
        Assert.Empty(PartsShare.LaptopChassis.Intersect(PartsShare.DesktopChassis));
    }

    // ---- the default power limit text ----

    [Theory]
    [InlineData(250.0)]
    [InlineData(285.5)]
    [InlineData(70.0)]
    [InlineData(1000.0)]
    public void TheBoardDefaultNoteRoundTrips(double watts)
    {
        var note = GpuSensorMerge.BoardDefaultNote(watts);
        Assert.True(GpuSensorMerge.TryParseBoardDefaultWatts("limit in force now; the driver allows 175 to 300 W, " + note, out var back));
        Assert.Equal(watts, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("limit in force now")]
    [InlineData("board default W")]
    [InlineData("board default -5 W")]
    [InlineData("board default 1e3 W")]
    [InlineData("board default 250 mW")]
    [InlineData("board default 250")]
    [InlineData("board default NaN W")]
    public void TheBoardDefaultParserRejectsAnythingThatIsNotThePlainNote(string? detail) =>
        Assert.False(GpuSensorMerge.TryParseBoardDefaultWatts(detail, out _));


    private static GpuHardware Merged(double? limitDefault)
    {
        const string luid = "luid_0x00000000_0x0001a2b3";
        var merged = GpuSensorMerge.Apply(
            SensorFixtures.Gpus(SensorFixtures.Gpu(luid)),
            SensorFixtures.Readings(SensorFixtures.Reading(luid, extras: SensorFixtures.Extras(limitDefault: limitDefault))));
        return Assert.Single(merged.Data!);
    }

    [Fact]
    public void ThePowerLimitDetailTheSensorMergeWritesIsWhatTheDocumentReads()
    {
        // End to end through the real merge: SensorHost extras with a board default of 250 W end up as "observed".
        var merged = Merged(limitDefault: 250);
        Assert.True(merged.PowerLimitWatts.IsOk);
        Assert.True(GpuSensorMerge.TryParseBoardDefaultWatts(merged.PowerLimitWatts.Detail, out var watts));
        Assert.Equal(250, watts);

        var loaded = Loaded(new Marks());
        var observed = OneGpu(loaded, loaded.Gpus.Data![0] with { PowerLimitWatts = merged.PowerLimitWatts }).GetProperty("observed");
        Assert.Equal(250, observed.GetProperty("nvmlDefaultPowerLimitW").GetDouble());

        // A SensorHost that gave no default leaves "observed" out.
        var noDefault = Merged(limitDefault: null);
        Assert.False(OneGpu(loaded, loaded.Gpus.Data![0] with { PowerLimitWatts = noDefault.PowerLimitWatts }).TryGetProperty("observed", out _));
    }
}
