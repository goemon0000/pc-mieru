using PcMieru.Core;
using PcMieru.Party.Hardware;

namespace PcMieru.Party.Core.Tests;

/// <summary>
/// The projection from a Core Snapshot to what the board reads (DESIGN-C 2.3). The point of most of these: a value Core could not read
/// stays missing, because the old experience rule pays out even at zero load and would pay on a made-up zero.
/// </summary>
public class BoardFrameTests
{
    private const long GiB = 1L << 30;
    private static readonly DateTime Now = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static Value<double> Ok(double v) => Value.Ok(v, "test");

    private static Value<double> Gone() => Value.NotCollected<double>("test", "no baseline yet");

    private static CpuInfo Cpu(Value<double> total, int logical = 16) => new(logical, total, [], Gone());

    private static MemoryInfo Memory(long total, long available) => new(
        total, available,
        Value.NotCollected<long>("test"), Value.NotCollected<long>("test"), Value.NotCollected<long>("test"),
        Value.NotCollected<long>("test"), Value.NotCollected<long>("test"));

    private static GpuInfo Gpu(string id, string name, long vram, Value<double>? util = null, long used = 1 * GiB) => new(
        id, name, util ?? Ok(10), [],
        Value.Ok(used, "test"),
        vram > 0 ? Value.Ok(vram, "test") : Value.Unsupported<long>("test"),
        Value.NotCollected<long>("test"),
        Value.NotCollected<double>("test"), Value.NotCollected<double>("test"), Value.NotCollected<double>("test"), Value.NotCollected<double>("test"));

    private static DiskInfo Disk(string id, Value<double> read, Value<double> write) => new(id, id, Ok(1), read, write, Ok(1));

    private static NicInfo Nic(string id, bool up, Value<double> send, Value<double> receive) =>
        new(id, id, up, send, receive, Value.NotCollected<long>("test"));

    private static Snapshot Snap(
        CpuInfo? cpu = null,
        MemoryInfo? memory = null,
        IReadOnlyList<DiskInfo>? disks = null,
        IReadOnlyList<NicInfo>? nics = null,
        IReadOnlyList<GpuInfo>? gpus = null,
        Value<int>? gpusState = null,
        StorageTopology? storage = null)
    {
        var gpuList = gpus ?? [];
        var system = new SystemInfo(
            cpu ?? Cpu(Gone()),
            memory ?? Memory(0, 0),
            disks ?? [],
            nics ?? [],
            gpuList,
            100,
            DisksState: Value.Ok((disks ?? []).Count, "test"),
            NicsState: Value.Ok((nics ?? []).Count, "test"),
            GpusState: gpusState ?? Value.Ok(gpuList.Count, "test"))
        {
            Storage = storage,
        };
        return new Snapshot(Snapshot.CurrentSchemaVersion, Now, Now.AddSeconds(-1), 1000, system, []);
    }

    [Fact]
    public void A_complete_snapshot_projects_every_number()
    {
        var frame = BoardFrame.Project(Snap(
            Cpu(Ok(42.5), logical: 24),
            Memory(32 * GiB, 8 * GiB),
            [Disk("0", Ok(100), Ok(50)), Disk("1", Ok(10), Ok(5))],
            [Nic("eth", true, Ok(1000), Ok(2000)), Nic("wifi", true, Ok(10), Ok(20))],
            [Gpu("g0", "RTX 5070", 12 * GiB, Ok(80))]));

        Assert.Equal(24, frame.LogicalProcessors);
        Assert.Equal(42.5, frame.CpuPercent);
        Assert.Equal(new BoardMemory(24 * GiB, 32 * GiB, 75), frame.Memory);
        Assert.Equal(new BoardDisk(165, false), frame.Disk);
        Assert.Equal(new BoardNet(1010, 2020, false), frame.Net);
        var gpu = Assert.Single(frame.Gpus!);
        Assert.Equal(new BoardGpu("g0", "RTX 5070", 80, 1 * GiB, 12 * GiB), gpu);
        Assert.Equal(80, frame.GpuMaxPercent);
    }

