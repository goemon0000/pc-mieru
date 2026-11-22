using System.Text.Json;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Share;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>
/// Hostile data in the fields the document does read: free text in the board revision, drives that were plugged in or mounted,
/// adapters in a different order from one run to the next, a sensor host that is slow with its first reading.
/// </summary>
public partial class PartsShareTests
{
    // ---- the board revision ----

    [Theory]
    [InlineData("KBC Version 08.4F.00")]
    [InlineData("EC FW 1.2.3")]
    [InlineData("SN-48151623")]
    [InlineData("Rev 1.xx")]
    public void TheBoardRevisionIsNeverExported(string revision)
    {
        // SMBIOS puts anything in the revision field: an embedded controller firmware version, or an id of this very unit.
        var loaded = Loaded(new Marks());
        var info = loaded with { Board = Plain(loaded.Board.Data! with { Revision = Plain(revision) }) };

        var json = Json(info, includeUsb: true);
        Assert.DoesNotContain(revision, json, StringComparison.Ordinal);
        Assert.DoesNotContain("revision", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["manufacturer", "product"], Parse(json).GetProperty("board").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void ABoardKnownOnlyByItsRevisionIsLeftOut()
    {
        var loaded = Loaded(new Marks());
        var onlyRevision = loaded.Board.Data! with { Manufacturer = Bad<string>(), Product = Bad<string>(), Revision = Plain("KBC Version 08.4F.00") };

        var json = Json(loaded with { Board = Plain(onlyRevision) });
        Assert.False(Parse(json).TryGetProperty("board", out _));
        Assert.DoesNotContain("KBC", json, StringComparison.Ordinal);
    }

    // ---- storage that is not part of the PC ----

    private static HardwareInfo WithDrives(HardwareInfo loaded, params StorageDevice[] drives) =>
        loaded with { Storage = Value.Ok<IReadOnlyList<StorageDevice>>(drives, "test") };

    private static List<JsonElement> StorageOf(string json) => [.. Parse(json).GetProperty("storage").EnumerateArray()];

    [Theory]
    [InlineData("SanDisk Cruzer Blade", StorageBus.Usb, true)]
    [InlineData("Samsung PSSD T7", StorageBus.Usb, false)]
    [InlineData("WD My Passport 25E2", StorageBus.Usb, false)]
    // The removable flag alone is enough, on any bus.
    [InlineData("Ordinary Looking SSD", StorageBus.Sata, true)]
    [InlineData("Ordinary Looking Disk", StorageBus.Other, true)]
    [InlineData("Ordinary Looking NVMe", StorageBus.Nvme, true)]
    // Virtual disks: a mounted VHD or a hypervisor's disk, whatever bus the driver gives them.
    [InlineData("Msft Virtual Disk", StorageBus.Virtual, false)]
    [InlineData("Msft Virtual Disk", StorageBus.Scsi, false)]
    [InlineData("VMware Virtual disk SCSI Disk Device", StorageBus.Scsi, false)]
    [InlineData("VBOX HARDDISK Virtual", StorageBus.Sata, false)]
    // A network target and a pool are not a disk in this PC.
    [InlineData("NETAPP LUN C-Mode", StorageBus.Iscsi, false)]
    [InlineData("Microsoft Storage Space Device", StorageBus.Spaces, false)]
    // Nothing says it is built in.
    [InlineData("Mystery Disk", StorageBus.Unknown, false)]
    public void AStorageDeviceThatWasPluggedInOrMountedIsNotListed(string model, StorageBus bus, bool removable)
    {
        var m = new Marks();
        var loaded = Loaded(m);
        var info = WithDrives(
            loaded,
            Drive(m, 10, "Samsung SSD 980 PRO 2TB", StorageBus.Nvme, 2_000_398_934_016),
            Drive(m, 11, model, bus, 1_000_204_886_016, removable),
            Drive(m, 12, "TOSHIBA MG04ACA200E", StorageBus.Sata, 2_000_398_934_016));

        var json = Json(info);
        Assert.DoesNotContain(model, json, StringComparison.Ordinal);
        Assert.Equal(["Samsung SSD 980 PRO 2TB", "TOSHIBA MG04ACA200E"], StorageOf(json).Select(s => s.GetProperty("model").GetString()));
    }

    [Fact]
    public void AStorageDeviceWhoseBusCannotBeReadIsNotListed()
    {
        var m = new Marks();
        var info = WithDrives(
            Loaded(m),
            Drive(m, 10, "Samsung SSD 980 PRO 2TB", StorageBus.Nvme, 2_000_398_934_016),
            Drive(m, 11, "Bus Unreadable Disk", Bad<StorageBus>(), 1_000_204_886_016));

        var json = Json(info);
        Assert.DoesNotContain("Bus Unreadable", json, StringComparison.Ordinal);
        Assert.Single(StorageOf(json));
    }

    [Fact]
    public void TheBusesOfBuiltInDrivesStayListedAndAnOtherBusIsOmittedNotGuessed()
    {
        var m = new Marks();
        var info = WithDrives(
            Loaded(m),
            Drive(m, 10, "Seagate ST4000NM", StorageBus.Scsi, 4_000_787_030_016),
            Drive(m, 11, "TOSHIBA MG04ACA200E", StorageBus.Sata, 2_000_398_934_016),
            Drive(m, 12, "Dell PERC H755 Volume", StorageBus.Raid, 1_000_204_886_016),
            Drive(m, 13, "Samsung SSD 980 PRO 2TB", StorageBus.Nvme, 2_000_398_934_016),
            Drive(m, 14, "Weird Bridge Disk", StorageBus.Other, 500_107_862_016));

        var items = StorageOf(Json(info));
        // The bus text sorts the groups (a group with no bus text first), then the model.
        Assert.Equal(
            ["Weird Bridge Disk", "Samsung SSD 980 PRO 2TB", "Dell PERC H755 Volume", "TOSHIBA MG04ACA200E", "Seagate ST4000NM"],
            items.Select(s => s.GetProperty("model").GetString()));
        Assert.Equal(
            [null, "NVMe", "RAID", "SATA", "SCSI"],
            items.Select(s => s.TryGetProperty("bus", out var bus) ? bus.GetString() : null));
    }

    [Fact]
    public void TheOnlyStorageOfAPcWithOnlyOutsideDrivesIsAnEmptyList()
    {
        var m = new Marks();
        var info = WithDrives(Loaded(m), Drive(m, 10, "SanDisk Cruzer Blade", StorageBus.Usb, 15_376_000_000, removable: true));

        Assert.Equal(0, Parse(Json(info)).GetProperty("storage").GetArrayLength());
    }

    [Theory]
    [InlineData(0, StorageBus.Unknown)]
    [InlineData(1, StorageBus.Scsi)]
    [InlineData(3, StorageBus.Sata)]
    [InlineData(7, StorageBus.Usb)]
    [InlineData(8, StorageBus.Raid)]
    [InlineData(9, StorageBus.Iscsi)]
    [InlineData(10, StorageBus.Scsi)]
    [InlineData(11, StorageBus.Sata)]
    [InlineData(14, StorageBus.Virtual)]
    [InlineData(15, StorageBus.Virtual)]
    [InlineData(16, StorageBus.Spaces)]
    [InlineData(17, StorageBus.Nvme)]
    [InlineData(2, StorageBus.Other)]
    [InlineData(12, StorageBus.Other)]
    [InlineData(13, StorageBus.Other)]
    [InlineData(99, StorageBus.Other)]
    public void TheWindowsBusTypeIsMappedToTheBusTheDocumentReasonsAbout(int busType, StorageBus expected) =>
        Assert.Equal(expected, StorageCollector.MapBus(busType));

    // ---- a fixed order ----

    /// <summary>Every ordering of <paramref name="items"/> (Heap's algorithm), the given order first.</summary>
    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> items)
    {
        var a = items.ToArray();
        var c = new int[a.Length];
        yield return a.ToArray();
        var i = 0;
        while (i < a.Length)
        {
            if (c[i] < i)
            {
                var k = i % 2 == 0 ? 0 : c[i];
                (a[k], a[i]) = (a[i], a[k]);
                yield return a.ToArray();
                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }

    [Fact]
    public void ThePermutationHelperWalksEveryOrderOnce()
    {
        var all = Permutations(new[] { 1, 2, 3, 4 }).Select(p => string.Join(',', p)).ToList();
        Assert.Equal(24, all.Count);
        Assert.Equal(24, all.Distinct().Count());
        Assert.Equal("1,2,3,4", all[0]);
    }

    [Fact]
    public void GpusComeOutInTheSameOrderWhateverOrderWindowsListsThem()
    {
        var loaded = Loaded(new Marks());
        var nvidia = loaded.Gpus.Data![0];
        var amd = loaded.Gpus.Data![1];

        GpuHardware Card(GpuHardware basis, string name, string sub) =>
            basis with { Name = Plain(name), Details = basis.Details! with { SubsystemId = Plain(sub) } };

        // The first three share the PCI id (a board partner's sub-vendor differs), two of them share the subsystem id as well.
        GpuHardware[] gpus =
        [
            Card(nvidia, "NVIDIA GeForce RTX 5070", "1043:89E6"),
            Card(nvidia, "NVIDIA GeForce RTX 5070", "1458:4100"),
            Card(nvidia, "NVIDIA GeForce RTX 5070 Ti", "1043:89E6"),
            amd,
        ];

        var documents = Permutations(gpus)
            .Select(order => Json(loaded with { Gpus = Value.Ok<IReadOnlyList<GpuHardware>>(order, "test") }))
            .ToList();
        Assert.Equal(24, documents.Count);
        Assert.All(documents, json => Assert.Equal(documents[0], json));

        var items = Parse(documents[0]).GetProperty("gpus").EnumerateArray().ToList();
        Assert.Equal(
            ["AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 5070", "NVIDIA GeForce RTX 5070 Ti", "NVIDIA GeForce RTX 5070"],
            items.Select(g => g.GetProperty("name").GetString()));
        Assert.Equal(
            ["1002:164E", "10DE:2F04", "10DE:2F04", "10DE:2F04"],
            items.Select(g => g.GetProperty("pciId").GetString()));
        Assert.Equal(
            ["1043:8877", "1043:89E6", "1043:89E6", "1458:4100"],
            items.Select(g => g.GetProperty("subsystemId").GetString()));
    }

    [Fact]
    public void StorageComesOutInTheSameOrderWhateverOrderWindowsListsIt()
    {
        var m = new Marks();
        var loaded = Loaded(m);
        const long Terabyte = 1_000_204_886_016;
        const long HalfTerabyte = 500_107_862_016;
        StorageDevice[] drives =
        [
            Drive(m, 0, "WD Blue SN580", StorageBus.Nvme, Terabyte),
            Drive(m, 1, "WD Blue SN580", StorageBus.Nvme, Terabyte),
            Drive(m, 2, "WD Blue SN580", StorageBus.Nvme, HalfTerabyte),
            Drive(m, 3, "WD Blue SA510", StorageBus.Sata, Terabyte),
            Drive(m, 4, "Crucial MX500", StorageBus.Sata, HalfTerabyte),
            Drive(m, 5, "Samsung 870 EVO", StorageBus.Sata, Terabyte),
        ];

        var documents = Permutations(drives).Select(order => Json(WithDrives(loaded, order))).ToList();
        Assert.Equal(720, documents.Count);
        Assert.All(documents, json => Assert.Equal(documents[0], json));

        var items = StorageOf(documents[0]);
        Assert.Equal(
            ["WD Blue SN580", "WD Blue SN580", "Crucial MX500", "Samsung 870 EVO", "WD Blue SA510"],
            items.Select(s => s.GetProperty("model").GetString()));
        Assert.Equal([HalfTerabyte, Terabyte, HalfTerabyte, Terabyte, Terabyte], items.Select(s => s.GetProperty("capacityBytes").GetInt64()));
        Assert.Equal([1, 2, 1, 1, 1], items.Select(s => s.GetProperty("count").GetInt32()));
    }

    private static DisplayDevice DisplayOf(string maker, int code, string? name, int widthMm, int heightMm = 336) => new(
        Plain("make"), Plain("model"), Plain(2024), Plain(14), Plain(widthMm), Plain(heightMm), Plain("2560x1440 @ 60 Hz"), Plain("2560x1440"), Plain(60.0), Plain(false),
        Plain(DisplayConnection.DisplayPort), Plain("gpu"),
        new DisplayDetails(
            Plain(maker), Plain(code), Plain("edid serial"), Plain("1.4"), Plain(true), Plain(1), Plain(27.0), Plain("digital"), Plain(8),
            name is null ? Bad<string>() : Plain(name), Plain(48.0), Plain(60.0), Plain(true), Plain(true), Plain(400)));

    [Fact]
    public void DisplaysComeOutInTheSameOrderWhateverOrderWindowsListsThem()
    {
        var loaded = Loaded(new Marks());
        DisplayDevice[] displays =
        [
            DisplayOf("AOC", 0x2790, null, 597),
            DisplayOf("AOC", 0x2790, "AOC 27G2", 597),
            DisplayOf("DEL", 0x0102, "DELL P2419H", 527),
            DisplayOf("DEL", 0xA0B1, "DELL U2720Q", 597),
            DisplayOf("DEL", 0xA0B1, "DELL U2723QE", 597),
            DisplayOf("DEL", 0xA0B1, "DELL U2723QE", 598),
        ];

        var documents = Permutations(displays)
            .Select(order => Json(loaded with { Displays = Value.Ok<IReadOnlyList<DisplayDevice>>(order, "test") }))
            .ToList();
        Assert.Equal(720, documents.Count);
        Assert.All(documents, json => Assert.Equal(documents[0], json));

        var items = Parse(documents[0]).GetProperty("displays").EnumerateArray().ToList();
        Assert.Equal(
            ["AOC 2790 - 597", "AOC 2790 AOC 27G2 597", "DEL 0102 DELL P2419H 527", "DEL A0B1 DELL U2720Q 597", "DEL A0B1 DELL U2723QE 597", "DEL A0B1 DELL U2723QE 598"],
            items.Select(d => string.Join(
                ' ',
                d.GetProperty("edidManufacturer").GetString(),
                d.GetProperty("edidProductCode").GetString(),
                d.TryGetProperty("edidModel", out var model) ? model.GetString() : "-",
                d.GetProperty("physicalWidthMm").GetInt32())));
    }

    [Fact]
    public void MemoryKeepsTheOrderOfTheSlotsGroupedByFirstAppearance()
    {
        var loaded = Loaded(new Marks());
        var memory = loaded.Memory.Data!;
        MemoryModule Stick(string maker) => memory.Modules[0] with { Manufacturer = Plain(maker) };

        var info = loaded with { Memory = Plain(memory with { Modules = [Stick("Zed"), Stick("Alpha"), Stick("Zed"), Stick("Mid")] }) };

        var items = Parse(Json(info)).GetProperty("memory").EnumerateArray().ToList();
        Assert.Equal(["Zed", "Alpha", "Mid"], items.Select(i => i.GetProperty("manufacturer").GetString()));
        Assert.Equal([2, 1, 1], items.Select(i => i.GetProperty("count").GetInt32()));
    }

    // ---- the first sensor reading ----

    private static Value<double> TimedOut() => Value.Missing<double>(ValueStatus.Timeout, "test", "the sensor host had no first reading");

    private static Value<double> ReadWithDefault() => new(250, ValueStatus.Ok, "test", "limit in force now; board default 250 W");

    /// <summary>The loaded tree with one NVIDIA card whose power limit reads as given.</summary>
    private static HardwareInfo OneNvidia(HardwareInfo loaded, Value<double> limit) =>
        loaded with { Gpus = Value.Ok<IReadOnlyList<GpuHardware>>([loaded.Gpus.Data![0] with { PowerLimitWatts = limit }], "test") };

    /// <summary>Runs CollectAsync against a reader that gives <paramref name="onGpuRead"/>(n) for the n-th read (from 0) of the GPU category.</summary>
    private static async Task<(HardwareInfo Info, int GpuReads)> CollectCounting(Func<int, HardwareInfo> onGpuRead, CancellationToken ct = default)
    {
        var loaded = Loaded(new Marks());
        var reads = 0;
        var info = await PartsShare.CollectAsync(
            (category, _) => Task.FromResult(category == HardwareCategory.Gpu ? onGpuRead(Interlocked.Increment(ref reads) - 1) : loaded),
            includeUsb: false,
            ct);
        return (info, reads);
    }

    private static bool HasObserved(HardwareInfo info) =>
        Parse(PartsShare.ToJson(PartsShare.Build(info, AppVersion, includeUsb: false))).GetProperty("gpus")[0].TryGetProperty("observed", out _);

    [Fact]
    public async Task AFirstReadingThatTimedOutIsTriedAgainAndObservedIsThere()
    {
        var loaded = Loaded(new Marks());

        var (info, reads) = await CollectCounting(n => OneNvidia(loaded, n == 0 ? TimedOut() : ReadWithDefault()));

        Assert.Equal(2, reads);
        Assert.True(HasObserved(info));
    }

    [Fact]
    public async Task ASensorHostThatNeverAnswersIsWaitedForOnlyAFixedNumberOfTimes()
    {
        var loaded = Loaded(new Marks());

        var (info, reads) = await CollectCounting(_ => OneNvidia(loaded, TimedOut()));

        Assert.Equal(1 + PartsShare.MaxSensorRetries, reads);
        Assert.False(HasObserved(info));
    }

    [Fact]
    public async Task AReadingThatIsThereTheFirstTimeIsNotReadAgain()
    {
        var loaded = Loaded(new Marks());

        var (info, reads) = await CollectCounting(_ => OneNvidia(loaded, ReadWithDefault()));

        Assert.Equal(1, reads);
        Assert.True(HasObserved(info));
    }

    [Theory]
    [InlineData(ValueStatus.Unsupported)]
    [InlineData(ValueStatus.Denied)]
    [InlineData(ValueStatus.Error)]
    [InlineData(ValueStatus.Gone)]
    [InlineData(ValueStatus.NotCollected)]
    public async Task OnlyATimeoutIsWorthAnotherRead(ValueStatus status)
    {
        var loaded = Loaded(new Marks());

        var (info, reads) = await CollectCounting(_ => OneNvidia(loaded, Value.Missing<double>(status, "test", "board default 250 W")));

        Assert.Equal(1, reads);
        Assert.False(HasObserved(info));
    }

    [Fact]
    public async Task ACardThatIsNotNvidiaDoesNotWaitForTheSensorHost()
    {
        var loaded = Loaded(new Marks());
        var amd = loaded.Gpus.Data![1] with { PowerLimitWatts = TimedOut() };

        var (_, reads) = await CollectCounting(_ => loaded with { Gpus = Value.Ok<IReadOnlyList<GpuHardware>>([amd], "test") });

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task GpusThatCouldNotBeReadAreNotReadAgain()
    {
        var loaded = Loaded(new Marks());

        var (info, reads) = await CollectCounting(_ => loaded with { Gpus = Value.Failed<IReadOnlyList<GpuHardware>>("test", "no adapters") });

        Assert.Equal(1, reads);
        Assert.Equal(0, Parse(PartsShare.ToJson(PartsShare.Build(info, AppVersion, includeUsb: false))).GetProperty("gpus").GetArrayLength());
    }

    [Fact]
    public async Task CancellingDuringTheFirstReadStopsBeforeTheSecond()
    {
        var loaded = Loaded(new Marks());
        using var cts = new CancellationTokenSource();
        var reads = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await PartsShare.CollectAsync(
                (category, _) =>
                {
                    if (category != HardwareCategory.Gpu)
                    {
                        return Task.FromResult(loaded);
                    }

                    Interlocked.Increment(ref reads);
                    cts.Cancel();
                    return Task.FromResult(OneNvidia(loaded, TimedOut()));
                },
                includeUsb: false,
                cts.Token);
        });
        Assert.Equal(1, reads);
    }
}
