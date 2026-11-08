using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

/// <summary>
/// Real-system tests. They only ever act on a <c>ping.exe</c> that the test started itself (and kills on dispose).
/// Real system processes (csrss, services, explorer ...) are opened with access 0 (query only), so nothing about them can change.
/// </summary>
public class ProcessOperationsTests
{
    private static readonly string System32 = Environment.SystemDirectory;

    /// <summary>A "someone else" identity: so the ping is not mistaken for ourselves.</summary>
    private static readonly SelfIdentity Other = new(uint.MaxValue - 1, @"C:\Program Files\PC Mieru");

    [Fact]
    public void Terminate_ends_our_own_ping()
    {
        using var ping = new OwnPing();

        var result = ProcessOperations.Terminate(ping.Pid, ping.CreateTime, Other, System32);

        Assert.Equal(OperationCode.Ok, result.Code);
        Assert.Equal(MessageKeys.ProcessTerminated, result.MessageKey);
        Assert.True(WaitUntil(() => ping.HasExited));
    }

    [Fact]
    public void A_wrong_create_time_means_replaced_and_the_process_is_left_alone()
    {
        using var ping = new OwnPing();

        var result = ProcessOperations.Terminate(ping.Pid, ping.CreateTime + 1, Other, System32);

        Assert.Equal(OperationCode.Gone, result.Code);
        Assert.Equal(MessageKeys.ProcessReplaced, result.MessageKey);
        Assert.False(ping.HasExited);
    }

    [Fact]
    public void A_wrong_create_time_changes_no_priority_either()
    {
        using var ping = new OwnPing();
        var before = ping.Priority;

        var result = ProcessOperations.SetPriority(ping.Pid, ping.CreateTime - 1, SettablePriority.High, Other, System32);

        Assert.Equal(OperationCode.Gone, result.Code);
        Assert.Equal(before, ping.Priority);
    }

    [Fact]
    public void A_process_that_is_already_gone_is_reported_as_gone()
    {
        long created;
        uint pid;
        using (var ping = new OwnPing())
        {
            pid = ping.Pid;
            created = ping.CreateTime;
        }

        Assert.True(WaitUntil(() => ProcessOperations.Terminate(pid, created, Other, System32).Code == OperationCode.Gone));
    }

    [Fact]
    public void Terminate_refuses_ourselves_and_the_process_stays_alive()
    {
        using var ping = new OwnPing();
        var self = new SelfIdentity(ping.Pid, null);

        var result = ProcessOperations.Terminate(ping.Pid, ping.CreateTime, self, System32);

        Assert.Equal(OperationCode.Protected, result.Code);
        Assert.Equal(MessageKeys.ProtectedSelf, result.MessageKey);
        Assert.False(ping.HasExited);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void Pid_0_and_4_are_refused_before_anything_is_opened(uint pid)
    {
        var result = ProcessOperations.Terminate(pid, 1, Other, System32);
        Assert.Equal(OperationCode.Protected, result.Code);
        Assert.Equal(MessageKeys.ProtectedSystem, result.MessageKey);
    }

    [Fact]
    public void Priority_is_set_and_read_back()
    {
        using var ping = new OwnPing();

        var below = ProcessOperations.SetPriority(ping.Pid, ping.CreateTime, SettablePriority.BelowNormal, Other, System32);
        Assert.Equal(OperationCode.Ok, below.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.BelowNormal, ping.Priority);

        var normal = ProcessOperations.SetPriority(ping.Pid, ping.CreateTime, SettablePriority.Normal, Other, System32);
        Assert.Equal(OperationCode.Ok, normal.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, ping.Priority);
    }

    [Fact]
    public void Efficiency_mode_turns_on_off_and_restores_what_was_there()
    {
        using var ping = new OwnPing();
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, ping.Priority);

        var on = ProcessOperations.SetEfficiency(ping.Pid, ping.CreateTime, enable: true, restore: null, Other, System32);

        Assert.Equal(OperationCode.Ok, on.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Idle, ping.Priority);
        Assert.True(ProcessOperations.QueryThrottle(ping.Pid, out var control, out var state));
        Assert.Equal(1u, control);
        Assert.Equal(1u, state);
        Assert.Equal("32", on.Data![HelperDataKeys.PreviousPriority]);

        var restore = new EfficiencyRestore(
            uint.Parse(on.Data[HelperDataKeys.PreviousPriority]),
            uint.Parse(on.Data[HelperDataKeys.PreviousControl]),
            uint.Parse(on.Data[HelperDataKeys.PreviousState]));
        var off = ProcessOperations.SetEfficiency(ping.Pid, ping.CreateTime, enable: false, restore, Other, System32);

        Assert.Equal(OperationCode.Ok, off.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, ping.Priority);
        Assert.True(ProcessOperations.QueryThrottle(ping.Pid, out var control2, out var state2));
        Assert.Equal(0u, state2);
        _ = control2;
    }

