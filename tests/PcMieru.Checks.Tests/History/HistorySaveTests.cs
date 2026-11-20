using System.Text;
using System.Text.Json;

namespace PcMieru.Checks.Tests;

/// <summary>DESIGN-A §4: the atomic write, the name, the never-overwrite rule and the failure values.</summary>
public sealed class HistorySaveTests : IDisposable
{
    private const string MemoryName = "memory-20261120T030004200Z-89914ef957814cf7a4f86d58003ddf86.json";

    private readonly TempRoot _tmp = new();

    // A folder that does not exist yet: the first save creates it.
    private string Root => Path.Combine(_tmp.Path, "checks");

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task A_saved_result_is_one_file_that_lists_and_reads_back()
    {
        var result = Samples.MemoryCheck();
        var store = Kit.Store(Root);

        var state = await store.SaveAsync(result);

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal(MemoryName, state.FileName);
        Assert.Null(state.FailureCode);
        Assert.Null(state.Detail);
        Assert.Equal([MemoryName], Kit.Names(Root));

        // The bytes are the golden JSON of the sample (written by hand-checked fixture, not by the saver), one final newline, no BOM.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Root, MemoryName));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(Fixture.Normalize(Fixture.Read("memory.json")), new UTF8Encoding(false, true).GetString(bytes));

        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("89914ef9-5781-4cf7-a4f8-6d58003ddf86", doc.RootElement.GetProperty("runId").GetString());
        Assert.Equal("2026-11-20T03:00:04.200Z", doc.RootElement.GetProperty("endedAtUtc").GetString());

        var page = await store.ListAsync(Kit.All, CancellationToken.None);
        Assert.Empty(page.Warnings);
        var entry = Assert.Single(page.Entries);
        Assert.Equal(
            new CheckHistoryEntry(
                Guid.Parse("89914ef9-5781-4cf7-a4f8-6d58003ddf86"),
                CheckKind.Memory,
                new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 20, 3, 0, 4, 200, TimeSpan.Zero),
                CheckOutcome.Stopped,
                CheckVerdict.Check,
                new CheckCounts(Ok: 0, Caution: 0, Check: 1, Unavailable: 0, Info: 8)),
            entry);