    [Fact]
    public void An_empty_snapshot_projects_to_all_missing_not_to_zeros()
    {
        var frame = BoardFrame.Project(Snap(gpusState: Value.NotCollected<int>("test", "GPU sampling is off")));

        Assert.Null(frame.CpuPercent);
        Assert.Null(frame.Memory);
        Assert.Null(frame.Gpus);
        Assert.Null(frame.GpuMaxPercent);
        Assert.Equal(new BoardDisk(null, false), frame.Disk);
        Assert.Equal(new BoardNet(null, null, false), frame.Net);
    }

    [Theory]
    [InlineData(ValueStatus.NotCollected)]
    [InlineData(ValueStatus.Error)]
    [InlineData(ValueStatus.Timeout)]
    public void A_cpu_that_could_not_be_read_stays_missing(ValueStatus status)
    {
        var frame = BoardFrame.Project(Snap(Cpu(Value.Missing<double>(status, "test", "why"), logical: 8)));

        Assert.Null(frame.CpuPercent);
        Assert.Equal(8, frame.LogicalProcessors);
    }

    [Fact]
    public void The_cpu_number_is_kept_inside_zero_to_one_hundred()
    {
        Assert.Equal(100, BoardFrame.Project(Snap(Cpu(Ok(130)))).CpuPercent);
        Assert.Equal(0, BoardFrame.Project(Snap(Cpu(Ok(0)))).CpuPercent);
        Assert.Null(BoardFrame.Project(Snap(Cpu(Ok(double.NaN)))).CpuPercent);
    }

    [Fact]
    public void Memory_percent_is_not_made_when_the_total_is_zero()
    {
        Assert.Null(BoardFrame.Project(Snap(memory: Memory(0, 0))).Memory);
        Assert.Null(BoardFrame.Project(Snap(memory: Memory(0, 5 * GiB))).Memory);
        Assert.Null(BoardFrame.Project(Snap(memory: Memory(-1, 0))).Memory);
    }

    [Fact]
    public void Memory_whose_available_number_is_outside_the_total_is_missing()
    {
        Assert.Null(BoardFrame.Project(Snap(memory: Memory(16 * GiB, 17 * GiB))).Memory);
        Assert.Null(BoardFrame.Project(Snap(memory: Memory(16 * GiB, -1))).Memory);
    }

    [Fact]
    public void Memory_at_the_edges_of_the_range_is_still_made()
    {
        Assert.Equal(0, BoardFrame.Project(Snap(memory: Memory(16 * GiB, 16 * GiB))).Memory!.Percent);
        Assert.Equal(100, BoardFrame.Project(Snap(memory: Memory(16 * GiB, 0))).Memory!.Percent);
    }

