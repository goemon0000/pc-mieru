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
    public void Only_our_own_old_tmp_files_are_cleaned()
    {
        var now = Kit.Now;
        var finalName = Memory(1);
        var oneHour = TimeSpan.FromHours(1);

        // Ours (exact name) and old enough: exactly one hour, and three days.
        var exactlyOld = Kit.TempNameOf(finalName, 1);
        var longOld = Kit.TempNameOf(Memory(2), 2);
        PutTemp(exactlyOld, now - oneHour);
        PutTemp(longOld, now - TimeSpan.FromDays(3));

        // Ours but too young: one second short of the hour; and dated in the future.
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

        // Ours and old, but held open by a writer: the delete fails and the file stays.
        var held = Kit.TempNameOf(Memory(5), 5);
        var heldPath = PutTemp(held, longAgo);

        int deleted;
        using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            deleted = Kit.Store(Root).CleanStaleTemps();
        }

        Assert.Equal(2, deleted);
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
    public async Task When_the_failed_saves_own_tmp_cannot_be_deleted_it_is_left_for_the_age_rule()
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

        // A later save, once the leftover is old, removes it.
        PutTemp(leftover, Kit.Now - TimeSpan.FromHours(2));
        var next = Kit.Make(CheckKind.Cpu, T(2), Kit.Id(2));
        Assert.Equal(CheckSaveStatus.Saved, (await Kit.Store(Root).SaveAsync(next)).Status);
        Assert.Equal([Kit.NameOf(next)], Kit.Names(Root));
    }
}