    [Fact]
    public void Efficiency_off_without_a_record_falls_back_to_normal()
    {
        using var ping = new OwnPing();
        ProcessOperations.SetEfficiency(ping.Pid, ping.CreateTime, true, null, Other, System32);

        var off = ProcessOperations.SetEfficiency(ping.Pid, ping.CreateTime, false, null, Other, System32);

        Assert.Equal(OperationCode.Ok, off.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, ping.Priority);
    }

    [Fact]
    public void Efficiency_restore_values_that_are_not_restorable_are_refused_before_opening()
    {
        using var ping = new OwnPing();
        var realtime = new EfficiencyRestore(0x100, 0, 0);

        var result = ProcessOperations.SetEfficiency(ping.Pid, ping.CreateTime, false, realtime, Other, System32);

        Assert.Equal(OperationCode.InvalidArguments, result.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, ping.Priority);
    }

    [Fact]
    public void Efficiency_is_refused_for_ourselves()
    {
        using var ping = new OwnPing();
        var result = ProcessOperations.SetEfficiency(ping.Pid, ping.CreateTime, true, null, new SelfIdentity(ping.Pid, null), System32);
        Assert.Equal(OperationCode.Protected, result.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.Normal, ping.Priority);
    }

    [Fact]
    public void Self_identity_is_this_process()
    {
        var self = ProcessOperations.CurrentSelf();
        Assert.Equal((uint)Environment.ProcessId, self.Pid);
        Assert.False(string.IsNullOrEmpty(self.Directory));
    }

    /// <summary>
    /// Real system processes, opened for QUERY ONLY (access 0): this can read their protection facts but cannot change them,
    /// so a bug in the rules shows up as a wrong verdict, never as a killed Windows component.
    /// </summary>
    [Theory]
    [InlineData("csrss")]
    [InlineData("smss")]
    [InlineData("services")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("lsass")]
    public void Real_windows_core_processes_come_out_as_protected_with_a_query_only_open(string name)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        if (processes.Length == 0)
        {
            return; // not running in this session
        }

