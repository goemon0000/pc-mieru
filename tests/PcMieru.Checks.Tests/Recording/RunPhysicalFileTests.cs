using PcMieru.Core;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The recorder on the real file system, in a throw-away folder under the system temp (never Documents): the lock file, the atomic JSON, the
/// forced flush and the free-space call behave as the in-memory seam says they do. The samples are still handed over by hand.
/// </summary>
public class RunPhysicalFileTests
{
    private const long S = 1_000_000;

    private static async Task<(RunRecorder Recorder, FakeSamples Samples, RunHandle Run, long T0)> StartOn(string root)
    {
        var samples = new FakeSamples();
        var recorder = new RunRecorder(samples, root);
        var run = await recorder.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        return (recorder, samples, run, MonotonicClock.NowMicroseconds());
    }

    [Fact]
    public async Task A_run_on_the_real_file_system_is_written_read_and_deleted()
    {
        using var temp = new TempRoot();
        var root = Path.Combine(temp.Path, "runs");
        var (recorder, samples, run, t0) = await StartOn(root);

        var lockPath = Path.Combine(root, RunFiles.LockName(run.RunId));
        Assert.True(PhysicalRunFileSystem.Instance.IsLockHeld(lockPath));
        Assert.Equal(RunActivityState.Recording, recorder.State);
        Assert.Equal(RunStatus.Recording, RunJson.Read(File.ReadAllBytes(run.JsonPath)).Status);

        for (var n = 1; n <= 3; n++)
        {
            samples.Raise(SampleKit.Sample(n, t0 + (n * S), gpus: [SampleKit.Gpu(sequence: n, acquiredUs: t0 + (n * S))]));
        }

        await recorder.MarkAsync(run, MarkerKind.BenchStart, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var store = new RunStore(root);
        var whileRecording = await store.DeleteAsync(run.RunId, CancellationToken.None);
        Assert.Equal(RunDeleteOutcome.Recording, whileRecording.Outcome);
        Assert.True(File.Exists(run.JsonPath));

        var result = await recorder.StopAsync(run, RunStopReason.User).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, result.Rows);
        Assert.True(result.DurationUs >= 3 * S);
        Assert.False(PhysicalRunFileSystem.Instance.IsLockHeld(lockPath));
        Assert.False(File.Exists(lockPath));
        Assert.False(File.Exists(Path.Combine(root, RunFiles.TempName(run.RunId))));
        Assert.Equal(
            new[] { RunFiles.JsonName(run.RunId), RunFiles.CsvName(run.RunId) },
            Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var info = Assert.Single((await store.ListAsync(CancellationToken.None)).Runs);
        Assert.Equal(RunStatus.Completed, info.Status);
        var detail = await store.ReadAsync(run.RunId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(3, detail.Info.Rows);
        var section = Assert.Single(detail.Summary.Sections);
        Assert.Equal("bench", section.Kind);
        Assert.True(section.Open);

        Assert.Equal(RunDeleteOutcome.Deleted, (await store.DeleteAsync(run.RunId, CancellationToken.None)).Outcome);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task A_run_that_was_cut_off_on_the_real_file_system_is_taken_over_at_the_next_start_unless_its_lock_is_held()
    {
        using var temp = new TempRoot();
        var root = Path.Combine(temp.Path, "runs");
        Directory.CreateDirectory(root);
        var id = RecHarness.IdOf(50);
        var layout = RunLayout.Create([SampleKit.Gpu()]);
        var samples = Enumerable.Range(1, 3)
            .Select(i => SampleKit.Sample(i, RecHarness.At(i * S), gpus: [SampleKit.Gpu(sequence: i, acquiredUs: RecHarness.At(i * S))]))
            .ToList();
        var header = RunFileKit.HeaderOf(layout, id, RunStatus.Recording, null);
        File.WriteAllBytes(Path.Combine(root, RunFiles.JsonName(id)), RunJson.Write(header));
        File.WriteAllText(Path.Combine(root, RunFiles.CsvName(id)), RunFileKit.CsvText(layout, RecHarness.StartUs, samples) + "4000000,2026-11-21T03:0");

        // Held by "another process": left alone.
        var lockPath = Path.Combine(root, RunFiles.LockName(id));
        using (PhysicalRunFileSystem.Instance.TryLock(lockPath))
        {
            var (held, _, heldRun, _) = await StartOn(root);
            await held.StopAsync(heldRun, RunStopReason.User);
            Assert.Equal(RunStatus.Recording, RunJson.Read(File.ReadAllBytes(Path.Combine(root, RunFiles.JsonName(id)))).Status);
        }

        // Nobody holds it any more: it becomes interrupted, with the rows up to the last whole line.
        var (recorder, _, run, _) = await StartOn(root);
        await recorder.StopAsync(run, RunStopReason.User);
        var found = RunJson.Read(File.ReadAllBytes(Path.Combine(root, RunFiles.JsonName(id))));
        Assert.Equal(RunStatus.Interrupted, found.Status);
        Assert.Equal(RunStopReason.Unknown, found.StopReason);
        Assert.Equal(3, found.Rows);
        Assert.Equal(3 * S, found.DurationUs);
    }

    [Fact]
    public void The_real_free_space_call_answers_for_a_folder_that_exists()
    {
        using var temp = new TempRoot();
        var free = PhysicalRunFileSystem.Instance.GetFreeBytes(temp.Path);
        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    [Fact]
    public void Two_locks_on_the_same_name_are_never_held_at_once_and_the_second_is_free_again_after_the_first_is_disposed()
    {
        using var temp = new TempRoot();
        var path = Path.Combine(temp.Path, "run-x.lock");
        var first = PhysicalRunFileSystem.Instance.TryLock(path);
        Assert.NotNull(first);
        Assert.Null(PhysicalRunFileSystem.Instance.TryLock(path));
        Assert.True(PhysicalRunFileSystem.Instance.IsLockHeld(path));
        first.Dispose();
        Assert.False(PhysicalRunFileSystem.Instance.IsLockHeld(path));
        using var again = PhysicalRunFileSystem.Instance.TryLock(path);
        Assert.NotNull(again);
    }
}