    [Fact]
    public void Gpus_are_in_the_old_order_vram_then_name_then_luid()
    {
        var frame = BoardFrame.Project(Snap(gpus:
        [
            Gpu("luid_0x00000000_0x00000010", "Radeon Graphics", 512L * 1024 * 1024),
            Gpu("luid_0x00000000_0x00000030", "GeForce RTX 5070", 12 * GiB),
            Gpu("luid_0x00000000_0x00000020", "GeForce RTX 5070", 12 * GiB),
            Gpu("luid_0x00000000_0x00000040", "GeForce RTX 3080 Ti", 12 * GiB),
            Gpu("luid_0x00000000_0x00000050", "RTX Big", 24 * GiB),
        ]));

        Assert.Equal(
            new[] { "RTX Big", "GeForce RTX 3080 Ti", "GeForce RTX 5070", "GeForce RTX 5070", "Radeon Graphics" },
            frame.Gpus!.Select(g => g.Name).ToArray());
        Assert.Equal("luid_0x00000000_0x00000020", frame.Gpus![2].Id);
        Assert.Equal("luid_0x00000000_0x00000030", frame.Gpus![3].Id);
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_the_snapshot_listed_them_in()
    {
        var a = Gpu("a", "Alpha", 8 * GiB);
        var b = Gpu("b", "Beta", 8 * GiB);
        var c = Gpu("c", "Gamma", 16 * GiB);

        var one = BoardFrame.Project(Snap(gpus: [a, b, c])).Gpus!.Select(g => g.Id);
        var two = BoardFrame.Project(Snap(gpus: [c, b, a])).Gpus!.Select(g => g.Id);

        Assert.Equal(new[] { "c", "a", "b" }, one);
        Assert.Equal(one, two);
    }

    [Fact]
    public void At_most_six_gpus_are_listed_but_the_busiest_of_all_is_still_the_maximum()
    {
        var gpus = Enumerable.Range(0, 8)
            .Select(i => Gpu("g" + i, "GPU " + i, (100 - i) * GiB, Ok(i == 7 ? 95 : 10)))
            .ToArray();

        var frame = BoardFrame.Project(Snap(gpus: gpus));

        Assert.Equal(BoardFrame.MaxGpus, frame.Gpus!.Count);
        Assert.Equal(6, frame.Gpus.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => "g" + i), frame.Gpus.Select(g => g.Id));
        Assert.Equal(95, frame.GpuMaxPercent);
    }

    [Fact]
    public void A_gpu_without_a_utilization_keeps_its_row_with_a_missing_number()
    {
        var frame = BoardFrame.Project(Snap(gpus:
        [
            Gpu("g0", "Busy", 12 * GiB, Ok(60)),
            Gpu("g1", "NoNumber", 8 * GiB, Gone()),
        ]));

        Assert.Equal(2, frame.Gpus!.Count);
        Assert.Equal(60, frame.Gpus[0].Percent);
        Assert.Null(frame.Gpus[1].Percent);
        Assert.Equal(60, frame.GpuMaxPercent);
    }

    [Fact]
    public void When_no_gpu_has_a_utilization_the_maximum_is_missing_not_zero()
    {
        var frame = BoardFrame.Project(Snap(gpus: [Gpu("g0", "A", 8 * GiB, Gone()), Gpu("g1", "B", 4 * GiB, Gone())]));

        Assert.Equal(2, frame.Gpus!.Count);
        Assert.Null(frame.GpuMaxPercent);
    }

    [Fact]
    public void A_gpu_without_a_vram_total_reports_it_missing()
    {
        var frame = BoardFrame.Project(Snap(gpus: [Gpu("g0", "Shared only", 0, Ok(5))]));

        Assert.Null(frame.Gpus![0].VramTotalBytes);
        Assert.Equal(1 * GiB, frame.Gpus[0].VramUsedBytes);
    }

    [Fact]
    public void An_empty_gpu_list_means_no_gpu_only_when_core_says_it_read_the_list()
    {
        var none = BoardFrame.Project(Snap(gpus: [], gpusState: Value.Ok(0, "test")));
        var unknown = BoardFrame.Project(Snap(gpus: [], gpusState: Value.Missing<int>(ValueStatus.Error, "test", "counters unreadable")));

        Assert.NotNull(none.Gpus);
        Assert.Empty(none.Gpus!);
        Assert.Null(none.GpuMaxPercent);
        Assert.Null(unknown.Gpus);
        Assert.Null(unknown.GpuMaxPercent);
    }

    [Fact]
    public void Disk_with_no_readable_disk_is_missing_not_zero()
    {
        var frame = BoardFrame.Project(Snap(disks: [Disk("0", Gone(), Gone()), Disk("1", Ok(5), Gone())]));

        Assert.Equal(new BoardDisk(null, false), frame.Disk);
    }

    [Fact]
    public void A_disk_with_one_missing_direction_is_left_out_and_the_total_says_partial()
    {
        var frame = BoardFrame.Project(Snap(disks: [Disk("0", Ok(100), Ok(50)), Disk("1", Ok(5), Gone())]));

        Assert.Equal(new BoardDisk(150, true), frame.Disk);
    }