        try
        {
            foreach (var process in processes)
            {
                var created = CreateTimeOrNull((uint)process.Id);
                if (created is null)
                {
                    continue; // cannot even query it: that is the Denied path, also safe
                }

                var refused = ProcessGuard.Open((uint)process.Id, created.Value, 0, Other, System32, out var guarded);
                guarded?.Dispose();
                Assert.NotNull(refused);
                Assert.Contains(refused!.Code, new[] { OperationCode.Protected, OperationCode.Denied });
                Assert.Null(guarded);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [Fact]
    public void An_ordinary_process_of_ours_is_allowed_with_a_query_only_open()
    {
        using var ping = new OwnPing();

        var refused = ProcessGuard.Open(ping.Pid, ping.CreateTime, 0, Other, System32, out var guarded);
        using (guarded)
        {
            Assert.Null(refused);
            Assert.NotNull(guarded);
            Assert.Equal(Path.Combine(System32, "PING.EXE").ToUpperInvariant(), guarded!.Facts.ImagePath!.ToUpperInvariant());
            Assert.Equal(false, guarded.Facts.IsCritical);
            Assert.Equal(false, guarded.Facts.HasProtection);
        }
    }

    [Fact]
    public void Explorer_is_not_in_the_protected_list_but_nothing_is_done_to_it()
    {
        var explorers = System.Diagnostics.Process.GetProcessesByName("explorer");
        if (explorers.Length == 0)
        {
            return;
        }

        try
        {
            var explorer = explorers[0];
            var created = CreateTimeOrNull((uint)explorer.Id);
            if (created is null)
            {
                return;
            }

            // access 0 = query only. The point is the verdict (explorer may be restarted by the user, so it is not protected).
            var refused = ProcessGuard.Open((uint)explorer.Id, created.Value, 0, Other, System32, out var guarded);
            using (guarded)
            {
                Assert.Null(refused);
                Assert.EndsWith("explorer.exe", guarded!.Facts.ImagePath, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var process in explorers)
            {
                process.Dispose();
            }
        }
    }

    [Fact]
    public void Inspect_says_allowed_for_our_ping_and_unknown_when_the_identity_does_not_match()
    {
        using var ping = new OwnPing();

        Assert.Equal(ProtectionOutcome.Allowed, ProcessGuard.Inspect(ping.Pid, ping.CreateTime, Other, System32).Outcome);
        var replaced = ProcessGuard.Inspect(ping.Pid, ping.CreateTime + 1, Other, System32);
        Assert.Equal(ProtectionOutcome.Unknown, replaced.Outcome);
        Assert.Equal(ProtectionOutcome.Protected, ProcessGuard.Inspect(ping.Pid, ping.CreateTime, new SelfIdentity(ping.Pid, null), System32).Outcome);
        Assert.Equal(ProtectionReason.SystemProcess, ProcessGuard.Inspect(4, 1, Other, System32).Reason);
    }

    [Fact]
    public void Inspect_of_a_process_that_ended_is_unknown_not_allowed()
    {
        uint pid;
        long created;
        using (var ping = new OwnPing())
        {
            pid = ping.Pid;
            created = ping.CreateTime;
            Assert.Equal(OperationCode.Ok, ProcessOperations.Terminate(pid, created, Other, System32).Code);
        }

        Assert.Equal(ProtectionOutcome.Unknown, ProcessGuard.Inspect(pid, created, Other, System32).Outcome);
    }

    [Fact]
    public void Inspect_of_real_csrss_is_protected_or_unknown_and_never_allowed()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("csrss"))
        {
            using (process)
            {
                var created = CreateTimeOrNull((uint)process.Id);
                if (created is null)
                {
                    continue;
                }

                Assert.NotEqual(ProtectionOutcome.Allowed, ProcessGuard.Inspect((uint)process.Id, created.Value, Other, System32).Outcome);
            }
        }
    }

    [Fact]
    public void Affinity_is_narrowed_and_restored_on_our_own_ping_and_bad_masks_are_refused()
    {
        using var ping = new OwnPing();
        Assert.True(ProcessOperations.TryQueryAffinity(ping.Pid, ping.CreateTime, out var original, out var system));
        Assert.NotEqual(0UL, system);

        var lowest = system & (~system + 1);
        var set = ProcessOperations.SetAffinity(ping.Pid, ping.CreateTime, lowest, Other, System32);
        Assert.Equal(OperationCode.Ok, set.Code);
        Assert.Equal(MessageKeys.AffinitySet, set.MessageKey);
        Assert.True(ProcessOperations.TryQueryAffinity(ping.Pid, ping.CreateTime, out var now, out _));
        Assert.Equal(lowest, now);

        Assert.Equal(MessageKeys.AffinityInvalid, ProcessOperations.SetAffinity(ping.Pid, ping.CreateTime, 0, Other, System32).MessageKey);
        Assert.Equal(OperationCode.InvalidArguments, ProcessOperations.SetAffinity(ping.Pid, ping.CreateTime, ~system, Other, System32).Code);
        Assert.Equal(OperationCode.Gone, ProcessOperations.SetAffinity(ping.Pid, ping.CreateTime + 1, lowest, Other, System32).Code);

        Assert.Equal(OperationCode.Ok, ProcessOperations.SetAffinity(ping.Pid, ping.CreateTime, original, Other, System32).Code);
        Assert.True(ProcessOperations.TryQueryAffinity(ping.Pid, ping.CreateTime, out var back, out _));
        Assert.Equal(original, back);

        // A creation time that does not match is "false", not somebody else's mask.
        Assert.False(ProcessOperations.TryQueryAffinity(ping.Pid, ping.CreateTime + 1, out _, out _));
    }

    [Fact]
    public void Affinity_is_refused_for_ourselves()
    {
        using var ping = new OwnPing();
        var result = ProcessOperations.SetAffinity(ping.Pid, ping.CreateTime, 1, new SelfIdentity(ping.Pid, null), System32);
        Assert.Equal(OperationCode.Protected, result.Code);
    }

    private static long? CreateTimeOrNull(uint pid)
    {
        using var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid)
        {
            return null;
        }

        return Native.GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }
}

