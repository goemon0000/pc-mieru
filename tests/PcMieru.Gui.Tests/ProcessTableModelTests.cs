namespace PcMieru.Gui.Tests;

public class ProcessTableModelTests
{
    private static ProcessInfo Proc(uint pid, string name, double? cpu, int threads = 1) =>
        new(
            new ProcessKey(pid, 1000 + pid),
            name,
            0,
            null,
            1,
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

    private static async Task<ProcessTableModel> ModelAsync(params ProcessInfo[] processes)
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var model = new ProcessTableModel();
        model.Update(snapshot with { Processes = processes });
        return model;
    }

    private static string[] Names(ProcessTableModel model) => Enumerable.Range(0, model.Count).Select(i => model[i].Name).ToArray();

    [Fact]
    public async Task Starts_sorted_by_cpu_high_first()
    {
        var model = await ModelAsync(Proc(1, "low", 1), Proc(2, "high", 50), Proc(3, "mid", 10));
        Assert.Equal(["high", "mid", "low"], Names(model));
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
        model.Filter = "300";
        Assert.Equal(["svchost.exe"], Names(model));
        model.Filter = "30";
        Assert.Empty(Names(model));
        model.Filter = string.Empty;
        Assert.Equal(3, model.Count);
    }

    [Fact]
    public async Task IndexOf_finds_a_process_by_identity_not_by_pid_alone()
    {
        var reused = Proc(7, "new", 1) with { Key = new ProcessKey(7, 9999) };
        var model = await ModelAsync(Proc(7, "old", 5), Proc(8, "other", 1));
        var oldKey = new ProcessKey(7, 1007);
        Assert.Equal(0, model.IndexOf(oldKey));
        model.Update((await Harness.FakeSnapshotAsync()) with { Processes = [reused, Proc(8, "other", 1)] });
        Assert.Equal(-1, model.IndexOf(oldKey));
        Assert.Equal(0, model.IndexOf(reused.Key));
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
    public async Task Works_on_the_fake_snapshot_for_every_column()
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var model = new ProcessTableModel();
        model.Update(snapshot);
        foreach (var column in Enum.GetValues<ProcessColumn>())
        {
            model.SortBy(column);
            Assert.Equal(snapshot.Processes.Count, model.Count);
        }
    }
}