    [Fact]
    public void A_disk_that_is_idle_counts_as_zero_because_it_was_measured()
    {
        var frame = BoardFrame.Project(Snap(disks: [Disk("0", Ok(0), Ok(0))]));

        Assert.Equal(new BoardDisk(0, false), frame.Disk);
    }

    [Fact]
    public void Network_counts_only_adapters_that_are_up()
    {
        var frame = BoardFrame.Project(Snap(nics: [Nic("up", true, Ok(10), Ok(20)), Nic("down", false, Ok(1000), Ok(2000))]));

        Assert.Equal(new BoardNet(10, 20, false), frame.Net);
    }

    [Fact]
    public void Network_with_a_missing_number_on_an_up_adapter_is_partial()
    {
        var frame = BoardFrame.Project(Snap(nics:
        [
            Nic("a", true, Ok(10), Ok(20)),
            Nic("b", true, Ok(5), Gone()),
        ]));

        Assert.Equal(new BoardNet(15, 20, true), frame.Net);
    }

    [Fact]
    public void Network_with_nothing_readable_is_missing_and_a_down_adapters_gap_is_not_partial()
    {
        var unreadable = BoardFrame.Project(Snap(nics: [Nic("a", true, Gone(), Gone())]));
        var downGap = BoardFrame.Project(Snap(nics: [Nic("a", true, Ok(1), Ok(2)), Nic("b", false, Gone(), Gone())]));

        Assert.Equal(new BoardNet(null, null, true), unreadable.Net);
        Assert.Equal(new BoardNet(1, 2, false), downGap.Net);
    }

    [Fact]
    public void Projecting_the_same_snapshot_twice_gives_equal_frames()
    {
        var s = Snap(Cpu(Ok(10)), Memory(8 * GiB, 4 * GiB), [Disk("0", Ok(1), Ok(2))], [Nic("a", true, Ok(1), Ok(2))], [Gpu("g", "G", GiB)]);

        var one = BoardFrame.Project(s);
        var two = BoardFrame.Project(s);

        Assert.Equal(one.Memory, two.Memory);
        Assert.Equal(one.Disk, two.Disk);
        Assert.Equal(one.Net, two.Net);
        Assert.Equal(one.Gpus!.Select(g => g.Id), two.Gpus!.Select(g => g.Id));
    }

    [Fact]
    public void Cards_with_equal_vram_are_ordered_by_pci_bus_like_the_old_roster_whatever_the_listing_order()
    {
        // Same name, same memory: only the bus tells them apart, and the LUIDs here would put them the other way round.
        var lowBus = Gpu("luid_0x00000000_0x00000099", "GeForce RTX 3080 Ti", 12 * GiB) with { PciBus = 3 };
        var highBus = Gpu("luid_0x00000000_0x00000001", "GeForce RTX 3080 Ti", 12 * GiB) with { PciBus = 9 };
        var noBus = Gpu("luid_0x00000000_0x00000000", "GeForce RTX 3080 Ti", 12 * GiB);

        var one = BoardFrame.Project(Snap(gpus: [highBus, noBus, lowBus]));
        var two = BoardFrame.Project(Snap(gpus: [noBus, lowBus, highBus]));

        var expected = new[] { lowBus.Id, highBus.Id, noBus.Id }; // a card whose bus is unknown goes after the ones that have one
        Assert.Equal(expected, one.Gpus!.Select(g => g.Id).ToArray());
        Assert.Equal(expected, two.Gpus!.Select(g => g.Id).ToArray());
        Assert.Equal(new uint?[] { 3, 9, null }, one.Gpus!.Select(g => g.PciBus).ToArray());
    }

    [Fact]
    public void All_gpus_is_not_cut_at_six_while_gpus_is()
    {
        var cards = Enumerable.Range(0, 8)
            .Select(i => Gpu("g" + i, "Card", (20 - i) * GiB) with { PciBus = (uint)i })
            .ToArray();

        var frame = BoardFrame.Project(Snap(gpus: cards));

        Assert.Equal(6, frame.Gpus!.Count);
        Assert.Equal(8, frame.AllGpus!.Count);
        Assert.Equal(frame.Gpus.Select(g => g.Id), frame.AllGpus.Take(6).Select(g => g.Id));
        Assert.Null(BoardFrame.Project(Snap(gpusState: Value.NotCollected<int>("test", "off"))).AllGpus);
    }

