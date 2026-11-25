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
        Value<int>? gpusState = null)
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
            GpusState: gpusState ?? Value.Ok(gpuList.Count, "test"));
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
}
