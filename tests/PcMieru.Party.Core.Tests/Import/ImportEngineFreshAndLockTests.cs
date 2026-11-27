using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Party.Game;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>ImportEngine.StartFresh and the per-user import lock.</summary>
public class ImportEngineFreshAndLockTests
{
    private static byte[] Save(double memory) => ImportEnginePrepareTests.Save(memory);

    /// <summary>Holds a lock on a thread of its own (a mutex belongs to the thread that took it) until told to let go.</summary>
    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;

        public Holder(string name)
        {
            var taken = new ManualResetEventSlim(false);
            bool got = false;
            _thread = new Thread(() =>
            {
                using var gate = ImportLock.TryAcquire(name, TimeSpan.FromSeconds(5));
                got = gate is not null;
                taken.Set();
                if (got) _release.Wait(TimeSpan.FromSeconds(30));
            }) { IsBackground = true };
            _thread.Start();
            Assert.True(taken.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(got, "the holder could not take the lock");
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)));
        }
    }

    // ---- StartFresh ----

    [Fact]
    public void StartFresh_writes_a_fresh_save_where_there_is_none_and_leaves_the_old_save_alone()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;   // the old product runs: the user chose not to continue it, so it does not matter

        var result = bed.Engine.StartFresh();

        Assert.Equal(ImportOutcome.StartedFresh, result.Outcome);
        Assert.Equal(ImportReason.StartedFresh, result.Reason);
        Assert.True(result.Succeeded);
        Assert.Null(result.OriginalCopy);
        Assert.Equal(0, bed.Probe.Calls);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
        Assert.NotEqual((byte)0xEF, bed.DestinationBytes()[0]);   // UTF-8 without BOM

        var loaded = PartyState.Load(bed.DestinationDir);
        Assert.Equal(PartyState.CurrentVersion, loaded.SaveVersion);
        Assert.Empty(loaded.Experience);
    }

    [Fact]
    public void StartFresh_works_with_no_old_save_at_all()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.NoCandidate, bed.Engine.Prepare().Status);
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        Assert.Equal(ImportOutcome.AlreadyHave, bed.Engine.Prepare().Status);
    }

    [Fact]
    public void StartFresh_twice_keeps_the_first()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        byte[] first = bed.DestinationBytes();

        var second = bed.Engine.StartFresh();
        Assert.Equal(ImportOutcome.AlreadyHave, second.Outcome);
        Assert.Equal(ImportReason.AlreadyHave, second.Reason);
        Assert.False(second.Succeeded);
        Assert.Equal(first, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void StartFresh_never_replaces_a_save_that_can_be_read()
    {
        using var bed = new ImportBed();
        bed.WriteDestination(Save(5.5));
        byte[] before = bed.DestinationBytes();

        Assert.Equal(ImportOutcome.AlreadyHave, bed.Engine.StartFresh().Outcome);
        Assert.Equal(before, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void StartFresh_never_replaces_a_destination_that_cannot_be_read_and_says_why()
    {
        using var bed = new ImportBed();
        bed.WriteDestination(Encoding.UTF8.GetBytes("this is not json"));

        var result = bed.Engine.StartFresh();
        Assert.Equal(ImportOutcome.DestinationBlocked, result.Outcome);
        Assert.Equal(ImportReason.DestinationBroken, result.Reason);
        Assert.Equal("this is not json", Encoding.UTF8.GetString(bed.DestinationBytes()));
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void StartFresh_loses_to_a_destination_that_appears_just_before_the_move()
    {
        using var bed = new ImportBed();
        bed.Engine.BeforeMove = _ => File.WriteAllBytes(bed.Destination, Save(5.5));

        var result = bed.Engine.StartFresh();
        Assert.Equal(ImportOutcome.AlreadyHave, result.Outcome);
        Assert.Equal(Save(5.5), bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void StartFresh_that_fails_to_write_leaves_nothing()
    {
        using var bed = new ImportBed();
        bed.Engine.BeforeMove = _ => throw new IOException("the disk went away");

        var result = bed.Engine.StartFresh();
        Assert.Equal(ImportOutcome.Failed, result.Outcome);
        Assert.Equal(ImportReason.WriteFailed, result.Reason);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void Later_means_nothing_is_created_at_all()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        // The user sees the question and answers "later": the app calls Prepare and then nothing.
        Assert.Equal(ImportOutcome.Ready, bed.Engine.Prepare().Status);
        Assert.Equal(ImportOutcome.Ready, bed.Engine.Prepare().Status);

        Assert.False(Directory.Exists(bed.DestinationDir));
        Assert.False(Directory.Exists(Path.Combine(bed.Paths.RoamingRoot, "PCMieru")));
    }

    // ---- the lock ----

    [Fact]
    public void The_lock_name_is_per_user_and_is_not_the_resident_mutex()
    {
        string name = ImportLock.CurrentName();
        Assert.Matches(@"^Global\\PCMieru\.Party\.Import\.v1\.[0-9a-f]{16}$", name);
        Assert.Equal(PartyMutexNames.ImportPrefix + PartyMutexNames.SidHash(PartyMutexNames.CurrentSid()), name);
        Assert.NotEqual(PartyMutexNames.CurrentResidentName(), name);
        Assert.Equal(PartyMutexNames.CurrentImportName(), name);
    }

    /// <summary>A thread of its own can take the lock at once (a mutex is thread-affine, so the check runs elsewhere).</summary>
    private static bool IsFree(string name)
    {
        bool free = false;
        var t = new Thread(() =>
        {
            using var gate = ImportLock.TryAcquire(name, TimeSpan.Zero);
            free = gate is not null;
        });
        t.Start();
        t.Join();
        return free;
    }

    [Fact]
    public void Every_call_gives_the_lock_back_before_it_returns()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        var plan = bed.Engine.Prepare();
        Assert.True(IsFree(bed.LockName));
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(plan, false).Outcome);
        Assert.True(IsFree(bed.LockName));
        Assert.Equal(ImportOutcome.AlreadyHave, bed.Engine.StartFresh().Outcome);   // the early return gives it back as well
        Assert.True(IsFree(bed.LockName));
    }

    [Fact]
    public void A_failing_call_gives_the_lock_back_too()
    {
        using var bed = new ImportBed();
        bed.Engine.BeforeMove = _ => throw new IOException("boom");
        Assert.Equal(ImportOutcome.Failed, bed.Engine.StartFresh().Outcome);
        Assert.True(IsFree(bed.LockName));
    }

    [Fact]
    public void A_busy_lock_is_answered_as_Busy_by_all_three_calls_and_nothing_is_touched()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        using (new Holder(bed.LockName))
        {
            var prepared = bed.Engine.Prepare();
            Assert.Equal(ImportOutcome.Busy, prepared.Status);
            Assert.Equal(ImportReason.LockBusy, prepared.Reason);
            Assert.False(prepared.CanCommit);

            var committed = bed.Engine.Commit(plan, true);
            Assert.Equal(ImportOutcome.Busy, committed.Outcome);
            Assert.Equal(ImportReason.LockBusy, committed.Reason);

            var fresh = bed.Engine.StartFresh();
            Assert.Equal(ImportOutcome.Busy, fresh.Outcome);
            Assert.Equal(ImportReason.LockBusy, fresh.Reason);

            Assert.Equal(1, bed.Probe.Calls);   // only the Prepare before the holder came
            Assert.False(Directory.Exists(bed.DestinationDir));
        }

        // The holder let go: the same plan commits.
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(plan, false).Outcome);
    }

    [Fact]
    public void Two_engines_on_one_lock_never_both_write()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var other = new ImportEngine(bed.Paths, new FakeProbe(), bed.LockName, TimeSpan.FromSeconds(10));
        var planA = bed.Engine.Prepare();
        var planB = other.Prepare();

        var results = new ImportResult[2];
        var go = new ManualResetEventSlim(false);
        var threads = new[]
        {
            new Thread(() => { go.Wait(); results[0] = bed.Engine.Commit(planA, false); }),
            new Thread(() => { go.Wait(); results[1] = other.Commit(planB, false); }),
        };
        foreach (var t in threads) t.Start();
        go.Set();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(20)));

        Assert.Single(results, r => r.Outcome == ImportOutcome.Imported);
        Assert.Single(results, r => r.Outcome == ImportOutcome.AlreadyHave);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_lock_whose_owner_ended_without_giving_it_back_is_ours()
    {
        string name = @"Local\PCMieru.Party.Import.test." + Guid.NewGuid().ToString("N");
        var leaked = new List<ImportLock>();
        var t = new Thread(() => leaked.Add(ImportLock.TryAcquire(name, TimeSpan.FromSeconds(5))!));   // takes it and ends: abandoned
        t.Start();
        t.Join();
        Assert.Single(leaked);
        Assert.NotNull(leaked[0]);

        using var gate = ImportLock.TryAcquire(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(gate);
        GC.KeepAlive(leaked);
    }

    [Fact]
    public void A_lock_that_is_kept_is_not_given_to_a_second_taker_and_dispose_is_safe_twice()
    {
        string name = @"Local\PCMieru.Party.Import.test." + Guid.NewGuid().ToString("N");
        using var holder = new Holder(name);

        bool got = true;
        var t = new Thread(() =>
        {
            using var gate = ImportLock.TryAcquire(name, TimeSpan.FromMilliseconds(100));
            got = gate is not null;
        });
        t.Start();
        t.Join();
        Assert.False(got);

        var lone = ImportLock.TryAcquire(@"Local\PCMieru.Party.Import.test." + Guid.NewGuid().ToString("N"), TimeSpan.Zero)!;
        lone.Dispose();
        lone.Dispose();
    }
}
