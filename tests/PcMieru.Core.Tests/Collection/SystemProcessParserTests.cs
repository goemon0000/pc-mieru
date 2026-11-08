using System.Buffers.Binary;
using PcMieru.Core.Collection;
using static PcMieru.Core.Tests.Collection.SyntheticProcessBuffer;

namespace PcMieru.Core.Tests.Collection;

public class SystemProcessParserTests
{
    private static (ParseStatus Status, List<RawProcess> Result) Parse(byte[] data, ulong baseAddress = DefaultBase, NameInterner? names = null)
    {
        var list = new List<RawProcess>();
        var status = SystemProcessParser.Parse(data, baseAddress, names ?? new NameInterner(), list);
        return (status, list);
    }

    [Fact]
    public void Reads_every_field_from_its_documented_offset()
    {
        var buffer = Build(
            DefaultBase,
            new Proc(0, string.Empty, Ppid: 0, Create: 0),
            new Proc(
                1234,
                "chrome.exe",
                Ppid: 99,
                Create: 133_111_222_333_444_555,
                User: 30_000_000,
                Kernel: 12_000_000,
                Handles: 321,
                Session: 2,
                WorkingSet: 123_456_789,
                PrivateWorkingSet: 98_765_432,
                PrivatePages: 111_222_333,
                Read: 7_000_000_000,
                Write: 5,
                Threads: [(5, 7), (2, 0), (5, 7)]));

        var (status, list) = Parse(buffer);

        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal(2, list.Count);
        var p = list[1];
        Assert.Equal(1234u, p.Pid);
        Assert.Equal(99u, p.ParentPid);
        Assert.Equal("chrome.exe", p.Name);
        Assert.Equal(133_111_222_333_444_555, p.CreateTime);
        Assert.Equal(new ProcessKey(1234, 133_111_222_333_444_555), p.Key);
        Assert.Equal(30_000_000, p.UserTime);
        Assert.Equal(12_000_000, p.KernelTime);
        Assert.Equal(42_000_000, p.CpuTime);
        Assert.Equal(3, p.ThreadCount);
        Assert.Equal(321, p.HandleCount);
        Assert.Equal(8, p.BasePriority);
        Assert.Equal(2u, p.SessionId);
        Assert.Equal(123_456_789, p.WorkingSet);
        Assert.Equal(98_765_432, p.PrivateWorkingSet);
        Assert.Equal(7_000_000_000UL, p.ReadTransferCount);
        Assert.Equal(5UL, p.WriteTransferCount);
    }

    [Fact]
    public void Commit_is_PrivatePageCount_as_is_never_PagefileUsage()
    {
        var (_, list) = Parse(Build(DefaultBase, new Proc(10, "a.exe", PrivatePages: 4096)));
        Assert.Equal(4096, list[0].PrivatePageCount);
    }

    [Fact]
    public void Pid_zero_with_an_empty_name_is_called_System_Idle_Process()
    {
        var (_, list) = Parse(Build(DefaultBase, new Proc(0, string.Empty, Ppid: 0, Create: 0)));
        Assert.Equal("System Idle Process", list[0].Name);
    }

    [Fact]
    public void A_name_that_is_not_NUL_terminated_is_cut_by_its_length_field()
    {
        // Two entries back to back: the first name's bytes are directly followed by the next entry's header.
        var (_, list) = Parse(Build(DefaultBase, new Proc(8, "ab.exe"), new Proc(9, "cd.exe")));
        Assert.Equal(["ab.exe", "cd.exe"], list.Select(p => p.Name));
    }

    [Fact]
    public void The_same_name_is_interned_to_one_instance()
    {
        var names = new NameInterner();
        var (_, list) = Parse(Build(DefaultBase, new Proc(8, "svchost.exe"), new Proc(9, "svchost.exe"), new Proc(10, "svchost.exe")), names: names);
        Assert.Same(list[0].Name, list[1].Name);
        Assert.Same(list[1].Name, list[2].Name);
        Assert.Equal(1, names.Count);
    }

    [Fact]
    public void The_base_address_is_the_only_thing_pointers_are_resolved_against()
    {
        var other = 0x7FF6_0000_0000UL;
        var (status, list) = Parse(Build(other, new Proc(5, "x.exe")), other);
        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal("x.exe", list[0].Name);

        // Same bytes, wrong base: the pointer points outside, so the name is unknown rather than garbage.
        var (_, wrong) = Parse(Build(other, new Proc(5, "x.exe")), DefaultBase);
        Assert.Equal("[unknown]", wrong[0].Name);
    }

