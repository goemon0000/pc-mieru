using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

public sealed class ProcessGrouperTests
{
    private static ProcessInfo P(uint pid, string name = "x.exe", uint parent = 0) => new(
        new ProcessKey(pid, 1_000 + pid),
        name,
        parent,
        null,
        1,
        1,
        1,
        8,
        0,
        0,
        0,
        0,
        0,
        Value.NotCollected<double>("t"),
        Value.NotCollected<double>("t"),
        Value.NotCollected<double>("t"),
        Value.NotCollected<double>("t"),
        Value.Ok(ProcessState.Running, "t"));

    private static WindowProbeResult Windows(params uint[] pids)
    {
        var result = new WindowProbeResult();
        foreach (var pid in pids)
        {
            _ = result.WithWindow.Add(pid);
        }

        return result;
    }

    private static readonly GroupVerdict BackgroundVerdict = new(ProcessGroup.Background, "test: background");
    private static readonly GroupVerdict WindowsVerdict = new(ProcessGroup.Windows, "test: windows");

    [Fact]
    public void A_visible_window_makes_an_App_whatever_the_image_says()
    {
        using var grouper = new ProcessGrouper(_ => WindowsVerdict);
        var list = new[] { P(1), P(2) };

        grouper.Update(list, Windows(1), ValueStatus.Ok, null);
        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));

        var app = grouper.GroupOf(list[0].Key);
        Assert.Equal(ProcessGroup.App, app.Data);
        Assert.True(app.IsOk);
        Assert.Contains("window", app.Detail);
        Assert.Equal(ProcessGroup.Windows, grouper.GroupOf(list[1].Key).Data);
    }

    [Fact]
    public void Until_the_image_has_been_examined_the_group_is_NotCollected_not_a_guess()
    {
        var release = new ManualResetEventSlim(false);
        using var grouper = new ProcessGrouper(_ =>
        {
            release.Wait(5000);
            return BackgroundVerdict;
        });
        var list = new[] { P(1) };

        grouper.Update(list, Windows(), ValueStatus.Ok, null);
        var pending = grouper.GroupOf(list[0].Key);
        Assert.Equal(ValueStatus.NotCollected, pending.Status);
        Assert.Equal(1, grouper.Pending);

        release.Set();
        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        var done = grouper.GroupOf(list[0].Key);

        Assert.Equal(ProcessGroup.Background, done.Data);
        Assert.Equal("test: background", done.Detail);
        Assert.Equal(0, grouper.Pending);
    }

    [Fact]
    public void A_window_that_appears_or_goes_away_changes_the_group_on_the_next_tick()
    {
        using var grouper = new ProcessGrouper(_ => BackgroundVerdict);
        var list = new[] { P(1) };
        grouper.Update(list, Windows(), ValueStatus.Ok, null);
        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(ProcessGroup.Background, grouper.GroupOf(list[0].Key).Data);

        grouper.Update(list, Windows(1), ValueStatus.Ok, null);
        Assert.Equal(ProcessGroup.App, grouper.GroupOf(list[0].Key).Data);

        grouper.Update(list, Windows(), ValueStatus.Ok, null);
        Assert.Equal(ProcessGroup.Background, grouper.GroupOf(list[0].Key).Data);
    }

    [Fact]
    public void Each_process_is_examined_once_and_never_by_two_threads_at_once()
    {
        var calls = 0;
        var running = 0;
        var peak = 0;
        using var grouper = new ProcessGrouper(process =>
        {
            _ = Interlocked.Increment(ref calls);
            var now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            Thread.Sleep(2);
            _ = Interlocked.Decrement(ref running);
            return WindowsVerdict;
        });
        var list = Enumerable.Range(1, 40).Select(i => P((uint)i)).ToArray();

        for (var tick = 0; tick < 5; tick++)
        {
            grouper.Update(list, Windows(), ValueStatus.Ok, null);
        }

        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(10)));
        grouper.Update(list, Windows(), ValueStatus.Ok, null);
        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(10)));

        Assert.Equal(40, calls);
        Assert.Equal(1, peak);
    }

    [Fact]
    public void A_process_that_left_the_sample_is_Gone_and_forgotten()
    {
        using var grouper = new ProcessGrouper(_ => WindowsVerdict);
        var both = new[] { P(1), P(2) };
        grouper.Update(both, Windows(), ValueStatus.Ok, null);
        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, grouper.EntryCount);

        grouper.Update([both[0]], Windows(), ValueStatus.Ok, null);

        Assert.Equal(ValueStatus.Gone, grouper.GroupOf(both[1].Key).Status);
        Assert.Equal(ProcessGroup.Windows, grouper.GroupOf(both[0].Key).Data);
        Assert.Equal(1, grouper.EntryCount);
        Assert.Equal(ValueStatus.Gone, grouper.GroupOf(new ProcessKey(99, 1)).Status);
    }

    [Fact]
    public void A_failed_window_enumeration_makes_the_group_unknown_not_Background()
    {
        using var grouper = new ProcessGrouper(_ => BackgroundVerdict);
        var list = new[] { P(1) };

        grouper.Update(list, null, ValueStatus.Timeout, "window enumeration: timed out");
        Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        var value = grouper.GroupOf(list[0].Key);

        Assert.Equal(ValueStatus.Timeout, value.Status);
        Assert.Contains("timed out", value.Detail);
    }

    [Fact]
    public void A_check_that_needs_a_retry_is_tried_on_later_ticks_and_then_settles()
    {
        var calls = 0;
        using var grouper = new ProcessGrouper(_ => ++calls < 3 ? new GroupVerdict(ProcessGroup.Background, "transient", Retry: true) : WindowsVerdict);
        var list = new[] { P(1) };

        for (var tick = 0; tick < 4; tick++)
        {
            grouper.Update(list, Windows(), ValueStatus.Ok, null);
            Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(3, calls);
        Assert.Equal(ProcessGroup.Windows, grouper.GroupOf(list[0].Key).Data);
    }

    [Fact]
    public void A_check_that_never_succeeds_gives_up_as_Background_with_the_reason()
    {
        var calls = 0;
        using var grouper = new ProcessGrouper(_ =>
        {
            calls++;
            return new GroupVerdict(ProcessGroup.Background, "still failing", Retry: true);
        });
        var list = new[] { P(1) };

        for (var tick = 0; tick < 6; tick++)
        {
            grouper.Update(list, Windows(), ValueStatus.Ok, null);
            Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(ProcessGrouper.MaxAttempts, calls);
        var value = grouper.GroupOf(list[0].Key);
        Assert.Equal(ProcessGroup.Background, value.Data);
        Assert.Equal("still failing", value.Detail);
    }

    [Fact]
    public void A_throwing_classifier_does_not_kill_the_worker()
    {
        using var grouper = new ProcessGrouper(info => info.Key.Pid == 1 ? throw new InvalidOperationException("boom") : WindowsVerdict);
        var list = new[] { P(1), P(2) };

        for (var tick = 0; tick < 4; tick++)
        {
            grouper.Update(list, Windows(), ValueStatus.Ok, null);
            Assert.True(grouper.WaitForIdle(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(ProcessGroup.Windows, grouper.GroupOf(list[1].Key).Data);
        var failed = grouper.GroupOf(list[0].Key);
        Assert.Equal(ProcessGroup.Background, failed.Data);
        Assert.Contains("InvalidOperationException", failed.Detail);
    }
}

[Trait("Category", "RealMachine")]
public sealed class WindowsFolderClassifierTests(ITestOutputHelper output)
{
    private static ProcessInfo InfoFor(ProcessKey key, uint parent = 0) => new(
        key,
        "x.exe",
        parent,
        null,
        1,
        1,
        1,
        8,
        0,
        0,
        0,
        0,
        0,
        Value.NotCollected<double>("t"),
        Value.NotCollected<double>("t"),
        Value.NotCollected<double>("t"),
        Value.NotCollected<double>("t"),
        Value.Ok(ProcessState.Running, "t"));

    private static Value<SignatureInfo> Sig(SignatureState state) => Value.Ok(new SignatureInfo(state, "Test"), "test");

    [Fact]
    public void A_signed_image_in_the_Windows_folder_is_Windows()
    {
        using var ping = new SpawnedPing();
        var classifier = new WindowsFolderClassifier(new SignatureVerifier().Verify);

        var verdict = classifier.Classify(InfoFor(ping.Key));

        output.WriteLine($"{verdict.Group}: {verdict.Why}");
        Assert.Equal(ProcessGroup.Windows, verdict.Group);
        Assert.False(verdict.Retry);
    }

    [Theory]
    [InlineData(SignatureState.Unsigned, "not signed")]
    [InlineData(SignatureState.Invalid, "not valid")]
    public void An_unsigned_or_tampered_image_in_the_Windows_folder_is_not_Windows(SignatureState state, string reason)
    {
        using var ping = new SpawnedPing();
        var classifier = new WindowsFolderClassifier(_ => Sig(state));

        var verdict = classifier.Classify(InfoFor(ping.Key));

        Assert.Equal(ProcessGroup.Background, verdict.Group);
        Assert.Contains(reason, verdict.Why);
        Assert.False(verdict.Retry);
    }

    [Fact]
    public void An_image_outside_the_Windows_folder_is_Background_without_a_signature_check()
    {
        var checks = 0;
        using var ping = new SpawnedPing();
        var classifier = new WindowsFolderClassifier(
            _ =>
            {
                checks++;
                return Sig(SignatureState.SignedEmbedded);
            },
            windowsDirectory: Path.Combine(Path.GetTempPath(), "not-windows"));

        var verdict = classifier.Classify(InfoFor(ping.Key));

        Assert.Equal(ProcessGroup.Background, verdict.Group);
        Assert.Contains("not in the Windows folder", verdict.Why);
        Assert.Equal(0, checks);
    }

    [Fact]
    public void A_folder_that_only_starts_with_the_same_letters_is_not_the_Windows_folder()
    {
        using var ping = new SpawnedPing();
        var system32 = Environment.SystemDirectory; // C:\WINDOWS\system32
        var classifier = new WindowsFolderClassifier(_ => Sig(SignatureState.SignedEmbedded), windowsDirectory: system32[..^1]);

        // "C:\WINDOWS\system3\" is the prefix: C:\WINDOWS\system32\ping.exe must not match it.
        var verdict = classifier.Classify(InfoFor(ping.Key));

        Assert.Equal(ProcessGroup.Background, verdict.Group);
    }

    [Fact]
    public void A_signature_that_cannot_be_checked_asks_for_a_retry()
    {
        using var ping = new SpawnedPing();
        var classifier = new WindowsFolderClassifier(_ => Value.Missing<SignatureInfo>(ValueStatus.Timeout, "test", "slow disk"));

        var verdict = classifier.Classify(InfoFor(ping.Key));

        Assert.True(verdict.Retry);
        Assert.Contains("slow disk", verdict.Why);
    }

    [Fact]
    public void The_kernels_own_processes_are_Windows()
    {
        var classifier = new WindowsFolderClassifier(_ => Sig(SignatureState.Unsigned));

        Assert.Equal(ProcessGroup.Windows, classifier.Classify(InfoFor(new ProcessKey(0, 0))).Group);
        Assert.Equal(ProcessGroup.Windows, classifier.Classify(InfoFor(ProcessHandleTests.ProcessKeyOf(4))).Group);
    }

    [Fact]
    public void A_process_that_has_exited_is_answered_without_a_retry()
    {
        var ping = new SpawnedPing();
        var key = ping.Key;
        ping.Dispose();
        var classifier = new WindowsFolderClassifier(_ => Sig(SignatureState.SignedEmbedded));

        var verdict = classifier.Classify(InfoFor(key));

        Assert.False(verdict.Retry);
        Assert.Equal(ProcessGroup.Background, verdict.Group);
        Assert.Contains("ended", verdict.Why);
    }
}

[Trait("Category", "RealMachine")]
public sealed class GroupsOfTheRealSystemTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_real_sample_puts_a_spawned_system_tool_in_Windows_and_this_test_host_outside_it()
    {
        using var source = new WindowsSnapshotSource(static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false });
        using var ping = new SpawnedPing();

        _ = await source.SampleAsync();
        Assert.True(source.WaitForGroups(TimeSpan.FromSeconds(60)), "the background examination did not finish");
        var snapshot = await source.SampleAsync();

        var self = snapshot.Processes.Single(p => p.Key.Pid == (uint)Environment.ProcessId);
        var spawned = snapshot.Processes.Single(p => p.Key == ping.Key);
        var selfGroup = source.GroupOf(self.Key);
        var pingGroup = source.GroupOf(spawned.Key);
        output.WriteLine($"test host: {selfGroup.Data} ({selfGroup.Detail}); ping: {pingGroup.Data} ({pingGroup.Detail})");

        Assert.Equal(ProcessGroup.Windows, pingGroup.Data);
        Assert.True(selfGroup.IsOk, selfGroup.Detail);
        Assert.NotEqual(ProcessGroup.Windows, selfGroup.Data); // Background, or App if the test host happens to own a visible console window

        var groups = snapshot.Processes.Select(p => source.GroupOf(p.Key)).ToList();
        var counts = groups.GroupBy(g => g.IsOk ? g.Data.ToString() : g.Status.ToString()).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"{snapshot.Processes.Count} processes: " + string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
        Assert.True(counts.GetValueOrDefault("Windows") > 20, "an ordinary Windows has dozens of signed Windows-folder processes");
    }

    [Fact]
    public async Task A_process_that_is_not_in_the_sample_has_no_group()
    {
        using var source = new WindowsSnapshotSource(static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false });
        _ = await source.SampleAsync();

        Assert.Equal(ValueStatus.Gone, source.GroupOf(new ProcessKey(0x7FFFFFF0, 1)).Status);
    }
}