    // ---- per-socket CPU (DESIGN-C 2.3) ----

    private static CpuInfo SocketCpu(int[][]? sockets, Value<double>[] perLogical, Value<double>? total = null, int? logical = null) =>
        new(logical ?? perLogical.Length, total ?? Ok(50), perLogical, Gone())
        {
            Sockets = sockets is null ? null : new CpuSockets([.. sockets.Select(static s => (IReadOnlyList<int>)s)]),
        };

    private static Value<double>[] Loads(params double?[] percents) =>
        [.. percents.Select(static p => p is { } v ? Ok(v) : Gone())];

    [Fact]
    public void No_socket_map_means_no_sockets_and_the_total_is_not_repeated()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu(null, Loads(10, 20, 30, 40))));

        Assert.Null(frame.Sockets);
        Assert.Equal(50, frame.CpuPercent);
        Assert.Null(BoardFrame.Project(Snap(Cpu(Ok(33)))).Sockets); // a source that never asked leaves it null
    }

    [Fact]
    public void Two_sockets_are_the_mean_of_their_own_logical_cpus()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(10, 20, 60, 100))));

        var sockets = frame.Sockets!;
        Assert.Equal(2, sockets.Count);
        Assert.Equal(new BoardCpuSocket(15, 2), sockets[0]);
        Assert.Equal(new BoardCpuSocket(80, 2), sockets[1]);
        Assert.Equal(50, frame.CpuPercent);
    }

    [Fact]
    public void Index_lists_may_interleave()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu([[0, 2], [1, 3]], Loads(0, 100, 20, 80))));

        Assert.Equal(new[] { new BoardCpuSocket(10, 2), new BoardCpuSocket(90, 2) }, frame.Sockets!.ToArray());
    }

    [Fact]
    public void A_cpu_without_a_number_is_left_out_of_the_mean_not_counted_as_zero()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(40, null, 10, 30))));

        Assert.Equal(new[] { new BoardCpuSocket(40, 2), new BoardCpuSocket(20, 2) }, frame.Sockets!.ToArray());
    }

    [Fact]
    public void A_socket_whose_cpus_all_lack_numbers_is_missing_never_zero()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(null, null, 10, 30))));

        var sockets = frame.Sockets!;
        Assert.Null(sockets[0].Percent);
        Assert.Equal(2, sockets[0].LogicalProcessors);
        Assert.Equal(20, sockets[1].Percent);

        var noBaseline = BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(null, null, null, null), total: Gone())));
        Assert.All(noBaseline.Sockets!, s => Assert.Null(s.Percent));
        Assert.Null(noBaseline.CpuPercent);
    }

    [Fact]
    public void One_package_is_one_socket_equal_to_the_total_even_when_per_logical_numbers_are_missing()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu([[0, 1, 2, 3]], Loads(10, 20, 30, 40), total: Ok(27.5))));
        var socket = Assert.Single(frame.Sockets!);
        Assert.Equal(new BoardCpuSocket(27.5, 4), socket);

        var fewValues = BoardFrame.Project(Snap(SocketCpu([[0, 1, 2, 3]], [], total: Ok(12), logical: 4)));
        Assert.Equal(new BoardCpuSocket(12, 4), Assert.Single(fewValues.Sockets!));

        var noTotal = BoardFrame.Project(Snap(SocketCpu([[0, 1, 2, 3]], Loads(null, null, null, null), total: Gone())));
        Assert.Null(Assert.Single(noTotal.Sockets!).Percent);
    }

    [Fact]
    public void A_map_that_does_not_cover_every_logical_cpu_gives_no_sockets()
    {
        var loads = Loads(10, 20, 30, 40);

        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [2]], loads))).Sockets);          // cpu 3 in no socket
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1]], loads))).Sockets);               // half the machine
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3, 4]], loads))).Sockets);   // an index that does not exist
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3, -1]], loads))).Sockets);
    }

    [Fact]
    public void A_map_with_an_overlap_or_an_empty_socket_gives_no_sockets()
    {
        var loads = Loads(10, 20, 30, 40);

        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1, 2], [2, 3]], loads))).Sockets);     // cpu 2 twice
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [1, 2], [3]], loads))).Sockets);   // cpu 1 twice
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1, 2, 3], []], loads))).Sockets);      // an empty socket
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 0], [1, 2, 3]], loads))).Sockets);     // twice inside one socket
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([], loads))).Sockets);                      // no sockets at all
    }

    [Fact]
    public void A_per_logical_list_of_another_length_than_the_cpu_count_gives_no_sockets_for_several_packages()
    {
        // The map covers 4 cpus, but the snapshot's per-logical list holds only 2 numbers: which numbers belong to which socket is unclear.
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(10, 20), logical: 4))).Sockets);
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(10, 20, 30, 40), logical: 0))).Sockets);
        Assert.Null(BoardFrame.Project(Snap(SocketCpu([[0, 1], [2, 3]], Loads(10, 20, 30, 40), logical: 8))).Sockets);
    }

    [Fact]
    public void Socket_percentages_are_clamped_like_the_total()
    {
        var frame = BoardFrame.Project(Snap(SocketCpu([[0], [1]], Loads(130, -5))));

        Assert.Equal(new[] { new BoardCpuSocket(100, 1), new BoardCpuSocket(0, 1) }, frame.Sockets!.ToArray());
    }

    // ---- disk kinds and the fullest volume (DESIGN-C 2.3) ----

    private static StorageTopology Topo(StorageDrive[] drives, StorageVolumeUse[]? volumes = null) => new(drives, volumes);

    private static StorageDrive Drive(int number, DiskKind kind, long? capacity = 1000) => new(number, kind, capacity, $"model{number}|serial{number}");

    private static BoardDiskKind Kind(BoardFrame frame, DiskKind kind) => Assert.Single(frame.DiskKinds!, k => k.Kind == kind);

    [Fact]
    public void Each_kind_sums_read_plus_write_of_its_own_disks_matched_by_drive_number()
    {
        var topo = Topo([Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Ssd), Drive(2, DiskKind.Hdd), Drive(3, DiskKind.Hdd)]);
        var disks = new[]
        {
            Disk("PhysicalDrive0", Ok(100), Ok(50)),
            Disk("PhysicalDrive1", Ok(10), Ok(5)),
            Disk("PhysicalDrive2", Ok(1), Ok(2)),
            Disk("PhysicalDrive3", Ok(3), Ok(4)),
        };

        var frame = BoardFrame.Project(Snap(disks: disks, storage: topo));

        Assert.Equal(new BoardDisk(175, false), frame.Disk);
        Assert.Equal([DiskKind.Nvme, DiskKind.Ssd, DiskKind.Hdd], frame.DiskKinds!.Select(static k => k.Kind));
        Assert.Equal(150, Kind(frame, DiskKind.Nvme).BytesPerSec);
        Assert.Equal(15, Kind(frame, DiskKind.Ssd).BytesPerSec);
        var hdd = Kind(frame, DiskKind.Hdd);
        Assert.Equal(10, hdd.BytesPerSec);
        Assert.Equal(2, hdd.DeviceCount);
        Assert.False(hdd.Partial);
        Assert.Equal(2000, hdd.CapacityBytes);
    }

    [Fact]
    public void The_kinds_are_never_a_split_of_the_total_so_they_never_add_up_to_more_than_it()
    {
        var topo = Topo([Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Hdd)]);
        var disks = new[] { Disk("PhysicalDrive0", Ok(100), Ok(0)), Disk("PhysicalDrive1", Ok(7), Ok(3)), Disk("PhysicalDrive9", Ok(1000), Ok(0)) };

        var frame = BoardFrame.Project(Snap(disks: disks, storage: topo));

        Assert.Equal(1110, frame.Disk.BytesPerSec);
        Assert.Equal(110, frame.DiskKinds!.Sum(static k => k.BytesPerSec));
    }

    [Fact]
    public void A_reused_drive_number_follows_the_topology_it_is_read_with_never_the_earlier_device()
    {
        var disks = new[] { Disk("PhysicalDrive1", Ok(40), Ok(10)) };

        // Number 1 was a hard disk.
        var before = BoardFrame.Project(Snap(disks: disks, storage: Topo([Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Hdd)])));
        Assert.Equal(50, Kind(before, DiskKind.Hdd).BytesPerSec);

        // Then the device changed and an NVMe has the number: the same row now counts for the NVMe and the HDD is gone.
        var after = BoardFrame.Project(Snap(disks: disks, storage: Topo([Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Nvme)])));
        Assert.DoesNotContain(after.DiskKinds!, static k => k.Kind == DiskKind.Hdd);
        var nvme = Kind(after, DiskKind.Nvme);
        Assert.Equal(2, nvme.DeviceCount);
        Assert.Equal(50, nvme.BytesPerSec);
        Assert.True(nvme.Partial);
    }

    [Fact]
    public void A_row_with_no_drive_in_the_topology_is_in_the_total_only()
    {
        var topo = Topo([Drive(0, DiskKind.Nvme)]);
        var disks = new[] { Disk("PhysicalDrive0", Ok(10), Ok(10)), Disk("PhysicalDrive5", Ok(100), Ok(100)) };

        var frame = BoardFrame.Project(Snap(disks: disks, storage: topo));

        Assert.Equal(20, Kind(frame, DiskKind.Nvme).BytesPerSec);
        Assert.Single(frame.DiskKinds!);
        Assert.Equal(220, frame.Disk.BytesPerSec);
    }

    [Fact]
    public void A_drive_of_unknown_kind_is_in_the_total_only_and_no_kind_lists_it()
    {
        var topo = Topo([Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Unknown), Drive(2, DiskKind.Unknown)]);
        var disks = new[] { Disk("PhysicalDrive0", Ok(1), Ok(1)), Disk("PhysicalDrive1", Ok(500), Ok(500)) };

        var frame = BoardFrame.Project(Snap(disks: disks, storage: topo));

        Assert.Equal(1002, frame.Disk.BytesPerSec);
        var only = Assert.Single(frame.DiskKinds!);
        Assert.Equal(DiskKind.Nvme, only.Kind);
        Assert.Equal(2, only.BytesPerSec);
        Assert.Equal(1, only.DeviceCount);
    }

    [Fact]
    public void Without_a_topology_no_kind_is_guessed_and_only_the_total_is_shown()
    {
        var disks = new[] { Disk("PhysicalDrive0", Ok(10), Ok(10)) };

        var frame = BoardFrame.Project(Snap(disks: disks));

        Assert.Null(frame.DiskKinds);
        Assert.Null(frame.FullestVolumePercent);
        Assert.Equal(20, frame.Disk.BytesPerSec);
    }

    [Fact]
    public void A_kind_with_a_drive_but_no_readable_number_is_null_never_zero()
    {
        var topo = Topo([Drive(0, DiskKind.Hdd, capacity: null), Drive(1, DiskKind.Ssd)]);
        // The HDD has no row at all; the SSD has a row whose write number is missing.
        var disks = new[] { Disk("PhysicalDrive1", Ok(10), Gone()) };

        var frame = BoardFrame.Project(Snap(disks: disks, storage: topo));

        var hdd = Kind(frame, DiskKind.Hdd);
        Assert.Null(hdd.BytesPerSec);
        Assert.True(hdd.Partial);
        Assert.Null(hdd.CapacityBytes);
        Assert.Null(hdd.UsedBytes);
        Assert.Null(hdd.TotalBytes);
        var ssd = Kind(frame, DiskKind.Ssd);
        Assert.Null(ssd.BytesPerSec);
        Assert.True(ssd.Partial);
        Assert.Equal(1000, ssd.CapacityBytes);
    }

    [Fact]
    public void A_negative_or_missing_rate_is_not_counted_and_marks_the_kind_partial()
    {
        var topo = Topo([Drive(0, DiskKind.Hdd), Drive(1, DiskKind.Hdd)]);
        var disks = new[] { Disk("PhysicalDrive0", Ok(10), Ok(20)), Disk("PhysicalDrive1", Ok(-5), Ok(20)) };

        var hdd = Kind(BoardFrame.Project(Snap(disks: disks, storage: topo)), DiskKind.Hdd);

        Assert.Equal(30, hdd.BytesPerSec);
        Assert.True(hdd.Partial);
    }

    [Fact]
    public void Two_rows_that_claim_the_same_drive_number_count_for_no_kind()
    {
        var topo = Topo([Drive(0, DiskKind.Nvme)]);
        var disks = new[] { Disk("PhysicalDrive0", Ok(10), Ok(10)), Disk("PhysicalDrive0", Ok(30), Ok(30)) };

        var frame = BoardFrame.Project(Snap(disks: disks, storage: topo));

        Assert.Null(Kind(frame, DiskKind.Nvme).BytesPerSec);
        Assert.Equal(80, frame.Disk.BytesPerSec);
    }

    [Fact]
    public void A_volume_on_one_drive_counts_for_that_kind_and_a_spanning_volume_for_none()
    {
        var topo = Topo(
            [Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Ssd), Drive(2, DiskKind.Hdd)],
            [
                new StorageVolumeUse("C:", 1000, 400, [0]),
                new StorageVolumeUse("S:", 4000, 400, [1, 2]),
                new StorageVolumeUse("E:", 500, 100, [2]),
            ]);

        var frame = BoardFrame.Project(Snap(storage: topo));

        var nvme = Kind(frame, DiskKind.Nvme);
        Assert.Equal((600L, 1000L), (nvme.UsedBytes, nvme.TotalBytes));
        var ssd = Kind(frame, DiskKind.Ssd);
        Assert.Null(ssd.UsedBytes);
        Assert.Null(ssd.TotalBytes);
        var hdd = Kind(frame, DiskKind.Hdd);
        Assert.Equal((400L, 500L), (hdd.UsedBytes, hdd.TotalBytes));
        // The spanning volume is the fullest one here (90%), so the fullest-volume number still sees it although no kind does.
        Assert.Equal(90, frame.FullestVolumePercent);
    }

    [Fact]
    public void The_fullest_volume_is_the_largest_used_share_and_missing_when_none_was_read()
    {
        var drives = new[] { Drive(0, DiskKind.Nvme), Drive(1, DiskKind.Hdd) };

        var some = BoardFrame.Project(Snap(storage: Topo(drives,
        [
            new StorageVolumeUse("C:", 1000, 500, [0]),
            new StorageVolumeUse("D:", 2000, 100, [1]),
            new StorageVolumeUse("E:", 100, 40, [1]),
        ])));
        Assert.Equal(95, some.FullestVolumePercent);

        Assert.Null(BoardFrame.Project(Snap(storage: Topo(drives))).FullestVolumePercent);
        Assert.Null(BoardFrame.Project(Snap(storage: Topo(drives, []))).FullestVolumePercent);
    }

    [Fact]
    public void A_volume_with_impossible_numbers_is_not_the_fullest()
    {
        var topo = Topo([Drive(0, DiskKind.Nvme)],
        [
            new StorageVolumeUse("C:", 1000, 2000, [0]),
            new StorageVolumeUse("D:", 0, 0, [0]),
            new StorageVolumeUse("E:", 1000, 750, [0]),
        ]);

        Assert.Equal(25, BoardFrame.Project(Snap(storage: topo)).FullestVolumePercent);
    }
}
