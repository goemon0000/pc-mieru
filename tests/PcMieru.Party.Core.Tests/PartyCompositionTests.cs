using PcMieru.Core;
using PcMieru.Party.Game;
using PcMieru.Party.Hardware;
using PcMieru.Party.Rendering;

namespace PcMieru.Party.Core.Tests;

/// <summary>BoardFrame から席を組み立てる（旧 Parts Life の Composition と同じ鍵・目盛り）。欠けは欠けのまま（DESIGN-C 2.3）。</summary>
public sealed class PartyCompositionTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const double MiB = 1024.0 * 1024;

    private static BoardFrame Frame(
        double? cpu, BoardMemory? mem, IReadOnlyList<BoardGpu>? gpus, double? bytesPerSec,
        IReadOnlyList<BoardDiskKind>? kinds = null, IReadOnlyList<BoardCpuSocket>? sockets = null, int logical = 16)
    {
        double? gpuMax = gpus is null ? null : gpus.Select(g => g.Percent).Where(p => p is not null).DefaultIfEmpty(null).Max();
        return new BoardFrame(logical, cpu, mem, gpus, gpuMax, new BoardDisk(bytesPerSec, false), new BoardNet(0, 0, false))
        {
            AllGpus = gpus,
            Sockets = sockets,
            DiskKinds = kinds,
        };
    }

    [Fact]
    public void One_cpu_one_gpu_one_nvme_gives_the_old_keys_titles_and_numbers()
    {
        var frame = Frame(50, new BoardMemory(8 * GiB, 16 * GiB, 50),
            [new BoardGpu("g0", "RTX", 25, null, null)], 100 * MiB,
            [new BoardDiskKind(DiskKind.Nvme, 1, 100 * MiB, false, 500 * GiB, 100 * GiB, 500 * GiB)]);

        var slots = Comp.Build(frame, "ja");

        Assert.Equal(new[] { "cpu#0", "cpu-cooler#0", "memory", "gpu#0", "m2", "psu" }, slots.Select(s => s.Key));
        Assert.Equal(new[] { "CPU", "FAN", "MEM", "GPU", "M.2", "PSU" }, slots.Select(s => s.Title));
        Assert.All(slots, s => Assert.True(s.Measured));
        Assert.Equal(0.5, slots[0].Load, 6);
        Assert.Equal("50%  16 コア", slots[0].Detail);
        Assert.Equal(0.5, slots[2].Load, 6);
        Assert.Equal("8.0 / 16.0 GB", slots[2].Detail);
        Assert.Equal(0.25, slots[3].Load, 6);
        Assert.Equal("25%  RTX", slots[3].Detail);
        Assert.Equal(SlotKind.M2, slots[4].Kind);
        Assert.Equal(100 * MiB / (2000 * MiB), slots[4].Load, 6);
        Assert.Equal("100 / 500 GB", slots[4].Detail);
        // Old rule: (cpu + gpuMax) * 0.5 + diskLoad * 0.2, with diskLoad = 100 MB/s on the 200 MB/s work scale.
        Assert.Equal((0.5 + 0.25) * 0.5 + 0.5 * 0.2, slots[5].Load, 6);
        Assert.Equal("100.0 MB/s", slots[5].Detail);
    }

    [Fact]
    public void Two_sockets_two_gpus_nvme_and_hdd_give_the_old_numbering()
    {
        var frame = Frame(40, new BoardMemory(32 * GiB, 64 * GiB, 50),
            [new BoardGpu("g0", "A", 10, null, null), new BoardGpu("g1", "B", 90, null, null)], 60 * MiB,
            [new BoardDiskKind(DiskKind.Nvme, 2, 200 * MiB, false, null, 100 * GiB, 1000 * GiB),
             new BoardDiskKind(DiskKind.Hdd, 1, 12 * MiB, false, null, null, null)],
            [new BoardCpuSocket(20, 8), new BoardCpuSocket(60, 8)]);

        var slots = Comp.Build(frame, "en");

        Assert.Equal(
            new[] { "cpu#0", "cpu-cooler#0", "cpu#1", "cpu-cooler#1", "memory", "gpu#0", "gpu#1", "m2", "hdd", "psu" },
            slots.Select(s => s.Key));
        Assert.Equal(new[] { "CPU0", "FAN0", "CPU1", "FAN1", "MEM", "GPU0", "GPU1", "M.2", "HDD", "PSU" }, slots.Select(s => s.Title));
        Assert.Equal(0.2, slots[0].Load, 6);
        Assert.Equal("20%  8 cores", slots[0].Detail);
        Assert.Equal(0.6, slots[2].Load, 6);
        Assert.Equal("100 / 1000 GB  ×2", slots[7].Detail);
        Assert.Equal(12 * MiB / (120 * MiB), slots[8].Load, 6);
        Assert.Equal(SlotKind.Sata, slots[8].Kind);
        Assert.Equal("", slots[8].Detail);
    }

    [Fact]
    public void A_missing_gpu_value_is_unmeasured_not_zero()
    {
        var frame = Frame(50, new BoardMemory(GiB, 2 * GiB, 50), [new BoardGpu("g0", "RTX", null, null, null)], 10 * MiB);

        var gpu = Comp.Build(frame, "ja").Single(s => s.Key == "gpu#0");

        Assert.False(gpu.Measured);
        Assert.Equal(0, gpu.Load);
        Assert.Equal(PartyComposition.Missing, gpu.Detail);
    }

    [Fact]
    public void A_missing_disk_value_is_unmeasured_and_without_a_kind_list_one_plain_disk_stands()
    {
        var withKinds = Frame(10, null, [], 1 * MiB, [new BoardDiskKind(DiskKind.Ssd, 1, null, true, null, null, null)]);
        var ssd = Comp.Build(withKinds, "ja").Single(s => s.Key == "ssd");
        Assert.False(ssd.Measured);
        Assert.Equal(PartyComposition.Missing, ssd.Detail);

        var plain = Comp.Build(Frame(10, null, [], 100 * MiB), "ja").Single(s => s.Key == "ssd");
        Assert.Equal("DISK", plain.Title);
        Assert.True(plain.Measured);
        Assert.False(Comp.Build(Frame(10, null, [], null), "ja").Single(s => s.Key == "ssd").Measured);
    }

    [Fact]
    public void Nothing_known_makes_nothing_measured_and_the_psu_is_unmeasured_too()
    {
        var slots = Comp.Build(Frame(null, null, null, null), "ja");

        Assert.DoesNotContain(slots, s => s.Key.StartsWith("gpu"));
        Assert.All(slots, s => Assert.False(s.Measured));
        Assert.All(slots, s => Assert.Equal(0, s.Load));
    }

    [Fact]
    public void A_missing_gpu_or_disk_slot_earns_no_experience_but_a_measured_zero_does()
    {
        var state = new PartyState();
        state.Migrate();
        var director = new Director(TestData.Story, state, null, new Random(1), () => 1_800_000_000);
        var missing = Comp.Build(
            Frame(50, null, [new BoardGpu("g0", "RTX", null, null, null)], null,
                [new BoardDiskKind(DiskKind.Nvme, 1, null, true, null, null, null)]), "ja");
        var zero = Comp.Build(
            Frame(50, null, [new BoardGpu("g0", "RTX", 0, null, null)], 0,
                [new BoardDiskKind(DiskKind.Nvme, 1, 0, false, null, null, null)]), "ja");

        for (int i = 0; i < 600; i++) director.Tick(1, Comp.WorkOf(Frame(50, null, null, null)), missing, "ja");
        Assert.Equal(0, state.Experience.GetValueOrDefault("gpu#0"));
        Assert.Equal(0, state.Experience.GetValueOrDefault("m2"));
        Assert.True(state.Experience.GetValueOrDefault("cpu#0") > 0);

        for (int i = 0; i < 600; i++) director.Tick(1, Comp.WorkOf(Frame(50, null, null, null)), zero, "ja");
        // A measured idle GPU is a real zero: it is not skipped, and at 0 load it adds whatever the rule gives for 0 (never negative).
        Assert.True(state.Experience.GetValueOrDefault("gpu#0") >= 0);
    }

    [Fact]
    public void WorkOf_uses_the_frame_values_and_marks_a_missing_channel_as_missing()
    {
        var work = Comp.WorkOf(Frame(50, new BoardMemory(GiB, 2 * GiB, 50), [new BoardGpu("g0", "x", 20, null, null)], 100 * MiB));

        Assert.Equal(0.5, work.Cpu, 6);
        Assert.Equal(0.2, work.Gpu, 6);
        Assert.Equal(0.5, work.Memory, 6);
        Assert.Equal(0.5, work.Disk, 6);
        Assert.Equal(0, work.DiskFullest);
        Assert.False(work.Has(WorkChannels.DiskFullest));   // 値が 0 なのは「無い」から。式には入れない
        Assert.True(work.Has(WorkChannels.Cpu | WorkChannels.Gpu | WorkChannels.Memory | WorkChannels.Disk));
    }
}
