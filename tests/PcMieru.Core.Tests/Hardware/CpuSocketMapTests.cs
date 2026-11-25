using System.Buffers.Binary;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>
/// The per-package part of the CPU topology: the parser keeps each package's GROUP_AFFINITY list and the per-group active counts, and the pure
/// mapping confirms a socket only for one package in one group (it is every PerLogicalPercent entry); every multi-package or multi-group machine
/// still parses but its mapping is unknown, with a reason, until a labelled per-processor source exists.
/// </summary>
public class CpuSocketMapTests
{
    /// <summary>A RelationProcessorCore / RelationProcessorPackage record: 8 header, 30 GroupCount, 32 GROUP_AFFINITY[] of (mask 8, group 2, pad 6).</summary>
    private static byte[] Processor(int relationship, params (ulong Mask, ushort Group)[] affinities)
    {
        var rec = new byte[32 + (affinities.Length * 16)];
        BinaryPrimitives.WriteInt32LittleEndian(rec, relationship);
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(4), rec.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(30), (ushort)affinities.Length);
        for (var i = 0; i < affinities.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(32 + (i * 16)), affinities[i].Mask);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(40 + (i * 16)), affinities[i].Group);
        }

        return rec;
    }

    private static byte[] Core(ulong mask, ushort group = 0) => Processor(0, (mask, group));

    private static byte[] Package(params (ulong Mask, ushort Group)[] affinities) => Processor(3, affinities);

    /// <summary>A RelationGroup record: 8 MaximumGroupCount, 10 ActiveGroupCount, 32 PROCESSOR_GROUP_INFO[] of 48 (max 1, active 1, mask at 40).</summary>
    private static byte[] Groups(int maximum, params (byte Active, ulong Mask)[] active)
    {
        var rec = new byte[32 + (maximum * 48)];
        BinaryPrimitives.WriteInt32LittleEndian(rec, 4);
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(4), rec.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8), (ushort)maximum);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(10), (ushort)active.Length);
        for (var g = 0; g < active.Length; g++)
        {
            rec[32 + (g * 48)] = 64;
            rec[32 + (g * 48) + 1] = active[g].Active;
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(32 + (g * 48) + 40), active[g].Mask);
        }

        return rec;
    }

    private static CpuTopology Parse(params byte[][] records) => CpuTopology.Parse(records.SelectMany(static r => r).ToArray());

    // ---- fixtures ----

    /// <summary>One package, 4 cores x 2 threads in one group.</summary>
    private static CpuTopology OnePackage() => Parse(
        Core(0b11), Core(0b1100), Core(0b110000), Core(0b11000000),
        Package((0xFF, 0)),
        Groups(1, (8, 0xFF)));

    /// <summary>Two packages of 4 logical CPUs each in one group (a dual-socket box under 64 logical CPUs).</summary>
    private static CpuTopology TwoPackages() => Parse(
        Core(0b11), Core(0b1100), Core(0b110000), Core(0b11000000),
        Package((0x0F, 0)),
        Package((0xF0, 0)),
        Groups(1, (8, 0xFF)));

    /// <summary>Two packages with 4 logical CPUs each, one package per processor group (the group holds the whole package).</summary>
    private static CpuTopology TwoGroups() => Parse(
        Core(0b11, 0), Core(0b1100, 0), Core(0b11, 1), Core(0b1100, 1),
        Package((0x0F, 0)),
        Package((0x0F, 1)),
        Groups(2, (4, 0x0F), (4, 0x0F)));

    private static string Unknown(CpuTopology topology, int logical)
    {
        Assert.False(CpuSocketMap.TryMap(topology, logical, out var sockets, out var reason));
        Assert.Null(sockets);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Null(CpuSocketMap.Map(topology, logical));
        return reason;
    }

    private static int[][] Known(CpuTopology topology, int logical)
    {
        Assert.True(CpuSocketMap.TryMap(topology, logical, out var sockets, out _));
        Assert.NotNull(sockets);
        Assert.Equal(sockets.LogicalIndexesBySocket.Count, sockets.Count);
        return [.. sockets.LogicalIndexesBySocket.Select(static l => l.ToArray())];
    }

    // ---- parse ----

    [Fact]
    public void One_package_keeps_its_group_affinity_and_the_old_counts_stay_the_same()
    {
        var t = OnePackage();

        Assert.Equal(4, t.PhysicalCores);
        Assert.Equal(8, t.LogicalProcessors);
        Assert.Equal(1, t.Packages);
        Assert.Equal(1, t.ProcessorGroups);
        Assert.Equal(new[] { new GroupAffinity(0, 0xFF) }, Assert.Single(t.PackageAffinities).ToArray());
        Assert.Equal(new[] { new ProcessorGroupInfo(0, 8, 0xFF) }, t.GroupInfos.ToArray());
    }

    [Fact]
    public void Two_packages_keep_one_affinity_list_each_in_windows_order()
    {
        var t = TwoPackages();

        Assert.Equal(2, t.Packages);
        Assert.Equal(1, t.ProcessorGroups);
        Assert.Equal(2, t.PackageAffinities.Count);
        Assert.Equal(new[] { new GroupAffinity(0, 0x0F) }, t.PackageAffinities[0].ToArray());
        Assert.Equal(new[] { new GroupAffinity(0, 0xF0) }, t.PackageAffinities[1].ToArray());
        Assert.Equal(8, t.LogicalProcessors);
    }

    [Fact]
    public void Two_groups_keep_the_group_numbers_the_masks_and_each_groups_active_count()
    {
        var t = TwoGroups();

        Assert.Equal(2, t.Packages);
        Assert.Equal(2, t.ProcessorGroups);
        Assert.Equal(new[] { new GroupAffinity(0, 0x0F) }, t.PackageAffinities[0].ToArray());
        Assert.Equal(new[] { new GroupAffinity(1, 0x0F) }, t.PackageAffinities[1].ToArray());
        Assert.Equal(new[] { new ProcessorGroupInfo(0, 4, 0x0F), new ProcessorGroupInfo(1, 4, 0x0F) }, t.GroupInfos.ToArray());
        Assert.Equal(8, t.LogicalProcessors);
    }

    [Fact]
    public void A_group_record_with_room_for_more_groups_lists_only_the_active_ones()
    {
        var t = Parse(Package((0xFF, 0)), Groups(4, (8, 0xFF)));

        Assert.Equal(1, t.ProcessorGroups);
        Assert.Equal(new[] { new ProcessorGroupInfo(0, 8, 0xFF) }, t.GroupInfos.ToArray());
    }

    [Fact]
    public void A_package_record_cut_short_still_counts_as_a_package_with_no_affinity()
    {
        var package = Package((0xFF, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(30), 3); // claims three entries, holds one

        var t = CpuTopology.Parse(package);

        Assert.Equal(1, t.Packages);
        Assert.Equal(new[] { new GroupAffinity(0, 0xFF) }, Assert.Single(t.PackageAffinities).ToArray());
        Assert.Empty(CpuTopology.Parse(Package().AsSpan(0, 20)).PackageAffinities);
    }

    // ---- mapping ----

    [Fact]
    public void One_package_in_one_group_is_one_socket_with_every_cpu()
    {
        Assert.Equal([[0, 1, 2, 3, 4, 5, 6, 7]], Known(OnePackage(), 8));
    }

    [Fact]
    public void One_package_spanning_several_groups_is_unknown_because_the_per_logical_order_across_groups_is_not_confirmed()
    {
        var t = Parse(Package((ulong.MaxValue, 0), (0xFF, 1)), Groups(2, (64, ulong.MaxValue), (8, 0xFF)));

        Assert.Equal(2, t.ProcessorGroups);
        Assert.Contains("2 processor groups", Unknown(t, 72));
        // Even when the sampler counted only one group's worth, the package's other groups are not "nothing": still unknown.
        Assert.Contains("2 processor groups", Unknown(t, 64));
    }

    [Fact]
    public void One_package_whose_size_disagrees_with_the_sampler_count_is_unknown()
    {
        Assert.Contains("sampler counts 6", Unknown(OnePackage(), 6));
    }

    [Fact]
    public void Two_packages_are_unknown_even_in_one_group_because_the_rows_carry_no_processor_id()
    {
        Assert.Contains("2 processor packages", Unknown(TwoPackages(), 8));
        var interleaved = Parse(Package((0b01010101, 0)), Package((0b10101010, 0)), Groups(1, (8, 0xFF)));
        Assert.Contains("2 processor packages", Unknown(interleaved, 8));
    }

    [Fact]
    public void Several_packages_in_several_groups_are_unknown()
    {
        Assert.Contains("2 processor packages", Unknown(TwoGroups(), 8));
        Assert.Contains("3 processor packages", Unknown(Parse(Package((0x0F, 0)), Package((0x0F, 1)), Package((0x0F, 2)), Groups(3, (4, 0x0F), (4, 0x0F), (4, 0x0F))), 12));
    }

    [Fact]
    public void One_package_whose_affinity_leaves_the_groups_active_set_or_has_no_affinity_is_unknown()
    {
        var gap = Parse(Package((0x0F, 0)), Groups(1, (8, 0xFF)));
        var outside = Parse(Package((0x1FF, 0)), Groups(1, (8, 0xFF)));
        var otherGroup = Parse(Package((0xFF, 1)), Groups(1, (8, 0xFF)));
        var none = Parse(Package(), Groups(1, (8, 0xFF)));

        Assert.Contains("lists 4 logical processors", Unknown(gap, 8));
        Assert.Contains("outside the one group", Unknown(outside, 8));
        Assert.Contains("outside the one group", Unknown(otherGroup, 8));
        Assert.Contains("lists 0 logical processors", Unknown(none, 8));
    }

    [Fact]
    public void A_missing_record_or_count_is_unknown()
    {
        Assert.Contains("no usable", Unknown(Parse(Groups(1, (8, 0xFF))), 8));
        Assert.Contains("no logical processor count", Unknown(OnePackage(), 0));
        Assert.Contains("0 processor groups", Unknown(Parse(Package((0xFF, 0))), 8));
    }

    [Fact]
    public void A_known_mapping_covers_every_index_exactly_once()
    {
        var all = Known(OnePackage(), 8).SelectMany(static l => l).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 8), all);
    }

    [Fact]
    public void A_full_group_of_64_is_a_valid_one_group_machine()
    {
        var t = Parse(Package((ulong.MaxValue, 0)), Groups(1, (64, ulong.MaxValue)));

        Assert.Equal(Enumerable.Range(0, 64), Assert.Single(Known(t, 64)));
    }

    // ---- the sampler and the contract ----

    [Fact]
    public void The_sockets_never_reach_the_json_so_the_snapshot_is_unchanged()
    {
        var snapshot = FakeSnapshots.Create(Fixture.FixedTime, tick: 2);
        var withSockets = snapshot with
        {
            System = snapshot.System with { Cpu = snapshot.System.Cpu with { Sockets = new CpuSockets([[0, 1], [2, 3]]) } },
        };

        Assert.NotNull(withSockets.System.Cpu.Sockets);
        var plain = PcMieruJson.Serialize(snapshot);
        var json = PcMieruJson.Serialize(withSockets);
        Assert.Equal(plain, json);
        Assert.DoesNotContain("sockets", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(PcMieruJson.Deserialize<Snapshot>(json)!.System.Cpu.Sockets);
    }

    [Fact]
    public void A_snapshot_made_without_a_source_says_nothing_about_sockets()
    {
        Assert.Null(FakeSnapshots.Create(Fixture.FixedTime, tick: 2).System.Cpu.Sockets);
    }

    /// <summary>Reads this PC's real topology (read-only): whatever the mapping says, it is either unknown or an exact cover of the sampler's count.</summary>
    [Fact]
    [Trait("Category", "RealMachine")]
    public void This_machines_mapping_is_unknown_or_an_exact_cover_of_the_sampler_count()
    {
        var logical = Math.Max(1, (int)Win32.GetActiveProcessorCount(Win32.AllProcessorGroups));
        var topology = CpuTopology.Read();
        Assert.NotNull(topology);
        Assert.Equal(topology.Packages, topology.PackageAffinities.Count);
        Assert.Equal(topology.ProcessorGroups, topology.GroupInfos.Count);

        if (CpuSocketMap.TryMap(topology, logical, out var sockets, out var reason))
        {
            Assert.Equal(Enumerable.Range(0, logical), sockets.LogicalIndexesBySocket.SelectMany(static l => l).Order());
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        Assert.Equal(sockets?.Count, CpuSocketMap.ReadMachine(logical)?.Count);
    }
}
