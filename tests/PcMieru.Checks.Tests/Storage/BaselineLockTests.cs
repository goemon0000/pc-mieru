using System.Diagnostics;
using System.Runtime.CompilerServices;
using PcMieru.Checks.Storage;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The lock file of the baseline (DESIGN-A §4, §14.5) and the public engine on the real file system and the real lock. Every test uses a
/// folder of its own (a fresh GUID folder name), so the locks of two tests never meet. The folders are never the user's.
/// </summary>
public class BaselineLockTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "pcmieru-checks-tests", "lock-" + Guid.NewGuid().ToString("N"));

    private static string BaselineOf(string root) => Path.Combine(root, "baseline");

    /// <summary>Runs <paramref name="work"/> on a thread of its own: the lock must not belong to the thread that took it.</summary>
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

    // ---- the file ----

    [Fact]
    public void The_lock_file_name_is_neither_a_history_nor_a_temporary_file_nor_one_of_the_engines_own_files()
    {
        var name = FileBaselineLock.FileName;

        Assert.False(BaselineNames.TryParseFileName(name, out _));
        Assert.False(name.StartsWith("disk-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal));
        Assert.False(name.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.False(BaselineNames.IsOwnTemp(name));
        Assert.DoesNotContain(".bad-", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Taking_the_lock_makes_the_folder_and_the_lock_file_in_it()
    {
        var root = NewRoot();
        try
        {
            using var held = new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.FromSeconds(5));

            Assert.NotNull(held);
            Assert.True(File.Exists(Path.Combine(BaselineOf(root), FileBaselineLock.FileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void The_lock_file_stays_after_the_lock_is_released_and_is_empty()
    {
        var root = NewRoot();
        try
        {
            var file = Path.Combine(BaselineOf(root), FileBaselineLock.FileName);
            var held = new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            held.Dispose();

            Assert.True(File.Exists(file));
            Assert.Equal(0, new FileInfo(file).Length);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- taking it ----

    [Fact]
    public void A_free_lock_is_taken_and_a_second_taker_gets_nothing_until_it_is_released()
    {
        var root = NewRoot();
        try
        {
            var mine = new FileBaselineLock(BaselineOf(root));
            var other = new FileBaselineLock(BaselineOf(root));

            var held = mine.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            Assert.Null(other.TryAcquire(TimeSpan.FromMilliseconds(100)));
            Assert.Null(mine.TryAcquire(TimeSpan.Zero)); // the same object does not take it twice either: no re-entry

            held.Dispose();

            using var afterRelease = other.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(afterRelease);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Another_spelling_of_the_same_folder_is_the_same_lock()
    {
        var root = NewRoot();
        try
        {
            var baseline = BaselineOf(root);
            string[] spellings =
            [
                baseline + Path.DirectorySeparatorChar,
                baseline + Path.AltDirectorySeparatorChar,
                baseline.ToUpperInvariant(),
                baseline.ToLowerInvariant(),
                Path.Combine(baseline, "x", ".."),
                Path.Combine(root, "elsewhere", "..", "baseline"),
            ];

            using var held = new FileBaselineLock(baseline).TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            foreach (var spelling in spellings)
            {
                var other = new FileBaselineLock(spelling).TryAcquire(TimeSpan.FromMilliseconds(50));
                Assert.True(other is null, $"'{spelling}' must be the same lock as '{baseline}'");
                other?.Dispose();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Two_instances_with_different_spellings_contend_for_one_lock_both_ways_round()
    {
        var root = NewRoot();
        try
        {
            var plain = new FileBaselineLock(BaselineOf(root));
            var spelled = new FileBaselineLock(BaselineOf(root).ToUpperInvariant() + Path.DirectorySeparatorChar);

            var first = spelled.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(first);
            Assert.Null(plain.TryAcquire(TimeSpan.FromMilliseconds(50)));
            first.Dispose();

            var second = plain.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(second);
            Assert.Null(spelled.TryAcquire(TimeSpan.FromMilliseconds(50)));
            second.Dispose();

            using var third = spelled.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(third);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Another_folder_is_not_held_by_the_first()
    {
        var first = NewRoot();
        var second = NewRoot();
        try
        {
            using var held = new FileBaselineLock(BaselineOf(first)).TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            using var other = new FileBaselineLock(BaselineOf(second)).TryAcquire(TimeSpan.FromMilliseconds(500));
            Assert.NotNull(other);
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void Releasing_twice_is_harmless_and_frees_the_lock_once()
    {
        var root = NewRoot();
        try
        {
            var lockFile = new FileBaselineLock(BaselineOf(root));
            var held = lockFile.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            held.Dispose();
            held.Dispose();

            using var again = lockFile.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(again);
            held.Dispose(); // a late third release of the old one must not free the new holder's lock
            Assert.Null(new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void The_lock_does_not_belong_to_a_thread_and_can_be_taken_and_released_on_different_ones()
    {
        var root = NewRoot();
        try
        {
            var lockFile = new FileBaselineLock(BaselineOf(root));
            var held = OnOtherThread(() => lockFile.TryAcquire(TimeSpan.FromSeconds(5)));
            Assert.NotNull(held);
            Assert.Null(lockFile.TryAcquire(TimeSpan.FromMilliseconds(50)));

            var failure = Record.Exception(held.Dispose);

            Assert.Null(failure);
            using var taken = lockFile.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(taken);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- waiting for it ----

    [Fact]
    public async Task A_waiter_retries_and_gets_the_lock_when_the_holder_lets_go_before_the_deadline()
    {
        var root = NewRoot();
        try
        {
            var holder = new FileBaselineLock(BaselineOf(root));
            using var tried = new ManualResetEventSlim();
            var waiter = new FileBaselineLock(BaselineOf(root), sleep: _ =>
            {
                tried.Set();
                Thread.Sleep(5);
            });
            var held = holder.TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            var waiting = Task.Run(() => waiter.TryAcquire(TimeSpan.FromSeconds(30)));
            Assert.True(tried.Wait(TimeSpan.FromSeconds(20)), "the waiter never retried");
            Assert.False(waiting.IsCompleted);
            held.Dispose();

            using var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(20)); // a TimeoutException when the waiter never got it
            Assert.NotNull(taken);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void A_lock_that_stays_held_is_given_up_only_when_the_deadline_has_passed()
    {
        var root = NewRoot();
        try
        {
            var sleeps = new List<TimeSpan>();
            var waiter = new FileBaselineLock(BaselineOf(root), sleep: interval =>
            {
                sleeps.Add(interval);
                Thread.Sleep(interval);
            });
            using var held = new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);
            var timeout = TimeSpan.FromMilliseconds(300);

            var clock = Stopwatch.StartNew();
            var taken = waiter.TryAcquire(timeout);
            clock.Stop();

            Assert.Null(taken);
            Assert.True(clock.Elapsed >= timeout, $"gave up after {clock.Elapsed}, before {timeout}");
            Assert.True(sleeps.Count >= 3, $"tried {sleeps.Count + 1} times in {timeout}: not a retry at a short interval");
            Assert.All(sleeps, static s => Assert.True(s > TimeSpan.Zero && s <= FileBaselineLock.RetryInterval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void A_zero_deadline_tries_once_and_does_not_wait()
    {
        var root = NewRoot();
        try
        {
            var slept = 0;
            var waiter = new FileBaselineLock(BaselineOf(root), sleep: _ => slept++);
            using var held = new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.FromSeconds(5));
            Assert.NotNull(held);

            Assert.Null(waiter.TryAcquire(TimeSpan.Zero));
            Assert.Equal(0, slept);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a holder that does not clean up ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TakeAndForget(FileBaselineLock lockFile)
    {
        // The held lock is dropped without Dispose: only the handle's finalizer can close it, as when a holder is gone without cleanup.
        var held = lockFile.TryAcquire(TimeSpan.FromSeconds(5));
        Assert.NotNull(held);
    }

    [Fact]
    public void A_handle_that_is_closed_without_cleanup_frees_the_lock_there_is_no_abandoned_state()
    {
        var root = NewRoot();
        try
        {
            var lockFile = new FileBaselineLock(BaselineOf(root));
            TakeAndForget(lockFile);

            IDisposable? taken = null;
            for (var attempt = 0; attempt < 100 && taken is null; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                taken = new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.Zero);
                if (taken is null)
                {
                    Thread.Sleep(20);
                }
            }

            Assert.NotNull(taken);
            taken.Dispose();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void A_process_that_dies_holding_the_lock_frees_it_at_once_and_while_it_lives_another_process_cannot_take_it()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(BaselineOf(root));
            var lockPath = Path.Combine(BaselineOf(root), FileBaselineLock.FileName);
            var readyPath = Path.Combine(root, "ready.txt");
            var script = $"$f = [System.IO.File]::Open('{lockPath}', 'OpenOrCreate', 'Write', 'None'); [System.IO.File]::WriteAllText('{readyPath}', 'held'); Start-Sleep -Seconds 300";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(script);
            using var holder = Process.Start(start) ?? throw new InvalidOperationException("The helper process did not start.");
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(readyPath))
                {
                    Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(60), "the helper process never took the lock");
                    Assert.False(holder.HasExited, "the helper process ended before it took the lock");
                    Thread.Sleep(50);
                }

                var lockFile = new FileBaselineLock(BaselineOf(root));
                Assert.Null(lockFile.TryAcquire(TimeSpan.FromMilliseconds(100))); // held by another process

                holder.Kill(entireProcessTree: true);
                Assert.True(holder.WaitForExit(TimeSpan.FromSeconds(30)));

                using var taken = lockFile.TryAcquire(TimeSpan.FromSeconds(10));
                Assert.NotNull(taken); // the OS closed its handle; nothing is left to clean up or to take over
            }
            finally
            {
                if (!holder.HasExited)
                {
                    holder.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a lock that cannot be had ----

    [Fact]
    public void A_folder_that_cannot_be_made_gives_no_lock_at_once_and_does_not_wait_for_the_deadline()
    {
        var parent = NewRoot();
        Directory.CreateDirectory(parent);
        try
        {
            var blocker = Path.Combine(parent, "blocker");
            File.WriteAllText(blocker, "a file where the folder should be");
            var slept = 0;
            var lockFile = new FileBaselineLock(Path.Combine(blocker, "baseline"), sleep: _ => slept++);

            var clock = Stopwatch.StartNew();
            var taken = lockFile.TryAcquire(TimeSpan.FromSeconds(30));
            clock.Stop();

            Assert.Null(taken);
            Assert.Equal(0, slept);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            TryDelete(parent);
        }
    }

    [Fact]
    public void A_lock_file_that_is_held_makes_the_engine_give_up_after_its_deadline_with_nothing_written()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(BaselineOf(root));
            var lockPath = Path.Combine(BaselineOf(root), FileBaselineLock.FileName);
            using var outsider = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var engine = new BaselineEngine(
                root,
                null,
                PhysicalBaselineFileSystem.Instance,
                PhysicalHistoryFileReader.Instance,
                new FileBaselineLock(BaselineOf(root)),
                TimeProvider.System,
                static _ => { },
                lockTimeout: TimeSpan.FromMilliseconds(200));
            var now = DateTimeOffset.UtcNow;

            var clock = Stopwatch.StartNew();
            var ticket = engine.BeginRun();
            var result = engine.Complete(ticket, Drives.Inspect(now, [Drives.Nvme(at: now)]));
            clock.Stop();

            Assert.True(ticket.Failed);
            Assert.True(result.ReadFailed);
            var disk = Assert.Single(result.Disks);
            Assert.Equal(BaselineDiskState.NotComparable, disk.State);
            Assert.Equal(CheckEvidenceCode.BaselineReadFailed, disk.Code);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(200));
            Assert.Equal([FileBaselineLock.FileName], Directory.GetFiles(BaselineOf(root)).Select(static p => Path.GetFileName(p)).ToArray());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task A_lock_that_is_let_go_during_the_deadline_lets_the_engine_run_as_usual()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(BaselineOf(root));
            var lockPath = Path.Combine(BaselineOf(root), FileBaselineLock.FileName);
            var outsider = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            using var tried = new ManualResetEventSlim();
            var engine = new BaselineEngine(
                root,
                null,
                PhysicalBaselineFileSystem.Instance,
                PhysicalHistoryFileReader.Instance,
                new FileBaselineLock(BaselineOf(root), sleep: _ =>
                {
                    tried.Set();
                    Thread.Sleep(5);
                }),
                TimeProvider.System,
                static _ => { },
                lockTimeout: TimeSpan.FromSeconds(30));
            var now = DateTimeOffset.UtcNow;

            var running = Task.Run(() => engine.Complete(engine.BeginRun(), Drives.Inspect(now, [Drives.Nvme(at: now)])));
            Assert.True(tried.Wait(TimeSpan.FromSeconds(20)), "the engine never retried");
            outsider.Dispose();

            var result = await running.WaitAsync(TimeSpan.FromSeconds(30)); // a TimeoutException when the engine never got the lock
            var disk = Assert.Single(result.Disks);
            Assert.Equal(CheckEvidenceCode.BaselineFirst, disk.Code);
            Assert.False(result.ReadFailed);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the public engine on the real file system ----

    [Fact]
    public void The_public_engine_records_and_compares_on_a_real_folder_with_the_real_lock_and_leaves_it_free()
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

            Assert.True(Directory.Exists(BaselineOf(root)));
            Assert.True(File.Exists(Path.Combine(BaselineOf(root), "acq-seq.json")));
            Assert.True(File.Exists(Path.Combine(BaselineOf(root), "disk-ordinals.json")));
            Assert.True(File.Exists(Path.Combine(BaselineOf(root), FileBaselineLock.FileName)));
            Assert.Empty(Directory.GetFiles(BaselineOf(root), "*.tmp"));

            // After the run nobody holds it: a lock can be taken at once.
            using var free = new FileBaselineLock(BaselineOf(root)).TryAcquire(TimeSpan.Zero);
            Assert.NotNull(free);
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