public class HelperExecutorProcessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static HelperEnvironment Env() => new()
    {
        Self = new SelfIdentity(uint.MaxValue - 1, @"C:\Program Files\PC Mieru"),
        SystemDirectory = Environment.SystemDirectory,
        Services = new FakeServiceApi(new FakeClock()),
        Clock = new FakeClock(),
        OpenStartupRoot = () => throw new InvalidOperationException("no registry in this test"),
        EntryExists = (_, _) => throw new InvalidOperationException("no startup in this test"),
        NowFileTime = () => 1,
    };

    [Fact]
    public void The_executor_dispatches_priority_and_terminate_on_our_own_ping()
    {
        using var ping = new OwnPing();
        const string nonce = "0123456789abcdef0123";
        const string path = @"C:\x\0123456789abcdef0123.json";

        var priority = HelperExecutor.Execute(
            new HelperRequest(HelperOperation.ProcessPriority, nonce, path) { Pid = ping.Pid, CreateTime = ping.CreateTime, Priority = SettablePriority.BelowNormal },
            Env());
        Assert.Equal(OperationCode.Ok, priority.Code);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.BelowNormal, ping.Priority);

        var terminate = HelperExecutor.Execute(
            new HelperRequest(HelperOperation.ProcessTerminate, nonce, path) { Pid = ping.Pid, CreateTime = ping.CreateTime },
            Env());
        Assert.Equal(OperationCode.Ok, terminate.Code);
    }

    [Fact]
    public void Service_operations_go_through_the_fake_only()
    {
        var clock = new FakeClock();
        var api = new FakeServiceApi(clock) { Initial = FakeServiceApi.Running() };
        var env = new HelperEnvironment
        {
            Self = new SelfIdentity(1, null),
            SystemDirectory = Environment.SystemDirectory,
            Services = api,
            Clock = clock,
            OpenStartupRoot = () => throw new InvalidOperationException(),
            EntryExists = (_, _) => false,
            NowFileTime = () => 1,
        };

        var result = HelperExecutor.Execute(
            new HelperRequest(HelperOperation.ServiceStart, "0123456789abcdef0123", @"C:\x\0123456789abcdef0123.json") { ServiceName = "Demo" },
            env);

        Assert.Equal(MessageKeys.ServiceAlreadyRunning, result.MessageKey);
        Assert.Equal(0, api.StartCalls);
    }

    [Fact]
    public void Disk_reads_on_this_pc_are_either_data_or_a_clean_refusal_and_never_write_anything()
    {
        // Read-only by construction (StorageProtocol cannot build a write). Which answer each drive gives depends on its bus and
        // on whether the test runs elevated: data, "unsupported bus", denied and "no such drive" are all fine, an exception is not.
        var allowed = new[] { OperationCode.Ok, OperationCode.Denied, OperationCode.Unsupported, OperationCode.NotFound, OperationCode.Error };
        for (var drive = 0; drive < 8; drive++)
        {
            var nvme = DiskOperations.NvmeHealth(drive);
            var smart = DiskOperations.SmartRead(drive);
            output.WriteLine($"drive {drive}: nvme-health={nvme.Code} {nvme.MessageKey}; smart-read={smart.Code} {smart.MessageKey}");
            Assert.Contains(nvme.Code, allowed);
            Assert.Contains(smart.Code, allowed);
            if (nvme.Code == OperationCode.Ok)
            {
                var health = Convert.FromBase64String(nvme.Data![HelperDataKeys.NvmeHealth]);
                Assert.Equal(512, health.Length);

                // For the log only: composite temperature (Kelvin, bytes 1-2) and percentage used (byte 5) of the NVMe health page.
                output.WriteLine($"  nvme drive {drive}: {BitConverter.ToUInt16(health, 1) - 273} C, used {health[5]} %, errors page {(nvme.Data.ContainsKey(HelperDataKeys.NvmeErrors) ? "read" : "absent")}");
            }

            if (smart.Code == OperationCode.Ok)
            {
                Assert.Equal(512, Convert.FromBase64String(smart.Data![HelperDataKeys.SmartData]).Length);
                output.WriteLine($"  smart drive {drive}: source {smart.Data[HelperDataKeys.SmartSource]}, thresholds {(smart.Data.ContainsKey(HelperDataKeys.SmartThresholds) ? "read" : "absent")}");
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void An_absurd_drive_number_is_a_clean_not_found_or_error(int drive)
    {
        var result = DiskOperations.SmartRead(drive);
        Assert.NotEqual(OperationCode.Ok, result.Code);
    }
}
