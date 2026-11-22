using System.Buffers.Binary;
using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Checks.Tests.Load;

/// <summary>
/// The list of logical processors the CPU check pins its threads to (DESIGN-A §6.2): the parse of Windows' RelationProcessorCore records, the
/// fallback when Windows does not answer, and the one real read of this machine. Nothing here loads the machine or pins anything.
/// </summary>
public sealed class LogicalCpuMapTests
{
    private const int RecordSize = 48;

    /// <summary>One SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record for a core: the relationship, the size, the class and its group masks.</summary>
    private static byte[] CoreRecord(byte efficiency, params (ulong Mask, ushort Group)[] groups)
    {
        var size = 32 + (groups.Length * 16);
        var record = new byte[Math.Max(size, RecordSize)];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0), 0); // RelationProcessorCore
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), record.Length);
        record[9] = efficiency;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), (ushort)groups.Length);
        for (var g = 0; g < groups.Length; g++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32 + (g * 16)), groups[g].Mask);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(40 + (g * 16)), groups[g].Group);
        }

        return record;
    }

    private static byte[] Join(params byte[][] records) => [.. records.SelectMany(static r => r)];

    // ---- the old CpuTopology Parse test ----

    [Fact]
    public void Parse_lists_every_logical_processor_with_its_core_and_class_in_order()
    {
        // Two 48-byte core records: the first is class 1 and holds logical processors 0 and 1 (mask 0b0011), the second is class 0 and holds 2.
        var buffer = new byte[2 * RecordSize];
        for (var i = 0; i < 2; i++)
        {
            var at = i * RecordSize;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 4), RecordSize);
            buffer[at + 9] = (byte)(i == 0 ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 30), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(at + 32), i == 0 ? 0b0011UL : 0b0100UL);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 40), 0);
        }

        var cpus = LogicalCpuMap.Parse(buffer);

        Assert.Equal(3, cpus.Count);
        Assert.Equal([1, 1, 0], cpus.Select(static c => (int)c.EfficiencyClass));
        Assert.Equal([0, 0, 1], cpus.Select(static c => c.Core));
        Assert.Equal([0, 1, 2], cpus.Select(static c => (int)c.Number));
        Assert.Equal([0, 1, 2], cpus.Select(static c => c.Index));
        Assert.All(cpus, static c => Assert.Equal(0, c.Group));
    }

    // ---- the walk ----

    [Fact]
    public void Parse_of_nothing_is_an_empty_list()
    {
        Assert.Empty(LogicalCpuMap.Parse([]));
        Assert.Empty(LogicalCpuMap.Parse(new byte[7]));
    }

    [Fact]
    public void A_record_that_runs_past_the_buffer_ends_the_walk_and_keeps_what_was_read()
    {
        var whole = Join(CoreRecord(0, (0b01, 0)), CoreRecord(0, (0b10, 0)));

        var cpus = LogicalCpuMap.Parse(whole.AsSpan(0, RecordSize + 20));

        Assert.Equal([0], cpus.Select(static c => (int)c.Number));
    }

    [Fact]
    public void A_record_of_size_zero_ends_the_walk_and_does_not_loop()
    {
        var buffer = Join(CoreRecord(0, (0b01, 0)), new byte[RecordSize]);

        var cpus = LogicalCpuMap.Parse(buffer);

        Assert.Single(cpus);
    }

    [Fact]
    public void A_record_of_another_relationship_is_skipped_and_is_not_a_core()
    {
        var cache = new byte[RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(cache.AsSpan(0), 2); // RelationCache
        BinaryPrimitives.WriteInt32LittleEndian(cache.AsSpan(4), RecordSize);
        var buffer = Join(CoreRecord(0, (0b01, 0)), cache, CoreRecord(0, (0b10, 0)));

        var cpus = LogicalCpuMap.Parse(buffer);

        Assert.Equal([0, 1], cpus.Select(static c => (int)c.Number));
        Assert.Equal([0, 1], cpus.Select(static c => c.Core)); // the cache record did not use up a core number
    }

    [Fact]
    public void The_list_is_in_the_order_of_group_then_number_whatever_the_order_of_the_records()
    {
        var buffer = Join(CoreRecord(0, (0b1, 1)), CoreRecord(0, (0b10, 0)), CoreRecord(0, (0b1, 0)));

        var cpus = LogicalCpuMap.Parse(buffer);

        Assert.Equal([(0, 0), (0, 1), (1, 0)], cpus.Select(static c => ((int)c.Group, (int)c.Number)));
        Assert.Equal([0, 1, 2], cpus.Select(static c => c.Index));
    }

    [Fact]
    public void A_core_in_two_groups_gives_its_bits_in_both()
    {
        var buffer = Join(CoreRecord(1, (0b1, 0), (0b1, 1)));

        var cpus = LogicalCpuMap.Parse(buffer);

        Assert.Equal([(0, 0), (1, 0)], cpus.Select(static c => ((int)c.Group, (int)c.Number)));
        Assert.All(cpus, static c => Assert.Equal(0, c.Core));
        Assert.All(cpus, static c => Assert.Equal(1, c.EfficiencyClass));
    }

    [Fact]
    public void A_mask_with_the_high_bit_set_gives_number_63()
    {
        var cpus = LogicalCpuMap.Parse(CoreRecord(0, (1UL << 63, 0)));

        Assert.Equal(63, Assert.Single(cpus).Number);
    }

    // ---- the fallback ----

    [Fact]
    public void The_fallback_is_one_processor_per_count_of_one_class_with_64_to_a_group()
    {
        var cpus = LogicalCpuMap.Fallback(130);

        Assert.Equal(130, cpus.Count);
        Assert.Equal(new[] { 0, 0, 1, 2 }, new[] { (int)cpus[0].Group, cpus[63].Group, cpus[64].Group, cpus[128].Group });
        Assert.Equal(0, cpus[64].Number);
        Assert.Equal(1, cpus[65].Number);
        Assert.All(cpus, static c => Assert.Equal(0, c.EfficiencyClass));
        Assert.Equal(Enumerable.Range(0, 130), cpus.Select(static c => c.Index));
    }

    [Fact]
    public void The_fallback_has_at_least_one_processor()
    {
        Assert.Single(LogicalCpuMap.Fallback(0));
        Assert.Single(LogicalCpuMap.Fallback(-3));
    }

    // ---- this machine (a read of Windows' map: nothing is loaded, nothing is pinned) ----

    [Fact]
    public void The_real_topology_counts_as_many_logical_processors_as_the_environment_says()
    {
        var cpus = LogicalCpuMap.Read();

        Assert.Equal(Environment.ProcessorCount, cpus.Count);
        Assert.Equal(Enumerable.Range(0, cpus.Count), cpus.Select(static c => c.Index));
        Assert.Equal(cpus.OrderBy(static c => c.Group).ThenBy(static c => c.Number).Select(static c => c.Index), cpus.Select(static c => c.Index));
        Assert.Equal(cpus.Count, cpus.Select(static c => (c.Group, c.Number)).Distinct().Count()); // no processor twice
    }
}
