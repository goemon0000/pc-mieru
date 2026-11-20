namespace PcMieru.Checks.Tests;

/// <summary>DESIGN-A §4: listing order, the cross-check of name and body, the quarantine and the files that are kept.</summary>
public sealed class HistoryListTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private string Root => Path.Combine(_tmp.Path, "checks");

    public void Dispose() => _tmp.Dispose();

    private static DateTimeOffset T(int seconds) => Samples.Start.AddSeconds(seconds);

    private CheckHistoryStore Store(ICheckHistoryFileSystem? files = null) => Kit.Store(Root, files: files);

    // ---- order ----

    [Fact]
    public async Task Entries_are_newest_end_time_first_whatever_the_kind_or_the_start()
    {
        // b started after a did, but ended before it: the end time decides.
        var a = Kit.Make(CheckKind.Memory, T(30), Kit.Id(1), durationMs: 30000);
        var b = Kit.Make(CheckKind.Cpu, T(25), Kit.Id(2), durationMs: 5000);
        var c = Kit.Make(CheckKind.System, T(40), Kit.Id(3));
        var d = Kit.Make(CheckKind.Storage, T(10), Kit.Id(4));
        foreach (var result in new[] { d, a, c, b })
        {
            Kit.Put(Root, result);
        }

        var page = await Store().ListAsync(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(3), Kit.Id(1), Kit.Id(2), Kit.Id(4)], Kit.RunIds(page));
        Assert.Empty(page.Warnings);
        var cpuOnly = await Store().ListAsync(new CheckHistoryQuery(CheckKind.Cpu, null), CancellationToken.None);
        Assert.Equal([Kit.Id(2)], Kit.RunIds(cpuOnly));
    }

    [Fact]
    public async Task Equal_end_times_are_ordered_by_uuid_ascending_across_kinds()
    {
        var newest = Kit.Make(CheckKind.Cpu, T(60), Kit.Id(99));
        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(50), Kit.Id(30)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(50), Kit.Id(10)));
        Kit.Put(Root, newest);
        Kit.Put(Root, Kit.Make(CheckKind.Storage, T(50), Kit.Id(20)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(40), Kit.Id(1)));

        var page = await Store().ListAsync(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(99), Kit.Id(10), Kit.Id(20), Kit.Id(30), Kit.Id(1)], Kit.RunIds(page));
    }

    [Fact]
    public async Task A_limit_cuts_the_list_to_the_newest_and_zero_or_less_is_refused()
    {
        for (var n = 1; n <= 5; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(n), Kit.Id(n)));
        }

        var store = Store();
        Assert.Equal([Kit.Id(5), Kit.Id(4), Kit.Id(3)], Kit.RunIds(await store.ListAsync(new CheckHistoryQuery(null, 3), CancellationToken.None)));
        Assert.Equal(5, (await store.ListAsync(new CheckHistoryQuery(null, 200), CancellationToken.None)).Entries.Count);
        Assert.Equal(5, (await store.ListAsync(new CheckHistoryQuery(null, null), CancellationToken.None)).Entries.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(new CheckHistoryQuery(null, 0), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(new CheckHistoryQuery(null, -1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ListAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_or_empty_folder_lists_nothing_and_is_not_created()
    {
        var page = await Store().ListAsync(Kit.All, CancellationToken.None);

        Assert.Empty(page.Entries);
        Assert.Empty(page.Warnings);
        Assert.False(Directory.Exists(Root));

        Directory.CreateDirectory(Root);
        Assert.Empty((await Store().ListAsync(Kit.All, CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_listing()
    {
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(1), Kit.Id(1)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().ListAsync(Kit.All, cts.Token));
    }

    // ---- the name must agree with the body ----

    public static IEnumerable<object[]> Mismatches()
    {
        yield return new object[] { "time", "name_time_mismatch" };
        yield return new object[] { "uuid", "name_id_mismatch" };
        yield return new object[] { "kind", "name_kind_mismatch" };
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task A_name_that_disagrees_with_the_body_is_moved_aside(string field, string reason)
    {
        var sibling = Kit.Make(CheckKind.Cpu, T(5), Kit.Id(100));
        Kit.Put(Root, sibling);
        var body = Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1));
        var wrongName = field switch
        {
            "time" => Kit.NameOf(CheckKind.Cpu, T(10).AddMilliseconds(1), Kit.Id(1)),
            "uuid" => Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(2)),
            "kind" => Kit.NameOf(CheckKind.Memory, T(10), Kit.Id(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Kit.Put(Root, body, wrongName);
        var original = File.ReadAllBytes(Path.Combine(Root, wrongName));
        var badName = $"{wrongName}.bad-{Kit.NowStamp}";

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(100)], Kit.RunIds(scan.Page));
        Assert.Equal(["history_unreadable=1", "history_quarantined=1"], Kit.WarningLines(scan.Page));
        var problem = Assert.Single(scan.Problems);
        Assert.Equal(new CheckHistoryProblem(wrongName, "history_unreadable", reason, badName, false), problem);
        Assert.Equal(new[] { Kit.NameOf(sibling), badName }.OrderBy(n => n, StringComparer.Ordinal).ToArray(), Kit.Names(Root));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Root, badName)));

        // The .bad file is not a history file: the next listing has nothing to say.
        var again = await Store().ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal([Kit.Id(100)], Kit.RunIds(again));
        Assert.Empty(again.Warnings);
    }

    // ---- corrupt files ----

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("plain words, not json")]
    [InlineData("{\"format\":\"pcm-check/1\"")]
    public void A_file_that_is_not_a_result_is_moved_aside_and_reported(string content)
    {
        var good = Kit.Make(CheckKind.Cpu, T(5), Kit.Id(100));
        Kit.Put(Root, good);
        var corruptName = Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.PutText(Root, corruptName, content);
        var original = File.ReadAllBytes(Path.Combine(Root, corruptName));

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(100)], Kit.RunIds(scan.Page));
        Assert.Equal(["history_unreadable=1", "history_quarantined=1"], Kit.WarningLines(scan.Page));
        var problem = Assert.Single(scan.Problems);
        Assert.Equal("invalid_content", problem.Reason);
        Assert.Equal($"{corruptName}.bad-{Kit.NowStamp}", problem.QuarantinedAs);
        Assert.False(File.Exists(Path.Combine(Root, corruptName)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Root, $"{corruptName}.bad-{Kit.NowStamp}")));
    }

    [Fact]
    public void Fields_that_disagree_make_the_file_unreadable_even_when_the_json_is_well_formed()
    {
        var result = Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1));
        var name = Kit.NameOf(result);
        Kit.PutText(Root, name, Kit.Edited(result, "\"app\": \"PCMieru 0.4.0\"", "\"app\": \"\""));

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        Assert.Empty(scan.Page.Entries);
        Assert.Equal(["history_unreadable=1", "history_quarantined=1"], Kit.WarningLines(scan.Page));
        Assert.Equal("invalid_content", Assert.Single(scan.Problems).Reason);
        Assert.Equal([$"{name}.bad-{Kit.NowStamp}"], Kit.Names(Root));
    }

    [Fact]
    public void A_taken_bad_name_is_never_overwritten_the_next_free_one_is_used()
    {
        var name = Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.PutText(Root, name, "{ corrupt");
        Kit.PutText(Root, $"{name}.bad-{Kit.NowStamp}", "SENTINEL 0");
        Kit.PutText(Root, $"{name}.bad-{Kit.NowStamp}-1", "SENTINEL 1");

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        var problem = Assert.Single(scan.Problems);
        Assert.Equal($"{name}.bad-{Kit.NowStamp}-2", problem.QuarantinedAs);
        Assert.Equal("SENTINEL 0", File.ReadAllText(Path.Combine(Root, $"{name}.bad-{Kit.NowStamp}")));
        Assert.Equal("SENTINEL 1", File.ReadAllText(Path.Combine(Root, $"{name}.bad-{Kit.NowStamp}-1")));
        Assert.Equal("{ corrupt", File.ReadAllText(Path.Combine(Root, $"{name}.bad-{Kit.NowStamp}-2")));
        Assert.False(File.Exists(Path.Combine(Root, name)));
    }

    [Fact]
    public void A_bad_name_taken_between_the_check_and_the_rename_is_not_overwritten()
    {
        var name = Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.PutText(Root, name, "{ corrupt");
        var files = new FaultFileSystem();
        var raced = false;
        files.MoveFault = (_, destination) =>
        {
            if (!raced)
            {
                raced = true;
                File.WriteAllText(destination, "SENTINEL from another process");
            }

            return null;
        };

        var scan = Store(files).Scan(Kit.All, CancellationToken.None);

        Assert.Equal($"{name}.bad-{Kit.NowStamp}-1", Assert.Single(scan.Problems).QuarantinedAs);
        Assert.Equal("SENTINEL from another process", File.ReadAllText(Path.Combine(Root, $"{name}.bad-{Kit.NowStamp}")));
        Assert.Equal("{ corrupt", File.ReadAllText(Path.Combine(Root, $"{name}.bad-{Kit.NowStamp}-1")));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void When_the_rename_fails_the_file_stays_as_it_is_and_nothing_is_written_to_it(Type faultType)
    {
        var name = Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.PutText(Root, name, "{ corrupt");
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(5), Kit.Id(100)));
        var before = Kit.Snapshot(Root);
        var files = new FaultFileSystem { MoveFault = (_, _) => (Exception)Activator.CreateInstance(faultType, "refused")! };

        var scan = Store(files).Scan(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(100)], Kit.RunIds(scan.Page));
        Assert.Equal(["history_unreadable=1", "history_quarantine_failed=1"], Kit.WarningLines(scan.Page));
        var problem = Assert.Single(scan.Problems);
        Assert.Null(problem.QuarantinedAs);
        Assert.True(problem.QuarantineFailed);
        Assert.Equal(before, Kit.Snapshot(Root));

        // It never writes back: no file was created, written or deleted; the only attempt was the rename (name 0).
        Assert.Equal(0, files.Count("create"));
        Assert.Equal(0, files.Count("write"));
        Assert.Equal(0, files.Count("delete"));
        Assert.Equal(1, files.Count("move"));

        // The same file is tried again by the next listing, and moved once the rename works.
        files.MoveFault = null;
        var later = Store(files).Scan(Kit.All, CancellationToken.None);
        Assert.Equal(["history_unreadable=1", "history_quarantined=1"], Kit.WarningLines(later.Page));
        Assert.False(File.Exists(Path.Combine(Root, name)));
    }

    [Fact]
    public void A_corrupt_file_that_another_process_moved_first_is_not_reported_twice()
    {
        var name = Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.PutText(Root, name, "{ corrupt");
        var files = new FaultFileSystem();
        files.MoveFault = (source, _) =>
        {
            // The other process wins the race: the file is gone when this one tries.
            File.Move(source, source + ".bad-other");
            return null;
        };

        var scan = Store(files).Scan(Kit.All, CancellationToken.None);

        Assert.Empty(scan.Problems);
        Assert.Empty(scan.Page.Warnings);
        Assert.Equal([$"{name}.bad-other"], Kit.Names(Root));
    }

    // ---- files that are kept ----

    [Fact]
    public void A_file_of_a_newer_format_is_kept_and_reported_future_format()
    {
        var result = Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1));
        var name = Kit.NameOf(result);
        Kit.PutText(Root, name, Kit.Edited(result, "\"format\": \"pcm-check/1\"", "\"format\": \"pcm-check/2\""));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(5), Kit.Id(100)));
        var before = Kit.Snapshot(Root);

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(100)], Kit.RunIds(scan.Page));
        Assert.Equal(["history_future_format=1"], Kit.WarningLines(scan.Page));
        Assert.Equal(new CheckHistoryProblem(name, "history_future_format", "future_format", null, false), Assert.Single(scan.Problems));
        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public void A_file_of_a_kind_this_version_does_not_know_is_kept_and_reported_future_format()
    {
        var result = Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1));
        var name = $"gpu-{Kit.Stamp(T(10))}-{Kit.Id(1):N}.json";
        Kit.PutText(Root, name, Kit.Edited(result, "\"kind\": \"cpu\"", "\"kind\": \"gpu\""));
        var before = Kit.Snapshot(Root);

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        Assert.Empty(scan.Page.Entries);
        Assert.Equal(["history_future_format=1"], Kit.WarningLines(scan.Page));
        Assert.Equal(new CheckHistoryProblem(name, "history_future_format", "future_kind", null, false), Assert.Single(scan.Problems));
        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public async Task A_file_another_process_holds_is_kept_reported_locked_and_listed_once_it_is_free()
    {
        var held = Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1));
        var name = Kit.Put(Root, held);
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(5), Kit.Id(100)));
        var path = Path.Combine(Root, name);

        CheckHistoryScan scan;
        IReadOnlyList<string> before;
        IReadOnlyList<string> after;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            before = Kit.Snapshot(Root);
            scan = Store().Scan(Kit.All, CancellationToken.None);
            after = Kit.Snapshot(Root);
        }

        Assert.Equal([Kit.Id(100)], Kit.RunIds(scan.Page));
        Assert.Equal(["history_locked=1"], Kit.WarningLines(scan.Page));
        Assert.Equal(new CheckHistoryProblem(name, "history_locked", "sharing_violation", null, false), Assert.Single(scan.Problems));
        Assert.Equal(before, after);

        var free = await Store().ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal([Kit.Id(1), Kit.Id(100)], Kit.RunIds(free));
        Assert.Empty(free.Warnings);
    }

    [Fact]
    public void A_locked_corrupt_file_is_not_moved_either()
    {
        var name = Kit.NameOf(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.PutText(Root, name, "{ corrupt");
        var path = Path.Combine(Root, name);

        CheckHistoryScan scan;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            scan = Store().Scan(Kit.All, CancellationToken.None);
        }

        Assert.Equal(["history_locked=1"], Kit.WarningLines(scan.Page));
        Assert.Equal([name], Kit.Names(Root));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "history_access_denied", "permission_denied")]
    [InlineData(typeof(IOException), "history_read_failed", "io_error")]
    public void A_permission_or_other_io_error_keeps_the_file_and_reports_it(Type faultType, string code, string reason)
    {
        var name = Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1)));
        Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(5), Kit.Id(100)));
        var before = Kit.Snapshot(Root);
        var files = new FaultFileSystem
        {
            ReadFault = path => Path.GetFileName(path) == name ? (Exception)Activator.CreateInstance(faultType, "refused")! : null,
        };

        var scan = Store(files).Scan(Kit.All, CancellationToken.None);

        Assert.Equal([Kit.Id(100)], Kit.RunIds(scan.Page));
        Assert.Equal([$"{code}=1"], Kit.WarningLines(scan.Page));
        Assert.Equal(new CheckHistoryProblem(name, code, reason, null, false), Assert.Single(scan.Problems));
        Assert.Equal(before, Kit.Snapshot(Root));
        Assert.Equal(0, files.Count("move"));
    }

    [Fact]
    public async Task A_file_that_vanishes_between_the_listing_and_the_read_is_not_a_problem()
    {
        var name = Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1)));
        var files = new FaultFileSystem
        {
            ReadFault = path =>
            {
                File.Delete(path);
                return new FileNotFoundException("gone", path);
            },
        };

        var page = await Store(files).ListAsync(Kit.All, CancellationToken.None);

        Assert.Empty(page.Entries);
        Assert.Empty(page.Warnings);
        Assert.DoesNotContain(name, Kit.Names(Root));
    }

    // ---- what is not a history file ----

    [Fact]
    public void Only_exact_history_file_names_in_the_top_folder_are_read_everything_else_is_left_alone()
    {
        var good = Kit.Make(CheckKind.Memory, T(10), Kit.Id(1));
        var goodName = Kit.Put(Root, good);
        var valid = Kit.Bytes(Kit.Make(CheckKind.Cpu, T(20), Kit.Id(2)));

        // Corrupt content under names that are not ours: reading any of them would show up as a warning.
        Kit.PutText(Root, "notes.txt", "not a result");
        Kit.PutText(Root, $"MEMORY-{Kit.Stamp(T(10))}-{Kit.Id(3):N}.json", "{ corrupt");
        Kit.PutText(Root, $"memory-{Kit.Stamp(T(10))}-{Kit.Id(4):N}.JSON", "{ corrupt");
        Kit.PutText(Root, $"memory-{Kit.Stamp(T(10))}-{Kit.Id(5):N}.json.bak", "{ corrupt");
        Kit.PutText(Root, $"memory-{Kit.Stamp(T(10))}-{Kit.Id(6):N}.json.pcmtmp-{Kit.Pid}-00000001.tmp", "{ corrupt");
        Kit.PutText(Root, $"memory-{Kit.Stamp(T(10))}-{Kit.Id(7):N}.json.bad-20261120T000000000Z", "{ corrupt");
        File.WriteAllBytes(Path.Combine(Root, $"cpu-{Kit.Stamp(T(20))}-{Kit.Id(2):N}.json.bad-20261120T000000000Z"), valid);
        Kit.PutText(Root, $"memory-{Kit.Stamp(T(10))}-{Kit.Id(8):N}", "{ corrupt");
        Directory.CreateDirectory(Path.Combine(Root, "baseline"));
        Kit.PutText(Path.Combine(Root, "baseline"), $"cpu-{Kit.Stamp(T(30))}-{Kit.Id(9):N}.json", "{ corrupt");
        var before = Kit.Snapshot(Root);

        var scan = Store().Scan(Kit.All, CancellationToken.None);

        Assert.Equal([good.RunId], Kit.RunIds(scan.Page));
        Assert.Empty(scan.Page.Warnings);
        Assert.Empty(scan.Problems);
        Assert.Equal(before, Kit.Snapshot(Root));
        Assert.Contains(goodName, Kit.Names(Root));
    }

    // ---- window and limit ----

    [Fact]
    public async Task The_listing_shows_the_newest_200_valid_files_of_each_kind_and_does_not_look_beyond()
    {
        // Memory 1..201 (the oldest is corrupt, beyond the window, so it is neither read nor reported nor moved), cpu 1001..1003.
        var corruptOldest = Kit.NameOf(CheckKind.Memory, T(1), Kit.Id(1));
        Kit.PutText(Root, corruptOldest, "{ corrupt");
        for (var n = 2; n <= 201; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Memory, T(n), Kit.Id(n)));
        }

        for (var n = 1001; n <= 1003; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(n), Kit.Id(n)));
        }

        // 200 valid memory files exist (2..201): the corrupt one is the 201st and is outside the window.
        var all = await Store().ListAsync(Kit.All, CancellationToken.None);

        Assert.Equal(203, all.Entries.Count);
        Assert.Equal(
            Enumerable.Range(1001, 3).Reverse().Concat(Enumerable.Range(2, 200).Reverse()).Select(n => Kit.Id(n)).ToArray(),
            Kit.RunIds(all));
        Assert.Empty(all.Warnings);
        Assert.True(File.Exists(Path.Combine(Root, corruptOldest)));

        var memory = await Store().ListAsync(new CheckHistoryQuery(CheckKind.Memory, null), CancellationToken.None);
        Assert.Equal(200, memory.Entries.Count);
    }

    [Fact]
    public async Task A_limit_also_stops_the_visit_so_older_problems_are_not_found_yet()
    {
        for (var n = 2; n <= 6; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(n), Kit.Id(n)));
        }

        var corruptName = Kit.NameOf(CheckKind.Cpu, T(1), Kit.Id(1));
        Kit.PutText(Root, corruptName, "{ corrupt");

        var limited = await Store().ListAsync(new CheckHistoryQuery(null, 3), CancellationToken.None);
        Assert.Equal(3, limited.Entries.Count);
        Assert.Empty(limited.Warnings);
        Assert.True(File.Exists(Path.Combine(Root, corruptName)));

        var everything = await Store().ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal(5, everything.Entries.Count);
        Assert.Equal(["history_unreadable=1", "history_quarantined=1"], Kit.WarningLines(everything));
        Assert.False(File.Exists(Path.Combine(Root, corruptName)));
    }

    // ---- reading one run ----

    [Fact]
    public async Task One_run_is_found_by_its_id_a_corrupt_one_is_moved_aside_and_a_newer_format_one_is_kept()
    {
        var good = Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1));
        Kit.Put(Root, good);
        var corrupt = Kit.NameOf(CheckKind.Cpu, T(20), Kit.Id(2));
        Kit.PutText(Root, corrupt, "{ corrupt");
        var futureResult = Kit.Make(CheckKind.Cpu, T(30), Kit.Id(3));
        var futureName = Kit.NameOf(futureResult);
        Kit.PutText(Root, futureName, Kit.Edited(futureResult, "\"format\": \"pcm-check/1\"", "\"format\": \"pcm-check/9\""));
        var store = Store();

        var found = await store.GetAsync(Kit.Id(1), CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Found, found.Status);
        Assert.Equal(CheckJson.Serialize(good, indented: true), CheckJson.Serialize(found.Result!, indented: true));
        Assert.Null(found.WarningCode);

        var missing = await store.GetAsync(Kit.Id(404), CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.NotFound, missing.Status);
        Assert.Null(missing.Result);
        Assert.Null(missing.WarningCode);

        var detailFuture = store.GetDetail(Kit.Id(3), CancellationToken.None);
        Assert.Null(detailFuture.Result);
        Assert.Equal("history_future_format", detailFuture.Problem?.Code);
        Assert.True(File.Exists(Path.Combine(Root, futureName)));
        var future = await store.GetAsync(Kit.Id(3), CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Unreadable, future.Status);
        Assert.Equal("history_future_format", future.WarningCode);

        // A corrupt file is moved aside by the store, and says so: Unreadable with history_quarantined (never "not found").
        var set = await store.GetAsync(Kit.Id(2), CancellationToken.None);
        Assert.Equal(CheckHistoryGetStatus.Unreadable, set.Status);
        Assert.Equal("history_quarantined", set.WarningCode);
        Assert.False(File.Exists(Path.Combine(Root, corrupt)));
        Assert.True(File.Exists(Path.Combine(Root, $"{corrupt}.bad-{Kit.NowStamp}")));
    }

    [Fact]
    public async Task A_corrupt_file_the_store_cannot_move_aside_is_unreadable_with_history_quarantine_failed()
    {
        var corrupt = Kit.PutText(Root, Kit.NameOf(CheckKind.Cpu, T(20), Kit.Id(2)), "{ corrupt");
        var files = new FaultFileSystem { MoveFault = (_, _) => new UnauthorizedAccessException("denied") };

        var result = await Store(files).GetAsync(Kit.Id(2), CancellationToken.None);

        Assert.Equal(CheckHistoryGetStatus.Unreadable, result.Status);
        Assert.Equal("history_quarantine_failed", result.WarningCode);
        Assert.True(File.Exists(Path.Combine(Root, corrupt)));
    }

    [Theory]
    [InlineData("access", "history_access_denied")]
    [InlineData("sharing", "history_locked")]
    [InlineData("other", "history_read_failed")]
    public async Task A_run_whose_file_cannot_be_read_is_unreadable_with_the_code_not_not_found_in_both_histories(string fault, string code)
    {
        var name = Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(10), Kit.Id(1)));
        var files = new FaultFileSystem
        {
            ReadFault = path => Path.GetFileName(path) != name
                ? null
                : fault switch
                {
                    "access" => (Exception)new UnauthorizedAccessException("denied"),
                    "sharing" => Kit.SharingViolation(),
                    _ => new IOException("device error"),
                },
        };
        var before = Kit.Snapshot(Root);

        var viaStore = await Store(files).GetAsync(Kit.Id(1), CancellationToken.None);
        var viaReadOnly = await Kit.ReadOnly(Root, files).GetAsync(Kit.Id(1), CancellationToken.None);

        foreach (var result in new[] { viaStore, viaReadOnly })
        {
            Assert.Equal(CheckHistoryGetStatus.Unreadable, result.Status);
            Assert.Equal(code, result.WarningCode);
            Assert.Null(result.Result);
        }

        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public void The_get_result_is_built_only_through_its_three_shapes()
    {
        Assert.Equal(CheckHistoryGetStatus.NotFound, CheckHistoryGetResult.NotFound.Status);

        var found = CheckHistoryGetResult.Found(Samples.MemoryCheck());
        Assert.Equal(CheckHistoryGetStatus.Found, found.Status);
        Assert.NotNull(found.Result);

        foreach (var code in CheckHistoryWarningCodes.All)
        {
            var unreadable = CheckHistoryGetResult.Unreadable(code);
            Assert.Equal(CheckHistoryGetStatus.Unreadable, unreadable.Status);
            Assert.Equal(code, unreadable.WarningCode);
            Assert.Null(unreadable.Result);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CheckHistoryGetResult.Unreadable("history_something_else"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckHistoryGetResult.Unreadable(null!));
        Assert.Throws<ArgumentNullException>(() => CheckHistoryGetResult.Found(null!));
        Assert.Equal(7, CheckHistoryWarningCodes.All.Count);
    }
}
