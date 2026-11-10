namespace PcMieru.Gui.Tests;

public class ProcessTableModelTests
{
    private static ProcessInfo Proc(uint pid, string name, double? cpu, int threads = 1, uint parent = 0, uint session = 1) =>
        new(
            new ProcessKey(pid, 1000 + pid),
            name,
            parent,
            parent == 0 ? null : new ProcessKey(parent, 1000 + parent),
            session,
            threads,
            0,
            8,
            0,
            0,
            0,
            0,
            0,
            cpu is { } c ? Value.Ok(c, "t") : Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.Ok(ProcessState.Running, "t"));

    private static async Task<ProcessTableModel> ModelAsync(params ProcessInfo[] processes) =>
        await ModelAsync(ProcessViewMode.List, processes);

    private static async Task<ProcessTableModel> ModelAsync(ProcessViewMode mode, params ProcessInfo[] processes)
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var model = new ProcessTableModel { Mode = mode };
        model.Update(snapshot with { Processes = processes });
        return model;
    }

    /// <summary>Names of the process rows in order; group headers are left out.</summary>
    private static string[] Names(ProcessTableModel model) =>
        Enumerable.Range(0, model.Count).Select(i => model[i].Process?.Name).OfType<string>().ToArray();

    [Fact]
    public async Task Starts_sorted_by_cpu_high_first()
    {
        var model = await ModelAsync(Proc(1, "low", 1), Proc(2, "high", 50), Proc(3, "mid", 10));
        Assert.Equal(["high", "mid", "low"], Names(model));
    }

    private static ProcessInfo WithDisk(ProcessInfo p, double? read, double? write) =>
        p with
        {
            IoReadBytesPerSec = read is { } r ? Value.Ok(r, "t") : Value.NotCollected<double>("t"),
            IoWriteBytesPerSec = write is { } w ? Value.Ok(w, "t") : Value.NotCollected<double>("t"),
        };

    [Fact]
    public async Task The_disk_column_ranks_by_read_plus_write_and_an_uncollected_one_goes_last()
    {
        var model = await ModelAsync(
            WithDisk(Proc(1, "reader", 1), 900, null),
            WithDisk(Proc(2, "both", 1), 500, 600),
            WithDisk(Proc(3, "unknown", 1), null, null),
            WithDisk(Proc(4, "writer", 1), null, 100));
        model.SortBy(ProcessColumn.Disk);
        Assert.Equal(["both", "reader", "writer", "unknown"], Names(model));
        model.SortBy(ProcessColumn.Disk);
        Assert.Equal("unknown", Names(model)[^1]);
    }

    private static ProcessInfo WithNet(ProcessInfo p, double? send, double? receive) =>
        p with
        {
            NetSendBytesPerSec = send is { } s ? Value.Ok(s, "t") : Value.NotCollected<double>("t"),
            NetReceiveBytesPerSec = receive is { } r ? Value.Ok(r, "t") : Value.NotCollected<double>("t"),
        };

    private static readonly ProcessInfo[] NetProcesses =
    [
        WithNet(Proc(1, "sender", 1), 900, null),
        WithNet(Proc(2, "both", 1), 500, 600),
        WithNet(Proc(3, "unknown", 1), null, null),
        WithNet(Proc(4, "receiver", 1), null, 100),
        WithNet(Proc(5, "quiet", 1), 0, 0),
    ];

    [Fact]
    public async Task The_network_column_ranks_by_send_plus_receive_high_first_and_an_uncollected_one_goes_last()
    {
        var model = await ModelAsync(NetProcesses);
        model.SortBy(ProcessColumn.Network);
        Assert.Equal(["both", "sender", "receiver", "quiet", "unknown"], Names(model));
    }

    [Fact]
    public async Task A_missing_network_value_stays_last_in_both_directions_and_the_rest_flips()
    {
        var model = await ModelAsync(NetProcesses);
        model.SortBy(ProcessColumn.Network);
        model.SortBy(ProcessColumn.Network);
        Assert.Equal(["quiet", "receiver", "sender", "both", "unknown"], Names(model));
    }

    [Fact]
    public async Task A_process_with_only_one_side_collected_counts_the_other_as_zero_for_the_sum()
    {
        var model = await ModelAsync(
            WithNet(Proc(1, "send-only", 1), 300, null),
            WithNet(Proc(2, "both", 1), 100, 150));
        model.SortBy(ProcessColumn.Network);
        Assert.Equal(["send-only", "both"], Names(model));
    }

    [Fact]
    public async Task The_send_and_receive_columns_rank_each_side_on_its_own_with_the_uncollected_last()
    {
        var model = await ModelAsync(NetProcesses);
        model.SortBy(ProcessColumn.NetSend);
        Assert.Equal(["sender", "both", "quiet"], Names(model).Take(3));
        Assert.Equal(["receiver", "unknown"], Names(model).TakeLast(2).Order());
        model.SortBy(ProcessColumn.NetReceive);
        Assert.Equal(["both", "receiver", "quiet"], Names(model).Take(3));
    }

    [Fact]
    public async Task The_flat_view_ranks_every_process_in_one_list_with_no_group_headers()
    {
        var processes = new[] { Proc(1, "a", 5), Proc(2, "b", 50), Proc(3, "c", 20), Proc(4, "d", 1) };
        var grouped = await ModelAsync(ProcessViewMode.List, processes);
        var flat = await ModelAsync(ProcessViewMode.Flat, processes);
        Assert.Equal(["b", "c", "a", "d"], Names(flat));
        Assert.Equal(processes.Length, flat.Count);
        Assert.DoesNotContain(Enumerable.Range(0, flat.Count), i => flat[i].IsGroup);
        Assert.Contains(Enumerable.Range(0, grouped.Count), i => grouped[i].IsGroup);
    }

    [Fact]
    public async Task Same_column_again_flips_and_a_new_text_column_starts_ascending()
    {
        var model = await ModelAsync(Proc(1, "b", 1), Proc(2, "a", 2), Proc(3, "c", 3));
        model.SortBy(ProcessColumn.Cpu);
        Assert.Equal(["b", "a", "c"], Names(model));
        model.SortBy(ProcessColumn.Name);
        Assert.Equal(["a", "b", "c"], Names(model));
        model.SortBy(ProcessColumn.Name);
        Assert.Equal(["c", "b", "a"], Names(model));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_value_that_was_not_collected_sorts_last_in_either_direction(bool descending)
    {
        var model = await ModelAsync(Proc(1, "none", null), Proc(2, "zero", 0), Proc(3, "some", 5));
        if (!descending)
        {
            model.SortBy(ProcessColumn.Cpu);
        }

        Assert.Equal("none", Names(model)[^1]);
    }

    [Fact]
    public async Task Ties_fall_back_to_pid_so_rows_do_not_jump_between_samples()
    {
        var model = await ModelAsync(Proc(30, "c", 1), Proc(10, "a", 1), Proc(20, "b", 1));
        Assert.Equal(["a", "b", "c"], Names(model));
    }

    [Fact]
    public async Task Filter_matches_part_of_the_name_ignoring_case_or_an_exact_pid()
    {
        var model = await ModelAsync(Proc(100, "Chrome.exe", 1), Proc(200, "code.exe", 2), Proc(300, "svchost.exe", 3));
        model.Filter = "  CHR ";
        Assert.Equal(["Chrome.exe"], Names(model));
        Assert.Equal(3, model.TotalCount);
        Assert.Equal(1, model.ShownProcesses);
        model.Filter = "300";
        Assert.Equal(["svchost.exe"], Names(model));
        model.Filter = "30";
        Assert.Empty(Names(model));
        model.Filter = string.Empty;
        Assert.Equal(3, model.ShownProcesses);
    }

    [Fact]
    public async Task IndexOf_finds_a_process_by_identity_not_by_pid_alone()
    {
        var reused = Proc(7, "new", 1) with { Key = new ProcessKey(7, 9999) };
        var model = await ModelAsync(Proc(7, "old", 5), Proc(8, "other", 1));
        var oldKey = new ProcessKey(7, 1007);
        Assert.True(model.IndexOf(oldKey) >= 0);
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [reused, Proc(8, "other", 1)] });
        Assert.Equal(-1, model.IndexOf(oldKey));
        Assert.True(model.IndexOf(reused.Key) >= 0);
        Assert.Null(model.Find(oldKey));
        Assert.Equal("new", model.Find(reused.Key)?.Name);
    }

    [Fact]
    public async Task Update_keeps_the_sort_and_filter()
    {
        var model = await ModelAsync(Proc(1, "alpha", 1), Proc(2, "beta", 2));
        model.SortBy(ProcessColumn.Name);
        model.Filter = "a";
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [Proc(3, "gamma", 1), Proc(4, "delta", 9), Proc(5, "zzz", 9)] });
        Assert.Equal(["delta", "gamma"], Names(model));
    }

    [Fact]
    public async Task Works_on_the_fake_snapshot_for_every_column_and_both_modes()
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        foreach (var mode in Enum.GetValues<ProcessViewMode>())
        {
            var model = new ProcessTableModel { Mode = mode };
            model.Update(snapshot);
            foreach (var column in Enum.GetValues<ProcessColumn>())
            {
                model.SortBy(column);
                Assert.Equal(snapshot.Processes.Count, model.ShownProcesses);
                Assert.Equal(snapshot.Processes.Count, Enumerable.Range(0, model.Count).Count(i => model[i].Process is not null));
            }
        }
    }

    [Fact]
    public async Task List_mode_puts_a_header_before_each_group_in_a_fixed_order()
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var model = new ProcessTableModel { Mode = ProcessViewMode.List };
        model.Update(
            snapshot with { Processes = [Proc(1, "a", 1), Proc(2, "b", 5), Proc(3, "c", 3), Proc(4, "d", 2)] },
            p => p.Key.Pid switch { 1 => ProcessGroup.Windows, 2 => ProcessGroup.App, _ => ProcessGroup.Background });
        var headers = Enumerable.Range(0, model.Count).Where(i => model[i].IsGroup).ToArray();
        Assert.Equal(3, headers.Length);
        Assert.Equal([ProcessGroup.App, ProcessGroup.Background, ProcessGroup.Windows], headers.Select(i => model[i].Group));
        Assert.Equal([1, 2, 1], headers.Select(i => model[i].GroupCount));
        Assert.Equal(["b", "c", "d", "a"], Names(model));
    }

    [Fact]
    public async Task A_group_with_no_matching_process_has_no_header()
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var model = new ProcessTableModel { Mode = ProcessViewMode.List };
        model.Update(
            snapshot with { Processes = [Proc(1, "a", 1), Proc(2, "b", 5)] },
            p => p.Key.Pid == 1 ? ProcessGroup.Windows : ProcessGroup.App);
        model.Filter = "b";
        Assert.Equal(1, Enumerable.Range(0, model.Count).Count(i => model[i].IsGroup));
    }

    [Fact]
    public async Task Tree_mode_nests_children_under_their_parent_and_collapses()
    {
        var model = await ModelAsync(ProcessViewMode.Tree, Proc(1, "root", 1), Proc(2, "kid", 9, parent: 1), Proc(3, "grandkid", 1, parent: 2), Proc(4, "other", 5));
        var rows = Enumerable.Range(0, model.Count).Select(i => model[i]).ToList();
        Assert.Equal(["other", "root", "kid", "grandkid"], rows.Select(r => r.Process!.Name));
        Assert.Equal([0, 0, 1, 2], rows.Select(r => r.Depth));
        Assert.True(rows[1].HasChildren && rows[1].Expanded);

        model.ToggleExpanded(new ProcessKey(1, 1001));
        Assert.Equal(["other", "root"], Names(model));
        Assert.False(model[1].Expanded);
        model.ToggleExpanded(new ProcessKey(1, 1001));
        Assert.Equal(4, model.Count);
    }

    [Fact]
    public async Task Tree_filter_keeps_the_path_to_a_match_visible()
    {
        var model = await ModelAsync(ProcessViewMode.Tree, Proc(1, "root", 1), Proc(2, "kid", 9, parent: 1), Proc(3, "needle", 1, parent: 2), Proc(4, "other", 5));
        model.Filter = "needle";
        Assert.Equal(["root", "kid", "needle"], Names(model));
    }

    [Fact]
    public async Task A_parent_that_is_gone_or_a_cycle_does_not_hang_or_lose_rows()
    {
        var a = Proc(1, "a", 1, parent: 2);
        var b = Proc(2, "b", 1, parent: 1);
        var orphan = Proc(3, "orphan", 1, parent: 99);
        var model = await ModelAsync(ProcessViewMode.Tree, a, b, orphan);
        Assert.Contains("orphan", Names(model));
        Assert.True(model.Count >= 1);
    }

    [Fact]
    public async Task While_frozen_the_rows_keep_their_place_but_the_numbers_update()
    {
        var model = await ModelAsync(Proc(1, "a", 50), Proc(2, "b", 10));
        Assert.Equal(["a", "b"], Names(model));
        model.Frozen = true;
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [Proc(1, "a", 1), Proc(2, "b", 80)] });
        Assert.Equal(["a", "b"], Names(model));
        Assert.Equal(80, model.Find(new ProcessKey(2, 1002))!.CpuPercent.GetOr(0));
        model.Resort();
        Assert.Equal(["b", "a"], Names(model));
    }

    [Fact]
    public async Task A_new_process_goes_to_the_end_while_frozen_instead_of_pushing_rows_down()
    {
        var model = await ModelAsync(Proc(1, "a", 50), Proc(2, "b", 10));
        model.Frozen = true;
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [Proc(1, "a", 50), Proc(2, "b", 10), Proc(3, "fresh", 99)] });
        Assert.Equal(["a", "b", "fresh"], Names(model));
    }

    [Fact]
    public async Task Unfrozen_the_next_sample_sorts_again()
    {
        var model = await ModelAsync(Proc(1, "a", 50), Proc(2, "b", 10));
        model.Frozen = true;
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [Proc(1, "a", 1), Proc(2, "b", 80)] });
        model.Frozen = false;
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [Proc(1, "a", 1), Proc(2, "b", 80)] });
        Assert.Equal(["b", "a"], Names(model));
    }

    [Fact]
    public async Task Clicking_a_header_sorts_even_while_frozen()
    {
        var model = await ModelAsync(Proc(1, "b", 50), Proc(2, "a", 10));
        model.Frozen = true;
        model.SortBy(ProcessColumn.Name);
        Assert.Equal(["a", "b"], Names(model));
    }
}
