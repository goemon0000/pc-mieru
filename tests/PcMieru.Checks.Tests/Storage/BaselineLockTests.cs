using PcMieru.Checks.Storage;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The named mutex of the baseline (DESIGN-A §14.5) and the public engine on the real file system and the real mutex. Every test uses a
/// root of its own (a fresh GUID folder name), so the mutexes of two tests never meet. The folders are never the user's.
/// </summary>
public class BaselineLockTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "pcmieru-checks-tests", "lock-" + Guid.NewGuid().ToString("N"));

    /// <summary>Runs <paramref name="work"/> on a thread of its own: a Windows mutex belongs to the thread that took it.</summary>
    private static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The other thread failed.", failure);
        }

        return result;
    }

    // ---- the name ----

    [Fact]
    public void The_name_is_the_session_prefix_and_32_hex_digits()
    {
        var name = NamedMutexBaselineLock.MutexName("S-1-5-21-1", NewRoot());

        Assert.StartsWith("Local\\PCMieru.Baseline.", name, StringComparison.Ordinal);
        var hash = name["Local\\PCMieru.Baseline.".Length..];
        Assert.Equal(32, hash.Length);
        Assert.All(hash, static c => Assert.True(char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)));
    }

    [Fact]
    public void The_same_user_and_folder_give_the_same_name_whatever_the_case_or_a_trailing_separator()
    {
        var root = NewRoot();
        var name = NamedMutexBaselineLock.MutexName("S-1-5-21-1", root);

        Assert.Equal(name, NamedMutexBaselineLock.MutexName("S-1-5-21-1", root));
        Assert.Equal(name, NamedMutexBaselineLock.MutexName("S-1-5-21-1", root.ToUpperInvariant()));
        Assert.Equal(name, NamedMutexBaselineLock.MutexName("S-1-5-21-1", root + Path.DirectorySeparatorChar));
        Assert.Equal(name, NamedMutexBaselineLock.MutexName("S-1-5-21-1", root + Path.AltDirectorySeparatorChar));
        Assert.Equal(name, NamedMutexBaselineLock.MutexName("S-1-5-21-1", Path.Combine(root, "x", "..")));
    }

    [Fact]
    public void Another_folder_or_another_user_gives_another_name()
    {
        var root = NewRoot();
        var name = NamedMutexBaselineLock.MutexName("S-1-5-21-1", root);

        Assert.NotEqual(name, NamedMutexBaselineLock.MutexName("S-1-5-21-1", root + "-2"));
        Assert.NotEqual(name, NamedMutexBaselineLock.MutexName("S-1-5-21-2", root));
    }

    [Fact]
    public void The_current_user_id_is_never_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(NamedMutexBaselineLock.CurrentUserId()));
    }

    // ---- taking it ----

    [Fact]
    public void A_free_lock_is_taken_and_a_second_taker_waits_until_it_is_released()
    {
        var root = NewRoot();
        var mine = new NamedMutexBaselineLock("test-user", root);
        var other = new NamedMutexBaselineLock("test-user", root);

        var held = mine.TryAcquire(TimeSpan.FromSeconds(5));
        Assert.NotNull(held);

        var whileHeld = OnOtherThread(() => other.TryAcquire(TimeSpan.FromMilliseconds(100)));
        Assert.Null(whileHeld);

        held.Dispose();
        var afterRelease = OnOtherThread(() =>
        {
            using var taken = other.TryAcquire(TimeSpan.FromSeconds(5));
            return taken is not null;
        });
        Assert.True(afterRelease);
    }

    [Fact]
    public void Another_folder_is_not_held_by_the_first()
    {
        var first = new NamedMutexBaselineLock("test-user", NewRoot());
        var second = new NamedMutexBaselineLock("test-user", NewRoot());

        using var held = first.TryAcquire(TimeSpan.FromSeconds(5));
        Assert.NotNull(held);

        var taken = OnOtherThread(() =>
        {
            using var other = second.TryAcquire(TimeSpan.FromMilliseconds(500));
            return other is not null;
        });
        Assert.True(taken);
    }

    [Fact]
    public void Releasing_twice_is_harmless_and_frees_the_lock_once()
    {
        var root = NewRoot();
        var mutex = new NamedMutexBaselineLock("test-user", root);
        var held = mutex.TryAcquire(TimeSpan.FromSeconds(5));
        Assert.NotNull(held);

        held.Dispose();
        held.Dispose();

        var again = OnOtherThread(() =>
        {
            using var taken = mutex.TryAcquire(TimeSpan.FromSeconds(5));
            return taken is not null;
        });
        Assert.True(again);
    }

    [Fact]
    public void A_lock_released_from_another_thread_than_the_one_that_took_it_does_not_throw()
    {
        var mutex = new NamedMutexBaselineLock("test-user", NewRoot());
        var held = OnOtherThread(() => mutex.TryAcquire(TimeSpan.FromSeconds(5)));
        Assert.NotNull(held);

        var failure = Record.Exception(held.Dispose);

        Assert.Null(failure);
    }

    [Fact]
    public void A_lock_its_owner_died_holding_is_taken_over_not_refused()
    {
        var mutex = new NamedMutexBaselineLock("test-user", NewRoot());
        // The owner's thread ends without releasing it: the handle stays open in the object we keep, so the mutex is abandoned, not gone.
        var abandoned = OnOtherThread(() => mutex.TryAcquire(TimeSpan.FromSeconds(5)));
        Assert.NotNull(abandoned);

        using var taken = mutex.TryAcquire(TimeSpan.FromSeconds(5));

        Assert.NotNull(taken);
        GC.KeepAlive(abandoned);
    }

    // ---- the public engine on the real file system ----

    [Fact]
    public void The_public_engine_records_and_compares_on_a_real_folder_with_the_real_mutex()
    {
        var root = NewRoot();
        try
        {
            var engine = new BaselineEngine(root);
            var now = DateTimeOffset.UtcNow;

            var first = engine.Complete(engine.BeginRun(), Drives.Inspect(now, [Drives.Nvme(at: now)]));
            var second = engine.Complete(engine.BeginRun(), Drives.Inspect(now.AddSeconds(1), [Drives.Nvme(at: now.AddSeconds(1), media: 2)]));

            var firstDisk = Assert.Single(first.Disks);
            Assert.Equal(BaselineDiskState.NotComparable, firstDisk.State);
            Assert.Equal(CheckEvidenceCode.BaselineFirst, firstDisk.Code);

            var secondDisk = Assert.Single(second.Disks);
            Assert.Equal(BaselineDiskState.Compared, secondDisk.State);
            Assert.Equal("disk-1", secondDisk.DiskId);
            var media = BaselineAssert.Counter(secondDisk, "nvme.media_errors");
            Assert.True(media.Grew);
            Assert.Equal(0UL, media.Before);
            Assert.Equal(2UL, media.After);
            Assert.False(first.ReadFailed);
            Assert.False(second.ReadFailed);

            Assert.True(Directory.Exists(Path.Combine(root, "baseline")));
            Assert.True(File.Exists(Path.Combine(root, "baseline", "acq-seq.json")));
            Assert.True(File.Exists(Path.Combine(root, "baseline", "disk-ordinals.json")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Two_public_engines_on_one_folder_hand_out_increasing_numbers_and_the_older_run_steps_aside()
    {
        var root = NewRoot();
        try
        {
            var older = new BaselineEngine(root);
            var newer = new BaselineEngine(root);
            var now = DateTimeOffset.UtcNow;

            var olderTicket = older.BeginRun();
            var newerTicket = newer.BeginRun();
            Assert.True(newerTicket.Acq > olderTicket.Acq);

            var newerResult = newer.Complete(newerTicket, Drives.Inspect(now, [Drives.Nvme(at: now)]));
            var olderResult = older.Complete(olderTicket, Drives.Inspect(now, [Drives.Nvme(at: now, media: 9)]));

            Assert.Equal(CheckEvidenceCode.BaselineFirst, Assert.Single(newerResult.Disks).Code);
            var steppedAside = Assert.Single(olderResult.Disks);
            Assert.Equal(BaselineDiskState.NotComparable, steppedAside.State);
            Assert.Equal(CheckEvidenceCode.BaselineConcurrent, steppedAside.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void A_folder_that_cannot_be_created_comes_back_as_a_reason_not_an_exception()
    {
        var parent = NewRoot();
        Directory.CreateDirectory(parent);
        try
        {
            var blocker = Path.Combine(parent, "blocker");
            File.WriteAllText(blocker, "a file where the folder should be");
            var engine = new BaselineEngine(Path.Combine(blocker, "root"));
            var now = DateTimeOffset.UtcNow;

            var result = engine.Complete(engine.BeginRun(), Drives.Inspect(now, [Drives.Nvme(at: now)]));

            Assert.True(result.ReadFailed);
            var disk = Assert.Single(result.Disks);
            Assert.Equal(BaselineDiskState.NotComparable, disk.State);
            Assert.Equal(CheckEvidenceCode.BaselineReadFailed, disk.Code);
        }
        finally
        {
            TryDelete(parent);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the temp folder; the next run uses another name.
        }
    }
}