        var read = await store.GetAsync(Samples.MemoryRun, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(CheckJson.Serialize(result, indented: true), CheckJson.Serialize(read, indented: true));
        Assert.Null(await store.GetAsync(Kit.Id(77), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, "030000000Z", "03:00:00.000Z")]
    [InlineData(1, "030000001Z", "03:00:00.001Z")]
    [InlineData(999, "030000999Z", "03:00:00.999Z")]
    public async Task The_millisecond_in_the_name_is_the_millisecond_in_the_json(int millis, string stamp, string jsonTime)
    {
        var end = Samples.Start.AddMilliseconds(millis);
        var id = Kit.Id(5);
        var result = Kit.Make(CheckKind.Cpu, end, id, durationMs: 0);

        var state = await Kit.Store(Root).SaveAsync(result);

        var expected = $"cpu-20261120T{stamp}-00000000000040008000000000000005.json";
        Assert.Equal(expected, state.FileName);
        Assert.Contains($"\"endedAtUtc\": \"2026-11-20T{jsonTime}\"", await File.ReadAllTextAsync(Path.Combine(Root, expected)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_result_is_refused_and_nothing_is_written()
    {
        var broken = Samples.CpuCheck() with { App = string.Empty };

        var state = await Kit.Store(Root).SaveAsync(broken);

        Assert.Equal(CheckSaveStatus.Failed, state.Status);
        Assert.Equal("invalid_result", state.FailureCode);
        Assert.Null(state.FileName);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task A_result_whose_counts_disagree_with_its_items_is_refused()
    {
        var broken = Samples.CpuCheck() with { Counts = new CheckCounts(0, 0, 0, 0, 1) };

        var state = await Kit.Store(Root).SaveAsync(broken);

        Assert.Equal("invalid_result", state.FailureCode);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task A_save_never_overwrites_a_file_with_the_same_name()
    {
        var result = Samples.MemoryCheck();
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, MemoryName);
        await File.WriteAllTextAsync(path, "SENTINEL: not json at all");
        var before = Kit.Snapshot(Root);

        var state = await Kit.Store(Root).SaveAsync(result);

        Assert.Equal(CheckSaveStatus.Failed, state.Status);
        Assert.Equal("already_exists", state.FailureCode);
        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public async Task Saving_the_same_run_twice_keeps_the_first_file_byte_for_byte()
    {
        var store = Kit.Store(Root);
        Assert.Equal(CheckSaveStatus.Saved, (await store.SaveAsync(Samples.MemoryCheck())).Status);
        var before = Kit.Snapshot(Root);

        var second = await store.SaveAsync(Samples.MemoryCheck());

        Assert.Equal("already_exists", second.FailureCode);
        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public async Task A_file_that_appears_between_the_check_and_the_rename_is_not_overwritten()
    {
        var files = new FaultFileSystem();
        var finalPath = Path.Combine(Root, MemoryName);
        files.MoveFault = (_, destination) =>
        {
            // Another process commits the same name after our existence check and before our rename.
            File.WriteAllText(destination, "SENTINEL from another process");
            return null;
        };

        var state = await Kit.Store(Root, files: files).SaveAsync(Samples.MemoryCheck());

        Assert.Equal("already_exists", state.FailureCode);
        Assert.Equal("SENTINEL from another process", await File.ReadAllTextAsync(finalPath));
        Assert.Equal([MemoryName], Kit.Names(Root));
    }

    [Fact]
    public async Task The_tmp_file_is_created_new_written_flushed_closed_and_then_renamed()
    {
        var files = new FaultFileSystem();
        var tmpName = Kit.TempNameOf(MemoryName, 1);

        var state = await Kit.Store(Root, files: files).SaveAsync(Samples.MemoryCheck());

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal(
            ["mkdir", $"create {tmpName}", "write", "flush", "close", $"move {tmpName} -> {MemoryName}"],
            files.Log.Take(6).ToArray());
        Assert.Equal([MemoryName], Kit.Names(Root));
    }

    [Fact]
    public async Task The_tmp_file_is_in_the_same_folder_and_is_not_listed_while_it_is_being_written()
    {
        var files = new FaultFileSystem();
        var tmpName = Kit.TempNameOf(MemoryName, 1);
        CheckHistoryPage? during = null;
        string[]? namesDuring = null;
        files.FlushFault = _ =>
        {
            // The .tmp exists and holds the bytes; another reader lists the folder right now.
            namesDuring = Kit.Names(Root);
            during = Kit.Store(Root).ListAsync(Kit.All, CancellationToken.None).GetAwaiter().GetResult();
            return null;
        };

        var state = await Kit.Store(Root, files: files).SaveAsync(Samples.MemoryCheck());

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.NotNull(namesDuring);
        Assert.Equal([tmpName], namesDuring);
        Assert.NotNull(during);
        Assert.Empty(during.Entries);
        Assert.Empty(during.Warnings);
        Assert.Single((await Kit.Store(Root).ListAsync(Kit.All, CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task A_taken_tmp_name_is_skipped_and_the_file_there_is_left_alone()
    {
        Directory.CreateDirectory(Root);
        var taken = Path.Combine(Root, Kit.TempNameOf(MemoryName, 1));
        await File.WriteAllTextAsync(taken, "SENTINEL: another writer's tmp");
        Kit.SetAge(taken, Kit.Now);
        var files = new FaultFileSystem();

        var state = await Kit.Store(Root, files: files).SaveAsync(Samples.MemoryCheck());

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal("SENTINEL: another writer's tmp", await File.ReadAllTextAsync(taken));
        Assert.Equal(1, files.Count($"create {Kit.TempNameOf(MemoryName, 1)}"));
        Assert.Equal(1, files.Count($"create {Kit.TempNameOf(MemoryName, 2)}"));
        Assert.Equal([MemoryName, Kit.TempNameOf(MemoryName, 1)], Kit.Names(Root));
    }

    public static IEnumerable<object[]> Faults()
    {
        yield return new object[] { "create", new UnauthorizedAccessException("denied"), "access_denied" };
        yield return new object[] { "create", Kit.DiskFull(), "disk_full" };
        yield return new object[] { "write", Kit.DiskFull(), "disk_full" };
        yield return new object[] { "write", new UnauthorizedAccessException("denied"), "access_denied" };
        yield return new object[] { "flush", new IOException("device error"), "io_error" };
        yield return new object[] { "move", new IOException("device error"), "io_error" };
        yield return new object[] { "move", new UnauthorizedAccessException("denied"), "access_denied" };
        yield return new object[] { "move", Kit.DiskFull(), "disk_full" };
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task A_failure_is_a_value_with_a_code_and_leaves_no_file_of_ours(string where, Exception fault, string code)
    {
        var files = new FaultFileSystem();
        switch (where)
        {
            case "create": files.CreateNewFault = _ => fault; break;
            case "write": files.WriteFault = _ => fault; break;
            case "flush": files.FlushFault = _ => fault; break;
            case "move": files.MoveFault = (_, _) => fault; break;
        }

        var state = await Kit.Store(Root, files: files).SaveAsync(Samples.MemoryCheck());

        Assert.Equal(CheckSaveStatus.Failed, state.Status);
        Assert.Equal(code, state.FailureCode);
        Assert.Null(state.FileName);
        Assert.NotNull(state.Detail);
        Assert.Contains(fault.GetType().Name, state.Detail, StringComparison.Ordinal);
        Assert.Empty(Kit.Names(Root));
    }

    [Fact]
    public async Task A_failed_save_never_deletes_a_file_that_was_already_committed()
    {
        var files = new FaultFileSystem();
        var store = Kit.Store(Root, files: files);
        Assert.Equal(CheckSaveStatus.Saved, (await store.SaveAsync(Samples.CpuCheck())).Status);
        var cpuName = Kit.NameOf(Samples.CpuCheck());
        var before = Kit.Snapshot(Root);
        Assert.Contains(cpuName, Kit.Names(Root));
        var deletesBefore = files.Count("delete");

        files.MoveFault = (_, _) => new IOException("device error");
        var state = await store.SaveAsync(Samples.MemoryCheck());

        Assert.Equal("io_error", state.FailureCode);
        Assert.Equal(before, Kit.Snapshot(Root));
        Assert.Equal(deletesBefore + 1, files.Count("delete"));
        Assert.StartsWith("delete memory-", files.Log.Last(entry => entry.StartsWith("delete", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_problem_in_the_housekeeping_after_the_commit_does_not_fail_the_save()
    {
        var files = new FaultFileSystem { ListFault = _ => new IOException("listing failed") };

        var state = await Kit.Store(Root, files: files).SaveAsync(Samples.MemoryCheck());

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal([MemoryName], Kit.Names(Root));
    }

    [Fact]
    public async Task Eight_saves_of_one_run_at_once_commit_exactly_one_file()
    {
        var result = Samples.MemoryCheck();
        var store = Kit.Store(Root, time: Kit.RealClock());

        var states = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.SaveAsync(result))));

        Assert.Equal(1, states.Count(s => s.Status == CheckSaveStatus.Saved));
        Assert.Equal(7, states.Count(s => s.FailureCode == "already_exists"));
        Assert.Equal([MemoryName], Kit.Names(Root));
        Assert.Equal(Fixture.Normalize(Fixture.Read("memory.json")), await File.ReadAllTextAsync(Path.Combine(Root, MemoryName)));
    }

    [Fact]
    public async Task Many_different_runs_saved_at_once_all_land_and_leave_no_tmp()
    {
        var store = Kit.Store(Root, time: Kit.RealClock());
        var results = Enumerable.Range(1, 24).Select(n => Kit.Make(CheckKind.Cpu, Samples.Start.AddSeconds(n), Kit.Id(n))).ToList();

        var states = await Task.WhenAll(results.Select(r => Task.Run(() => store.SaveAsync(r))));

        Assert.All(states, s => Assert.Equal(CheckSaveStatus.Saved, s.Status));
        Assert.Equal(
            results.Select(Kit.NameOf).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            Kit.Names(Root));
    }

    [Fact]
    public async Task A_cancelled_token_stops_a_save_before_it_starts()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.Store(Root).SaveAsync(Samples.MemoryCheck(), cts.Token));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void The_default_root_is_local_app_data_pcmieru_checks()
    {
        var expected = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "PCMieru", "checks");

        Assert.Equal(expected, CheckHistoryStore.DefaultRoot());
    }

    [Fact]
    public void The_save_failure_codes_are_the_fixed_list()
    {
        Assert.Equal(["invalid_result", "already_exists", "access_denied", "disk_full", "io_error"], CheckSaveFailureCodes.All.ToArray());
        Assert.Equal(CheckSaveStatus.NotSaved, CheckSaveState.NotSaved.Status);
        Assert.Equal("a.json", CheckSaveState.Saved("a.json").FileName);
        Assert.Equal("disk_full", CheckSaveState.Failed("disk_full", "x").FailureCode);
    }
}