    [Fact]
    public void A_name_pointer_outside_the_buffer_gives_a_placeholder_and_no_exception()
    {
        var buffer = Build(DefaultBase, new Proc(5, "x.exe"));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(SystemProcessParser.ImageNameBuffer), DefaultBase + (ulong)buffer.Length + 8);
        Assert.Equal("[unknown]", Parse(buffer).Result[0].Name);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(SystemProcessParser.ImageNameBuffer), 0);
        Assert.Equal("[unknown]", Parse(buffer).Result[0].Name);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(SystemProcessParser.ImageNameBuffer), ulong.MaxValue);
        Assert.Equal("[unknown]", Parse(buffer).Result[0].Name);
    }

    [Fact]
    public void A_name_that_runs_past_the_end_or_has_an_odd_length_is_not_read()
    {
        var buffer = Build(DefaultBase, new Proc(5, "x.exe"));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SystemProcessParser.ImageNameLength), ushort.MaxValue - 1);
        Assert.Equal("[unknown]", Parse(buffer).Result[0].Name);

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SystemProcessParser.ImageNameLength), 5);
        Assert.Equal("[unknown]", Parse(buffer).Result[0].Name);
    }

    [Fact]
    public void A_next_entry_offset_past_the_end_stops_with_Malformed_and_keeps_the_earlier_entries()
    {
        var buffer = Build(DefaultBase, new Proc(1, "a.exe"), new Proc(2, "b.exe"));
        // Make the first entry claim it is longer than the whole buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(SystemProcessParser.NextEntryOffset), (uint)buffer.Length + 1);
        var (status, list) = Parse(buffer);
        Assert.Equal(ParseStatus.Malformed, status);
        Assert.Empty(list);

        var two = Build(DefaultBase, new Proc(1, "a.exe"), new Proc(2, "b.exe"), new Proc(3, "c.exe"));
        var firstSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(two);
        var secondOffset = firstSize;
        BinaryPrimitives.WriteUInt32LittleEndian(two.AsSpan(secondOffset + SystemProcessParser.NextEntryOffset), 0x7FFF_FFF0);
        var (status2, list2) = Parse(two);
        Assert.Equal(ParseStatus.Malformed, status2);
        Assert.Equal([1u], list2.Select(p => p.Pid));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0x80u)]
    [InlineData(0xFFu)]
    public void A_next_entry_offset_smaller_than_the_fixed_part_is_rejected_not_looped_on(uint next)
    {
        var buffer = Build(DefaultBase, new Proc(1, "a.exe"), new Proc(2, "b.exe"));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(SystemProcessParser.NextEntryOffset), next);
        Assert.Equal(ParseStatus.Malformed, Parse(buffer).Status);
    }

    [Fact]
    public void A_buffer_cut_inside_an_entry_is_Malformed()
    {
        var buffer = Build(DefaultBase, new Proc(1, "a.exe"), new Proc(2, "b.exe"));
        for (var cut = 1; cut < SystemProcessParser.EntrySize; cut += 17)
        {
            var (status, _) = Parse(buffer.AsSpan(0, cut).ToArray());
            Assert.Equal(ParseStatus.Malformed, status);
        }
    }

    [Fact]
    public void An_empty_buffer_yields_nothing_without_throwing()
    {
        var (status, list) = Parse([]);
        Assert.Equal(ParseStatus.Ok, status);
        Assert.Empty(list);
    }

    [Fact]
    public void Garbage_never_throws_and_never_returns_more_than_the_buffer_can_hold()
    {
        var random = new Random(12345);
        var names = new NameInterner();
        for (var round = 0; round < 500; round++)
        {
            var data = new byte[random.Next(0, 4096)];
            random.NextBytes(data);
            if (data.Length >= 4 && round % 2 == 0)
            {
                // Plausible-looking first offset, to get further into the entry than pure noise does.
                BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)random.Next(0, data.Length + 64));
            }

            var list = new List<RawProcess>();
            var status = SystemProcessParser.Parse(data, DefaultBase, names, list);
            Assert.True(list.Count <= (data.Length / SystemProcessParser.EntrySize) + 1);
            _ = status;
        }
    }

    [Fact]
    public void A_thread_count_larger_than_the_entry_is_clipped_not_trusted()
    {
        var buffer = Build(DefaultBase, new Proc(5, "x.exe", Threads: [(5, 5)], ClaimedThreads: 100_000));
        var (status, list) = Parse(buffer);
        Assert.Equal(ParseStatus.Ok, status);
        Assert.Equal(100_000, list[0].ThreadCount);
        Assert.Equal(1, list[0].ThreadsRead);
        Assert.False(list[0].AllThreadsSuspended); // a clipped list is not "all threads"
    }

    [Theory]
    [InlineData(5, 5, true)]   // Waiting + Suspended
    [InlineData(5, 12, true)]  // Waiting + WrSuspended
    [InlineData(5, 6, false)]  // Waiting + UserRequest
    [InlineData(2, 5, false)]  // Running, stale reason
    public void Suspended_means_every_thread_is_waiting_with_a_suspend_reason(int state, int reason, bool expected)
    {
        var (_, list) = Parse(Build(DefaultBase, new Proc(5, "x.exe", Threads: [(state, reason), (state, reason)])));
        Assert.Equal(expected, list[0].AllThreadsSuspended);
    }

    [Fact]
    public void One_running_thread_among_suspended_ones_is_not_suspended_and_no_threads_is_not_suspended()
    {
        var (_, list) = Parse(Build(DefaultBase, new Proc(5, "x.exe", Threads: [(5, 5), (2, 0)]), new Proc(6, "y.exe", Threads: [])));
        Assert.False(list[0].AllThreadsSuspended);
        Assert.False(list[1].AllThreadsSuspended);
    }

    [Fact]
    public void Huge_counters_are_clamped_into_long()
    {
        var buffer = Build(DefaultBase, new Proc(5, "x.exe"));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(SystemProcessParser.WorkingSetSize), ulong.MaxValue);
        Assert.Equal(long.MaxValue, Parse(buffer).Result[0].WorkingSet);
    }
}
