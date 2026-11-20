namespace PcMieru.Checks.Tests;

/// <summary>DESIGN-A §4: the newest 200 valid files per kind are kept; what retention may and may not delete; the .tmp clean-up rule.</summary>
public sealed class HistoryRetentionTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private string Root => Path.Combine(_tmp.Path, "checks");

    public void Dispose() => _tmp.Dispose();

    private static DateTimeOffset T(int seconds) => Samples.Start.AddSeconds(seconds);

    private static string Memory(int n) => Kit.NameOf(CheckKind.Memory, T(n), Kit.Id(n));

    private void PutMemory(int from, int to)
    {
        for (var n = from; n <= to; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Memory, T(n), Kit.Id(n)));
        }
    }

    private static string[] Sorted(IEnumerable<string> names) => [.. names.OrderBy(n => n, StringComparer.Ordinal)];

    // ---- 199 / 200 / 201 ----

    [Theory]
    [InlineData(199, 0)]
    [InlineData(200, 0)]
    [InlineData(201, 1)]
    public void Retention_keeps_the_newest_200_files_of_a_kind(int count, int deleted)
    {
        PutMemory(1, count);
        foreach (var n in new[] { 1001, 1002, 1003 })
        {
            Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(-n), Kit.Id(n)));
        }

        var result = Kit.Store(Root).ApplyRetention(CheckKind.Memory);

        Assert.Equal(new CheckRetentionResult(deleted, 0), result);
        var keptFrom = count - Math.Min(count, 200) + 1;
        var expected = Enumerable.Range(keptFrom, count - keptFrom + 1).Select(Memory)
            .Concat(new[] { 1001, 1002, 1003 }.Select(n => Kit.NameOf(CheckKind.Cpu, T(-n), Kit.Id(n))));
        Assert.Equal(Sorted(expected), Kit.Names(Root));
    }

    [Theory]
    [InlineData(198, 199, 0)]
    [InlineData(199, 200, 0)]
    [InlineData(200, 200, 1)]
    public async Task A_save_that_makes_the_201st_file_deletes_the_oldest_and_never_the_new_file(int existing, int remaining, int deleted)
    {
        PutMemory(1, existing);
        var files = new FaultFileSystem();
        var store = Kit.Store(Root, files: files);
        var newest = Kit.Make(CheckKind.Memory, T(1000), Kit.Id(1000));

        var state = await store.SaveAsync(newest);

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal(Kit.NameOf(newest), state.FileName);
        var keptFrom = existing + 1 - (remaining - 1);
        var expected = Enumerable.Range(keptFrom, existing - keptFrom + 1).Select(Memory).Append(Kit.NameOf(newest));
        Assert.Equal(Sorted(expected), Kit.Names(Root));
        Assert.Equal(remaining, Kit.Names(Root).Length);
        Assert.Equal(deleted, files.Count("delete"));

        var page = await store.ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal(remaining, page.Entries.Count);
        Assert.Equal(Kit.Id(1000), page.Entries[0].RunId);
    }

    [Fact]
    public void A_file_tied_on_end_time_at_the_boundary_is_ranked_by_uuid()
    {
        // 199 newer files, then two with the same end time: ranks 200 and 201 are decided by UUID, ascending (as in the listing).
        for (var i = 1; i <= 199; i++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Memory, T(100 + i), Kit.Id(100 + i)));
        }

        var kept = Kit.Make(CheckKind.Memory, T(5), Kit.Id(1));
        var dropped = Kit.Make(CheckKind.Memory, T(5), Kit.Id(2));
        Kit.Put(Root, dropped);
        Kit.Put(Root, kept);

        var result = Kit.Store(Root).ApplyRetention(CheckKind.Memory);

        Assert.Equal(new CheckRetentionResult(1, 0), result);
        Assert.True(File.Exists(Path.Combine(Root, Kit.NameOf(kept))));
        Assert.False(File.Exists(Path.Combine(Root, Kit.NameOf(dropped))));
    }

    [Fact]
    public void Retention_of_one_kind_never_touches_another_kind()
    {
        PutMemory(1, 201);
        for (var n = 1; n <= 201; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Cpu, T(n), Kit.Id(5000 + n)));
        }

        var cpuBefore = Kit.Names(Root).Where(n => n.StartsWith("cpu-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(201, cpuBefore.Length);

        Assert.Equal(new CheckRetentionResult(1, 0), Kit.Store(Root).ApplyRetention(CheckKind.Memory));

        Assert.Equal(cpuBefore, Kit.Names(Root).Where(n => n.StartsWith("cpu-", StringComparison.Ordinal)).ToArray());
        Assert.False(File.Exists(Path.Combine(Root, Memory(1))));
    }

    [Fact]
    public void With_200_files_or_fewer_of_the_kind_nothing_is_read()
    {
        PutMemory(1, 200);
        for (var n = 1; n <= 40; n++)
        {
            Kit.PutText(Root, $"{Memory(n)}.bad-20261120T000000000Z", "{ corrupt");
            Kit.PutText(Root, $"{Memory(n)}.pcmtmp-{Kit.Pid}-00000001.tmp", "{ corrupt");
        }

        var files = new FaultFileSystem();

        var result = Kit.Store(Root, files: files).ApplyRetention(CheckKind.Memory);

        Assert.Equal(CheckRetentionResult.None, result);
        Assert.Equal(0, files.Count("read"));
        Assert.Equal(0, files.Count("delete"));
    }

    // ---- the file just saved is kept, and is one of the 200 (DESIGN-A §4.1 保持) ----

    [Fact]
    public async Task A_run_that_ended_before_200_newer_files_is_saved_and_kept_and_the_oldest_of_the_newer_ones_goes()
    {
        // 200 valid files, all newer than the run that is saved now (a run that ended long ago but is saved late).
        PutMemory(101, 300);
        var old = Kit.Make(CheckKind.Memory, T(-500), Kit.Id(9));
        var files = new FaultFileSystem();
        var store = Kit.Store(Root, files: files);

        var state = await store.SaveAsync(old);

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal(Kit.NameOf(old), state.FileName);
        Assert.True(File.Exists(Path.Combine(Root, Kit.NameOf(old))));

        // 200 valid files remain: the saved one and the 199 newest of the others. The oldest of the others paid for it.
        Assert.Equal(200, Kit.Names(Root).Length);
        Assert.Equal(Sorted(Enumerable.Range(102, 199).Select(Memory).Append(Kit.NameOf(old))), Kit.Names(Root));
        Assert.Equal(1, files.Count("delete"));
        Assert.Equal(1, files.Count($"delete {Memory(101)}"));
        Assert.Equal(0, files.Count($"read {Kit.NameOf(old)}")); // the file just saved is not even read by the retention

        // The listing shows it, as the oldest of 200.
        var page = await store.ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal(200, page.Entries.Count);
        Assert.Equal(Kit.Id(9), page.Entries[^1].RunId);
    }

    [Fact]
    public void A_kept_file_takes_one_of_the_200_places_whatever_its_age_and_is_never_deleted()
    {
        PutMemory(101, 300);
        var keep = Kit.Put(Root, Kit.Make(CheckKind.Memory, T(-500), Kit.Id(9)));

        var result = Kit.Store(Root).ApplyRetention(CheckKind.Memory, keep);

        Assert.Equal(new CheckRetentionResult(1, 0), result);
        Assert.Contains(keep, Kit.Names(Root));
        Assert.DoesNotContain(Memory(101), Kit.Names(Root));
        Assert.Equal(200, Kit.Names(Root).Length);

        // Without the keep name it is an ordinary old file, one of 201: the oldest, and it goes.
        var plain = Kit.Store(Root).ApplyRetention(CheckKind.Memory);
        Assert.Equal(CheckRetentionResult.None, plain);

        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(400), Kit.Id(400)));
        Assert.Equal(new CheckRetentionResult(1, 0), Kit.Store(Root).ApplyRetention(CheckKind.Memory));
        Assert.DoesNotContain(keep, Kit.Names(Root));
    }

    [Fact]
    public void A_kept_name_that_is_not_in_the_folder_reserves_no_place()
    {
        PutMemory(1, 201);

        var result = Kit.Store(Root).ApplyRetention(CheckKind.Memory, Kit.NameOf(CheckKind.Memory, T(999), Kit.Id(999)));

        Assert.Equal(new CheckRetentionResult(1, 0), result);
        Assert.Equal(200, Kit.Names(Root).Length);
        Assert.DoesNotContain(Memory(1), Kit.Names(Root));
    }

    [Fact]
    public void With_200_files_or_fewer_the_kept_file_changes_nothing()
    {
        PutMemory(1, 199);
        var keep = Kit.Put(Root, Kit.Make(CheckKind.Memory, T(-500), Kit.Id(9)));
        var files = new FaultFileSystem();

        Assert.Equal(CheckRetentionResult.None, Kit.Store(Root, files: files).ApplyRetention(CheckKind.Memory, keep));

        Assert.Equal(200, Kit.Names(Root).Length);
        Assert.Equal(0, files.Count("read"));
        Assert.Equal(0, files.Count("delete"));
    }

    [Fact]
    public void A_corrupt_older_file_is_still_never_deleted_when_a_file_is_kept()
    {
        PutMemory(101, 300);
        var keep = Kit.Put(Root, Kit.Make(CheckKind.Memory, T(-500), Kit.Id(9)));
        Kit.PutText(Root, Memory(1), "{ corrupt");

        var result = Kit.Store(Root).ApplyRetention(CheckKind.Memory, keep);

        Assert.Equal(new CheckRetentionResult(1, 0), result);
        Assert.Contains(Memory(1), Kit.Names(Root));
        Assert.Contains(keep, Kit.Names(Root));
    }

    // ---- what retention deletes and what it never touches ----

    [Fact]
    public void Retention_deletes_only_files_that_read_as_valid_and_leaves_every_other_file_alone()
    {
        // The newest 200 valid files: ids 101..300.
        for (var n = 101; n <= 300; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Memory, T(n), Kit.Id(n)));
        }

        // Older files, ids 1..10, of every kind of trouble:
        Kit.PutText(Root, Memory(1), "{ corrupt");                                                                       // corrupt
        var future = Kit.Make(CheckKind.Memory, T(2), Kit.Id(2));
        Kit.PutText(Root, Memory(2), Kit.Edited(future, "\"format\": \"pcm-check/1\"", "\"format\": \"pcm-check/2\""));      // a newer format
        var locked = Kit.Put(Root, Kit.Make(CheckKind.Memory, T(3), Kit.Id(3)));                                             // held by another process
        var mismatched = Kit.NameOf(CheckKind.Memory, T(4).AddMilliseconds(1), Kit.Id(4));
        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(4), Kit.Id(4)), mismatched);                                             // name and body disagree
        for (var n = 5; n <= 10; n++)
        {
            Kit.Put(Root, Kit.Make(CheckKind.Memory, T(n), Kit.Id(n)));                                                       // valid, older than 200: deletable
        }

        // Neighbours that are not history files at all.
        Kit.Put(Root, Kit.Make(CheckKind.Memory, T(9), Kit.Id(9)), $"{Memory(9)}.bad-20261120T000000000Z");
        Kit.PutText(Root, $"{Memory(9)}.pcmtmp-{Kit.Pid}-00000009.tmp", "leftover");
        Kit.PutText(Root, "notes.txt", "keep me");
        Directory.CreateDirectory(Path.Combine(Root, "baseline"));
        Kit.PutText(Path.Combine(Root, "baseline"), Memory(8), "baseline file");

        var deletedNames = Enumerable.Range(5, 6).Select(Memory).ToArray();
        IReadOnlyList<string> before;
        IReadOnlyList<string> after;
        CheckRetentionResult result;
        using (new FileStream(Path.Combine(Root, locked), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            before = Kit.Snapshot(Root);
            result = Kit.Store(Root).ApplyRetention(CheckKind.Memory);
            after = Kit.Snapshot(Root);
        }

        Assert.Equal(new CheckRetentionResult(6, 0), result);
        var expectedAfter = before.Where(line => !deletedNames.Any(name => line.StartsWith($"F {name} ", StringComparison.Ordinal))).ToArray();
        Assert.Equal(before.Count - 6, expectedAfter.Length);
        Assert.Equal(expectedAfter, after);
        Assert.Contains(mismatched, Kit.Names(Root));
        Assert.Contains(Memory(1), Kit.Names(Root));
        Assert.Contains(Memory(2), Kit.Names(Root));
        Assert.Contains(locked, Kit.Names(Root));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void A_file_that_cannot_be_read_is_never_deleted_even_when_it_is_the_oldest(Type faultType)
    {
        PutMemory(1, 201);
        var files = new FaultFileSystem
        {
            ReadFault = path => Path.GetFileName(path) == Memory(1) ? (Exception)Activator.CreateInstance(faultType, "refused")! : null,
        };

        var result = Kit.Store(Root, files: files).ApplyRetention(CheckKind.Memory);

        Assert.Equal(CheckRetentionResult.None, result);
        Assert.Equal(201, Kit.Names(Root).Length);
        Assert.Equal(0, files.Count("delete"));
    }

    [Fact]
    public async Task A_delete_that_fails_is_counted_and_does_not_fail_the_save_or_touch_other_files()
    {
        PutMemory(1, 200);
        var files = new FaultFileSystem { DeleteFault = _ => new IOException("in use") };
        var store = Kit.Store(Root, files: files);

        var state = await store.SaveAsync(Kit.Make(CheckKind.Memory, T(1000), Kit.Id(1000)));

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal(201, Kit.Names(Root).Length);
        Assert.Equal(1, files.Count("delete"));
        Assert.Equal(new CheckRetentionResult(0, 1), store.ApplyRetention(CheckKind.Memory));
    }

    // ---- .tmp clean-up ----

    private string PutTemp(string name, DateTimeOffset lastWrite)
    {
        Kit.PutText(Root, name, "leftover");
        var path = Path.Combine(Root, name);
        Kit.SetAge(path, lastWrite);
        return path;
    }

    [Fact]
    public void Only_abandoned_tmp_files_by_name_shape_and_age_are_cleaned_whatever_the_pid()
    {
        var now = Kit.Now;
        var finalName = Memory(1);
        var oneHour = TimeSpan.FromHours(1);

        // The exact name shape and old enough: exactly one hour, and three days. The pid in the name is not looked at.
        var exactlyOld = Kit.TempNameOf(finalName, 1);
        var longOld = Kit.TempNameOf(Memory(2), 2);
        PutTemp(exactlyOld, now - oneHour);
        PutTemp(longOld, now - TimeSpan.FromDays(3));
        var otherPid = $"{Memory(6)}.pcmtmp-1-00000006.tmp";
        PutTemp(otherPid, now - TimeSpan.FromDays(3));

        // The right shape but too young: one second short of the hour; and dated in the future.
        var young = Kit.TempNameOf(Memory(3), 3);
        var future = Kit.TempNameOf(Memory(4), 4);
        PutTemp(young, now - oneHour + TimeSpan.FromSeconds(1));
        PutTemp(future, now + TimeSpan.FromDays(1));

        // Not ours, however old.
        var longAgo = now - TimeSpan.FromDays(3);
        var foreign = new[]
        {
            "foo.tmp",
            $"{finalName}.tmp",
            $"{finalName}.pcmtmp-{Kit.Pid}-ABCDEF01.tmp",       // upper case nonce
            $"{finalName}.pcmtmp-{Kit.Pid}-1234567.tmp",        // seven digits
            $"{finalName}.pcmtmp-{Kit.Pid}-00000001.tmp.bak",
            $"{finalName}.pcmtmp-x-00000001.tmp",
            $"gpu-{Kit.Stamp(T(1))}-{Kit.Id(1):N}.json.pcmtmp-{Kit.Pid}-00000001.tmp", // a kind of a newer version
            $"{finalName}.bad-20261120T000000000Z",
            finalName,
            "notes.txt",
        };
        foreach (var name in foreign)
        {
            PutTemp(name, longAgo);
        }

        // The right shape and old, but held open by a writer: the delete fails and the file stays.
        var held = Kit.TempNameOf(Memory(5), 5);
        var heldPath = PutTemp(held, longAgo);

        CheckTempCleanResult cleaned;
        using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            cleaned = Kit.Store(Root).CleanStaleTemps();
        }

        Assert.Equal(3, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        var expected = new[] { young, future, held }.Concat(foreign);
        Assert.Equal(Sorted(expected), Kit.Names(Root));
    }

    [Fact]
    public async Task A_committed_save_cleans_the_abandoned_tmp_files_of_earlier_runs_and_leaves_young_ones()
    {
        var old = Kit.TempNameOf(Memory(7), 9);
        var young = Kit.TempNameOf(Memory(8), 9);
        PutTemp(old, Kit.Now - TimeSpan.FromHours(5));
        PutTemp(young, Kit.Now - TimeSpan.FromMinutes(5));
        var result = Kit.Make(CheckKind.Cpu, T(1), Kit.Id(1));

        var state = await Kit.Store(Root).SaveAsync(result);

        Assert.Equal(CheckSaveStatus.Saved, state.Status);
        Assert.Equal(Sorted([Kit.NameOf(result), young]), Kit.Names(Root));
    }

    [Fact]
    public async Task A_listing_never_deletes_a_tmp_file_even_an_old_one()
    {
        var old = Kit.TempNameOf(Memory(7), 9);
        PutTemp(old, Kit.Now - TimeSpan.FromDays(9));
        var before = Kit.Snapshot(Root);

        var page = await Kit.Store(Root).ListAsync(Kit.All, CancellationToken.None);

        Assert.Empty(page.Entries);
        Assert.Empty(page.Warnings);
        Assert.Equal(before, Kit.Snapshot(Root));
    }

    [Fact]
    public async Task When_the_failed_saves_own_tmp_cannot_be_deleted_it_is_left_and_a_later_clean_up_promotes_it()
    {
        var files = new FaultFileSystem
        {
            MoveFault = (_, _) => new IOException("device error"),
            DeleteFault = _ => new UnauthorizedAccessException("denied"),
        };
        var result = Kit.Make(CheckKind.Cpu, T(1), Kit.Id(1));

        var state = await Kit.Store(Root, files: files).SaveAsync(result);

        Assert.Equal("io_error", state.FailureCode);
        var leftover = Assert.Single(Kit.Names(Root));
        Assert.Equal(Kit.TempNameOf(Kit.NameOf(result), 1), leftover);

        // A later save while the leftover is young leaves it alone.
        var young = Kit.Make(CheckKind.Cpu, T(2), Kit.Id(2));
        Kit.SetAge(Path.Combine(Root, leftover), Kit.Now - TimeSpan.FromMinutes(5));
        Assert.Equal(CheckSaveStatus.Saved, (await Kit.Store(Root).SaveAsync(young)).Status);
        Assert.Equal(Sorted([leftover, Kit.NameOf(young)]), Kit.Names(Root));

        // Once it is old, the next save finds a complete, valid result of the right name and commits it (the write was complete,
        // only the rename had failed; the failed save reported it, and the file is not lost).
        Kit.SetAge(Path.Combine(Root, leftover), Kit.Now - TimeSpan.FromHours(2));
        var next = Kit.Make(CheckKind.Cpu, T(3), Kit.Id(3));
        Assert.Equal(CheckSaveStatus.Saved, (await Kit.Store(Root).SaveAsync(next)).Status);
        Assert.Equal(Sorted([Kit.NameOf(result), Kit.NameOf(young), Kit.NameOf(next)]), Kit.Names(Root));
    }

    // ---- promotion of an abandoned .tmp (DESIGN-A §4.1 .tmp の名前と放置の規則) ----

    private static readonly TimeSpan Old = TimeSpan.FromHours(5);

    /// <summary>A leftover written the way a save writes it: the strict bytes of the result, under the .tmp name of <paramref name="finalName"/>, with the age set by hand.</summary>
    private string PutLeftover(CheckResult body, string finalName, TimeSpan age, int nonce = 1)
    {
        var name = Kit.TempNameOf(finalName, nonce);
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, name), Kit.Bytes(body));
        Kit.SetAge(Path.Combine(Root, name), Kit.Now - age);
        return name;
    }

    [Fact]
    public async Task An_abandoned_complete_result_is_promoted_to_the_committed_file_with_the_same_bytes()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var finalName = Kit.NameOf(result);
        PutLeftover(result, finalName, Old);
        var files = new FaultFileSystem();

        var cleaned = Kit.Store(Root, files: files).CleanStaleTemps();

        Assert.Equal(0, cleaned.Deleted);
        Assert.Equal(1, cleaned.Promoted);
        Assert.Equal([CheckKind.Memory], cleaned.PromotedKinds);
        Assert.Equal([finalName], Kit.Names(Root));
        Assert.Equal(Kit.Bytes(result), File.ReadAllBytes(Path.Combine(Root, finalName)));
        Assert.Equal(1, files.Count($"move {Kit.TempNameOf(finalName, 1)} -> {finalName}"));
        Assert.Equal(0, files.Count("delete"));

        // It is in the history now.
        var page = await Kit.Store(Root).ListAsync(Kit.All, CancellationToken.None);
        Assert.Equal([Kit.Id(10)], Kit.RunIds(page));
    }

    [Fact]
    public void A_leftover_that_is_not_old_enough_is_neither_promoted_nor_deleted()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var name = PutLeftover(result, Kit.NameOf(result), TimeSpan.FromMinutes(59));

        var cleaned = Kit.Store(Root).CleanStaleTemps();

        Assert.Equal(0, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Equal([name], Kit.Names(Root));
    }

    public static IEnumerable<object[]> MismatchedNames()
    {
        // The body is a memory result that ended at T(1) with UUID 1; the .tmp name says something else.
        yield return new object[] { "kind", Kit.NameOf(CheckKind.Cpu, T(1), Kit.Id(1)) };
        yield return new object[] { "stamp", Kit.NameOf(CheckKind.Memory, T(1).AddMilliseconds(1), Kit.Id(1)) };
        yield return new object[] { "id", Kit.NameOf(CheckKind.Memory, T(1), Kit.Id(2)) };
    }

    [Theory]
    [MemberData(nameof(MismatchedNames))]
    public void A_leftover_whose_body_does_not_match_the_name_is_deleted_never_promoted(string what, string nameSaysFinal)
    {
        var body = Kit.Make(CheckKind.Memory, T(1), Kit.Id(1));
        PutLeftover(body, nameSaysFinal, Old);
        var files = new FaultFileSystem();

        var cleaned = Kit.Store(Root, files: files).CleanStaleTemps();

        Assert.True(what is "kind" or "stamp" or "id");
        Assert.Equal(1, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Empty(Kit.Names(Root));
        Assert.Equal(0, files.Count("move "));
    }

    public static IEnumerable<object[]> InvalidBodies()
    {
        yield return new object[] { "not json", "{ corrupt" };
        yield return new object[] { "empty", string.Empty };
        yield return new object[] { "a newer format", Kit.Edited(Kit.Make(CheckKind.Memory, T(1), Kit.Id(1)), "\"format\": \"pcm-check/1\"", "\"format\": \"pcm-check/9\"") };
        yield return new object[] { "an unknown kind", Kit.Edited(Kit.Make(CheckKind.Memory, T(1), Kit.Id(1)), "\"kind\": \"memory\"", "\"kind\": \"gpu\"") };
        yield return new object[] { "an unknown field", Kit.Edited(Kit.Make(CheckKind.Memory, T(1), Kit.Id(1)), "\"kind\": \"memory\"", "\"surprise\": 1,\n  \"kind\": \"memory\"") };
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public void A_leftover_that_does_not_read_strictly_is_deleted_never_promoted(string what, string text)
    {
        var finalName = Kit.NameOf(CheckKind.Memory, T(1), Kit.Id(1));
        var name = Kit.TempNameOf(finalName, 1);
        Kit.PutText(Root, name, text);
        Kit.SetAge(Path.Combine(Root, name), Kit.Now - Old);

        var cleaned = Kit.Store(Root).CleanStaleTemps();

        Assert.True(what.Length > 0);
        Assert.Equal(1, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Empty(Kit.Names(Root));
    }

    [Theory]
    [InlineData("another")]
    [InlineData("same")]
    public void A_leftover_whose_final_name_exists_is_deleted_and_the_existing_file_is_never_touched(string existing)
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var finalName = Kit.NameOf(result);
        if (existing == "same")
        {
            Kit.Put(Root, result);
        }
        else
        {
            Kit.PutText(Root, finalName, "SENTINEL: a file of another process");
        }

        var finalPath = Path.Combine(Root, finalName);
        Kit.SetAge(finalPath, Kit.Now - TimeSpan.FromMinutes(1));
        PutLeftover(result, finalName, Old);
        var before = File.ReadAllBytes(finalPath);
        var beforeTime = File.GetLastWriteTimeUtc(finalPath);
        var files = new FaultFileSystem();

        var cleaned = Kit.Store(Root, files: files).CleanStaleTemps();

        Assert.Equal(1, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Equal([finalName], Kit.Names(Root));
        Assert.Equal(before, File.ReadAllBytes(finalPath));
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(finalPath));
        Assert.Equal(0, files.Count("move "));
    }

    [Fact]
    public void A_final_name_taken_by_another_process_during_the_promotion_is_never_overwritten()
    {
        // The final name is free when looked at, and is taken just before the rename: the rename itself refuses to replace.
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var finalName = Kit.NameOf(result);
        PutLeftover(result, finalName, Old);
        var files = new FaultFileSystem
        {
            MoveFault = (_, destination) =>
            {
                File.WriteAllText(destination, "SENTINEL: a file of another process");
                return null;
            },
        };

        var cleaned = Kit.Store(Root, files: files).CleanStaleTemps();

        Assert.Equal(1, files.Count("move "));
        Assert.Equal(0, cleaned.Promoted);
        Assert.Equal(1, cleaned.Deleted);
        Assert.Equal([finalName], Kit.Names(Root));
        Assert.Equal("SENTINEL: a file of another process", File.ReadAllText(Path.Combine(Root, finalName)));
    }

    [Fact]
    public void A_promotion_whose_rename_fails_for_another_reason_leaves_the_leftover_for_the_next_clean_up()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var name = PutLeftover(result, Kit.NameOf(result), Old);
        var files = new FaultFileSystem { MoveFault = (_, _) => new IOException("device error") };

        var cleaned = Kit.Store(Root, files: files).CleanStaleTemps();

        Assert.Equal(0, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Equal(0, files.Count("delete"));
        Assert.Equal([name], Kit.Names(Root));

        // Next time the rename works.
        Assert.Equal(1, Kit.Store(Root).CleanStaleTemps().Promoted);
        Assert.Equal([Kit.NameOf(result)], Kit.Names(Root));
    }

    [Fact]
    public void A_promotion_retries_a_rename_that_meets_a_sharing_violation_like_a_save_does()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        PutLeftover(result, Kit.NameOf(result), Old);
        var calls = 0;
        var files = new FaultFileSystem { MoveFault = (_, _) => ++calls <= 2 ? Kit.SharingViolation() : null };
        var delays = new List<TimeSpan>();

        var cleaned = Kit.Store(Root, files: files, sleep: delays.Add).CleanStaleTemps();

        Assert.Equal(1, cleaned.Promoted);
        Assert.Equal(3, files.Count("move "));
        Assert.Equal([TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50)], delays.ToArray());
        Assert.Equal([Kit.NameOf(result)], Kit.Names(Root));
    }

    [Fact]
    public void A_leftover_that_vanishes_before_it_is_read_is_left_alone_and_counted_as_nothing()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var name = PutLeftover(result, Kit.NameOf(result), Old);
        var files = new FaultFileSystem
        {
            ReadFault = path =>
            {
                File.Delete(path);
                return new FileNotFoundException("gone");
            },
        };

        var cleaned = Kit.Store(Root, files: files).CleanStaleTemps();

        Assert.Equal(0, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Equal(0, files.Count("delete"));
        Assert.DoesNotContain(name, Kit.Names(Root));
    }

    [Fact]
    public void A_leftover_that_is_held_open_is_neither_promoted_nor_deleted()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        var name = PutLeftover(result, Kit.NameOf(result), Old);

        CheckTempCleanResult cleaned;
        using (new FileStream(Path.Combine(Root, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            cleaned = Kit.Store(Root).CleanStaleTemps();
        }

        Assert.Equal(0, cleaned.Deleted);
        Assert.Equal(0, cleaned.Promoted);
        Assert.Equal([name], Kit.Names(Root));
    }

    [Fact]
    public async Task A_save_promotes_a_leftover_of_another_kind_and_the_retention_of_that_kind_runs()
    {
        PutMemory(1, 200);
        var newest = Kit.Make(CheckKind.Memory, T(500), Kit.Id(500));
        PutLeftover(newest, Kit.NameOf(newest), Old);
        var cpu = Kit.Make(CheckKind.Cpu, T(1), Kit.Id(1));

        var state = await Kit.Store(Root).SaveAsync(cpu);

        Assert.Equal(CheckSaveStatus.Saved, state.Status);

        // The promoted file is the 201st memory file, so the oldest memory file went; the saved cpu file is there.
        var expected = Enumerable.Range(2, 199).Select(Memory).Append(Kit.NameOf(newest)).Append(Kit.NameOf(cpu));
        Assert.Equal(Sorted(expected), Kit.Names(Root));
    }

    [Fact]
    public async Task A_listing_never_promotes_a_leftover()
    {
        var result = Kit.Make(CheckKind.Memory, T(10), Kit.Id(10));
        PutLeftover(result, Kit.NameOf(result), Old);
        var before = Kit.Snapshot(Root);

        var page = await Kit.Store(Root).ListAsync(Kit.All, CancellationToken.None);

        Assert.Empty(page.Entries);
        Assert.Equal(before, Kit.Snapshot(Root));
    }
}
