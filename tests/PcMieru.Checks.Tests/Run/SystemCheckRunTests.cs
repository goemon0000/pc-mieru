using System.Text;
using PcMieru.Checks.SystemInfo;
using PcMieru.Checks.Tests.SystemInfo;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Run;

/// <summary>
/// The system check end to end (DESIGN-A 3.1, 4.0, 14.1): a hardware provider, WMI and an event log that are scripted in place of the machine,
/// the run framework in between, and the history store on a fresh temp folder. The expectations are the rules written out, not computed with
/// the code under test. Nothing here calls Windows, reads the machine or touches the user's folders.
/// </summary>
public sealed class SystemCheckRunTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static string[] WmiClasses => ["Win32_PageFileUsage", "Win32_ComputerSystem", "Win32_VideoController", "MSFT_PhysicalDisk"];

    private static string EventId(EventCountKind kind) => EventCountQuery.Create(kind, DateTime.UnixEpoch).ItemId;

    private static CheckItem Row(CheckRun run, string id, string? subject = null) =>
        run.Result.Items.Single(i => i.Id == id && i.Subject?.Id == subject);

    private static IEnumerable<CheckItem> RowsOf(CheckRun run, string id) => run.Result.Items.Where(i => i.Id == id);

    private static IEnumerable<CheckItem> EventRows(CheckRun run) => run.Result.Items.Where(static i => i.Id.StartsWith("system.eventlog.", StringComparison.Ordinal));

    /// <summary>A rig whose machine is healthy: the drives Core lists are the drives WMI lists, with the same serials, at the moment the fake machine is at.</summary>
    private static RunRig Rig(ManualTimeProvider? clock = null)
    {
        var rig = new RunRig(clock: clock);
        rig.Wmi.Rows("MSFT_PhysicalDisk", FakeWmiReader.MachineDisks());
        rig.Time.Now = new DateTimeOffset(Hw.Now);
        return rig;
    }

    private static Task<CheckRun> Run(RunRig rig, CancellationToken ct = default) => rig.RunAsync(new CheckRequest(CheckKind.System), null, ct);

    private static async Task<CheckRun> RunWith(Action<RunRig> arrange)
    {
        using var rig = Rig();
        arrange(rig);
        return await Run(rig);
    }

    private static string GpuPnp(int errorCode) => errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- a healthy machine ----

    [Fact]
    public async Task A_healthy_machine_gives_a_valid_completed_system_result_whose_verdict_is_ok()
    {
        using var rig = Rig();

        var run = await Run(rig);

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal(CheckKind.System, run.Result.Kind);
        Assert.Equal(Samples.App, run.Result.App);
        Assert.Equal(Kit.Id(1), run.Result.RunId);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Empty(run.Result.Notes);
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
        Assert.Equal(0, run.Result.Counts.Caution);
        Assert.Equal(0, run.Result.Counts.Check);
        Assert.Equal(0, run.Result.Counts.Unavailable);
        Assert.Equal(run.Result.Items.Count, run.Result.Counts.Total);
        Assert.Equal(run.Result.Items.Count(static i => i.Verdict == ItemVerdict.Info), run.Result.Counts.Info);
    }

    [Fact]
    public async Task A_healthy_machine_has_the_rows_of_every_part_of_it()
    {
        using var rig = Rig();

        var run = await Run(rig);

        // Two drives, in the order of their numbers, each with its own health row (the model is the subject's, the serial is nowhere).
        Assert.Equal(CheckValue.Int(2), Row(run, "system.storage.devices").Value);
        var health = RowsOf(run, "system.storage.health").ToList();
        Assert.Equal(["disk-1", "disk-2"], health.Select(static h => h.Subject!.Id));
        Assert.Equal(["Samsung SSD 980 PRO 2TB", "WDC WD40EZRZ-00GXCB0"], health.Select(static h => h.Subject!.Model));
        Assert.All(health, static h =>
        {
            Assert.Equal(CheckValue.Text("healthy"), h.Value);
            Assert.Equal(ItemVerdict.Ok, h.Verdict);
            Assert.Equal(CheckSource.Wmi, h.Source);
        });

        // Two volumes, each with its free space against its total, and the drive it lives on.
        var free = RowsOf(run, "system.storage.free").ToList();
        Assert.Equal(["volume-1", "volume-2"], free.Select(static f => f.Subject!.Id));
        Assert.Equal(["disk-1", "disk-2"], free.Select(static f => f.Evidence!.DiskId));

        // One GPU with the status of its own controller, one page file, the eleven counts of the event log.
        Assert.Equal(CheckValue.Int(0), Row(run, "system.gpu.device_status", "gpu-1").Value);
        Assert.Equal(CheckValue.Int(1), Row(run, "system.pagefile.count").Value);
        Assert.Equal(CheckValue.Bool(true), Row(run, "system.pagefile.automatic", "pagefile-1").Value);
        Assert.Equal(11, EventRows(run).Count());
        Assert.All(EventRows(run), static e =>
        {
            Assert.Equal(CheckValue.Int(0), e.Value);
            Assert.Equal(CheckSource.Eventlog, e.Source);
        });

        // Two modules of memory at their rated speed.
        Assert.Equal(CheckValue.Int(2), Row(run, "system.memory.modules").Value);
        Assert.Equal(["ram-1", "ram-2"], RowsOf(run, "system.memory.configured_speed").Select(static r => r.Subject!.Id));
    }

    [Fact]
    public async Task The_result_is_saved_and_reads_back_byte_for_byte()
    {
        using var rig = Rig();

        var run = await Run(rig);

        Assert.Equal(CheckSaveStatus.Saved, run.Save.Status);
        Assert.Equal([Kit.NameOf(run.Result)], Kit.Names(rig.HistoryRoot));
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
        var back = await rig.ReadBackAsync(run.Result.RunId);
        Assert.NotNull(back);
        Assert.Equal(Kit.Bytes(run.Result), Kit.Bytes(back!));
        Assert.Equal(CheckKind.System, back!.Kind);
    }

    [Fact]
    public async Task Nothing_that_names_the_machine_reaches_the_file()
    {
        using var rig = Rig();

        var run = await Run(rig);

        var text = Encoding.UTF8.GetString(Kit.Bytes(run.Result));
        Assert.DoesNotContain(Hw.SerialOfDisk0, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Hw.SerialOfDisk1, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Hw.LabelOfC, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pagefile.sys", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VEN_10DE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("D:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalDrive", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- what the run does and does not do ----

    [Fact]
    public async Task The_hardware_is_read_fresh_every_run_and_each_category_is_asked_once()
    {
        using var rig = Rig();

        await Run(rig);

        Assert.Equal(1, rig.Hardware.Invalidations);
        Assert.Equal(
            new[] { HardwareCategory.Cpu, HardwareCategory.Memory, HardwareCategory.Gpu, HardwareCategory.Storage, HardwareCategory.Os },
            rig.Hardware.Asked.Order());
        Assert.Equal(WmiClasses.Order(StringComparer.Ordinal), rig.Wmi.Asked.Order(StringComparer.Ordinal)); // one read of each, the disks' health once for all the drives
        Assert.Equal(11, rig.Events.Calls.Count);

        await Run(rig);

        Assert.Equal(2, rig.Hardware.Invalidations);
    }

    [Fact]
    public async Task The_system_check_never_touches_the_storage_source_or_the_baseline()
    {
        using var rig = Rig();

        var run = await Run(rig);

        Assert.True(run.IsSaved);
        Assert.Equal(0, rig.Source.Calls); // no sample of the drives, so no elevation either
        Assert.Empty(rig.Baseline.DiskFiles());
    }

    [Fact]
    public async Task The_events_are_asked_over_one_window_that_starts_seven_days_before_the_run()
    {
        using var rig = Rig();

        await Run(rig);

        var windows = rig.Events.Calls.Select(static c => c.Query.WindowStartUtc).Distinct().ToList();
        Assert.Single(windows);
        Assert.Equal(EventCountQuery.WindowStartFor(Hw.Now), windows[0]);
    }

    [Fact]
    public async Task The_stages_go_in_order()
    {
        using var rig = Rig();
        var log = new ProgressLog();

        var run = await rig.RunAsync(new CheckRequest(CheckKind.System), log);

        Assert.True(run.IsSaved);
        Assert.Equal([CheckStage.Starting, CheckStage.Collecting, CheckStage.Judging, CheckStage.Saving, CheckStage.Done], log.Stages);
    }

    [Fact]
    public async Task Two_runs_are_two_files_and_neither_changes_the_other()
    {
        using var rig = Rig();

        var first = await Run(rig);
        var second = await Run(rig);

        Assert.Equal(Kit.Id(1), first.Result.RunId);
        Assert.Equal(Kit.Id(2), second.Result.RunId);
        Assert.Equal(2, rig.History.Saved.Count);
        Assert.Equal(Kit.Bytes(first.Result), Kit.Bytes((await rig.ReadBackAsync(first.Result.RunId))!));
        Assert.Equal(Kit.Bytes(second.Result), Kit.Bytes((await rig.ReadBackAsync(second.Result.RunId))!));
    }

    [Fact]
    public async Task A_stop_by_the_caller_is_a_stopped_result_that_is_still_saved()
    {
        using var rig = Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hardware.Then(HardwareCategory.Os, async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Hw.Machine();
        });
        using var cts = new CancellationTokenSource();

        var task = Run(rig, cts.Token);
        await started.Task.WaitAsync(QueryTest.Patience);
        await cts.CancelAsync();
        var run = await task.WaitAsync(QueryTest.Patience);

        Assert.Equal(CheckOutcome.Stopped, run.Result.Outcome);
        Assert.Null(run.FailureCode);
        Assert.True(run.IsSaved);
        Assert.Equal(CheckKind.System, run.Result.Kind);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal([run.Result.RunId], rig.History.Saved.Select(static r => r.RunId));
    }

    // ---- faults: only the rows that rest on the failed question change ----

    [Fact]
    public async Task One_event_count_that_times_out_is_only_its_own_row_and_the_verdict_cannot_be_ok()
    {
        var run = await RunWith(static rig => rig.Events.Fails(EventId(EventCountKind.Disk), CheckReason.Timeout));

        var bad = Row(run, "system.eventlog.disk7d");
        Assert.Equal(ItemVerdict.Unavailable, bad.Verdict);
        Assert.Equal(CheckReason.Timeout, bad.Reason);
        Assert.Null(bad.Value);
        Assert.Equal(CheckSource.Eventlog, bad.Source);
        Assert.All(EventRows(run).Where(static e => e.Id != "system.eventlog.disk7d"), static e => Assert.Equal(CheckValue.Int(0), e.Value));
        Assert.Equal(1, run.Result.Counts.Unavailable);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict); // one count was not read, so "ok" cannot be said
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    [Fact]
    public async Task When_the_event_deadline_is_gone_the_rest_of_the_counts_are_timeouts_and_the_rest_of_the_check_is_not_touched()
    {
        var clock = new ManualTimeProvider();
        using var rig = Rig(clock);
        rig.Events.Then(EventId(EventCountKind.Ntfs), (_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return Task.FromResult(CheckReading<long>.TimedOut());
        });

        var run = await Run(rig);

        // Whea, whea warning, disk, disk warning came; ntfs gave up; the six after it were not even asked.
        Assert.Equal(5, rig.Events.Asked.Count);
        foreach (var id in new[] { "whea7d", "whea_warning7d", "disk7d", "disk_warning7d" })
        {
            Assert.Equal(CheckValue.Int(0), Row(run, "system.eventlog." + id).Value);
        }

        foreach (var id in new[] { "ntfs7d", "kp41_7d", "volmgr7d", "stornvme7d" })
        {
            var row = Row(run, "system.eventlog." + id);
            Assert.Equal(ItemVerdict.Unavailable, row.Verdict); // the judged ones
            Assert.Equal(CheckReason.Timeout, row.Reason);
            Assert.Null(row.Value);
        }

        foreach (var id in new[] { "ntfs_warning7d", "stornvme_warning7d", "volmgr_warning7d" })
        {
            var row = Row(run, "system.eventlog." + id);
            Assert.Equal(ItemVerdict.Info, row.Verdict); // the warning rows are never judged
            Assert.Equal(CheckReason.Timeout, row.Reason);
            Assert.Null(row.Value);
        }

        // The other groups were not touched by it.
        Assert.Equal(CheckValue.Int(2), Row(run, "system.storage.devices").Value);
        Assert.Equal(CheckValue.Int(1), Row(run, "system.pagefile.count").Value);
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    [Fact]
    public async Task When_WMI_is_denied_the_rows_that_rest_on_it_are_unavailable_and_denied_and_the_others_are_as_they_were()
    {
        var run = await RunWith(static rig =>
        {
            foreach (var wmiClass in WmiClasses)
            {
                rig.Wmi.Fails(wmiClass, CheckReason.Denied);
            }
        });

        // The page files: how many there are is not known, and so there is no row of a file.
        var count = Row(run, "system.pagefile.count");
        Assert.Equal(ItemVerdict.Unavailable, count.Verdict);
        Assert.Equal(CheckReason.Denied, count.Reason);
        Assert.Null(count.Value);
        Assert.DoesNotContain(run.Result.Items, static i => i.Subject?.Id.StartsWith("pagefile-", StringComparison.Ordinal) == true);

        // The GPU's status and the drives' health come from WMI too.
        var status = Row(run, "system.gpu.device_status", "gpu-1");
        Assert.Equal(ItemVerdict.Unavailable, status.Verdict);
        Assert.Equal(CheckReason.Denied, status.Reason);
        Assert.All(RowsOf(run, "system.storage.health"), static h =>
        {
            Assert.Equal(ItemVerdict.Unavailable, h.Verdict);
            Assert.Equal(CheckReason.Denied, h.Reason);
            Assert.Equal(CheckSource.Wmi, h.Source);
        });
        Assert.Equal(2, RowsOf(run, "system.storage.health").Count());

        // What Core read and the event log said is as it was.
        Assert.Equal(CheckValue.Int(2), Row(run, "system.memory.modules").Value);
        Assert.Equal(ItemVerdict.Ok, Row(run, "system.memory.modules").Verdict);
        Assert.Equal(ItemVerdict.Ok, Row(run, "system.os.uptime").Verdict);
        Assert.All(EventRows(run), static e => Assert.Equal(CheckValue.Int(0), e.Value));
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    [Fact]
    public async Task When_the_provider_is_late_the_rows_of_that_part_are_timeouts_and_the_late_answer_is_thrown_away()
    {
        var clock = new ManualTimeProvider();
        using var rig = Rig(clock);
        var late = new TaskCompletionSource<HardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hardware.Then(HardwareCategory.Memory, _ => late.Task);

        var task = Run(rig);
        Assert.True(QueryTest.Eventually(() => rig.Hardware.Asked.Count == 5), "the provider was not asked for all five categories");
        await Task.Delay(100); // real time: the four categories that answer at once have answered before the clock moves
        Assert.True(QueryTest.Eventually(() =>
        {
            clock.Advance(OneSecond);
            return task.IsCompleted;
        }), "the run did not end although the clock ran on");
        var run = await task;
        var before = Kit.Bytes(run.Result);

        var modules = Row(run, "system.memory.modules");
        Assert.Equal(ItemVerdict.Unavailable, modules.Verdict);
        Assert.Equal(CheckReason.Timeout, modules.Reason);
        Assert.Null(modules.Value);
        Assert.Equal(CheckSource.Smbios, modules.Source);
        Assert.DoesNotContain(run.Result.Items, static i => i.Subject?.Id.StartsWith("ram-", StringComparison.Ordinal) == true);
        Assert.Equal(CheckValue.Int(2), Row(run, "system.storage.devices").Value); // the other categories came
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.Empty(CheckResultValidator.Validate(run.Result));

        // The answer comes at last: the result, and the file, are not changed by it.
        late.SetResult(Hw.Machine());
        await Task.Delay(50);
        Assert.Equal(before, Kit.Bytes(run.Result));
        Assert.Equal(before, Kit.Bytes((await rig.ReadBackAsync(run.Result.RunId))!));
        Assert.Single(rig.History.Saved);
    }

    [Fact]
    public async Task When_no_category_of_the_hardware_comes_the_run_still_completes_with_unavailable_rows()
    {
        var run = await RunWith(static rig =>
        {
            foreach (var category in new[] { HardwareCategory.Os, HardwareCategory.Cpu, HardwareCategory.Memory, HardwareCategory.Gpu, HardwareCategory.Storage })
            {
                rig.Hardware.Throws(category, new InvalidOperationException("the provider is broken"));
            }
        });

        Assert.Equal(CheckOutcome.Completed, run.Result.Outcome);
        Assert.True(run.IsSaved);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
        Assert.Equal(CheckVerdict.Unavailable, run.Result.Verdict);
        foreach (var id in new[] { "system.os.version", "system.cpu.model", "system.memory.modules" })
        {
            var row = Row(run, id);
            Assert.Equal(CheckReason.Error, row.Reason);
            Assert.Null(row.Value);
        }

        // No list, no child rows; what WMI and the event log said is still there.
        Assert.DoesNotContain(run.Result.Items, static i => i.Subject is not null && !i.Subject.Id.StartsWith("pagefile-", StringComparison.Ordinal));
        Assert.Equal(CheckValue.Int(1), Row(run, "system.pagefile.count").Value);
        Assert.Equal(11, EventRows(run).Count());
    }

    [Fact]
    public async Task When_a_drive_does_not_say_what_Core_thinks_it_is_its_health_is_unavailable_and_nothing_is_guessed()
    {
        var run = await RunWith(static rig =>
        {
            // WMI lists drive 0 with another serial than the one Core read for drive 0: the number is of one moment, so it is not the same disk.
            rig.Wmi.Rows("MSFT_PhysicalDisk", FakeWmiReader.Disk("0", "SOMEONE-ELSES-DISK", health: 2), FakeWmiReader.Disk("1", Hw.SerialOfDisk1));
        });

        var first = Row(run, "system.storage.health", "disk-1");
        Assert.Equal(ItemVerdict.Unavailable, first.Verdict);
        Assert.Null(first.Value);
        Assert.NotNull(first.Reason);
        Assert.Equal(ItemVerdict.Ok, Row(run, "system.storage.health", "disk-2").Verdict);
        Assert.Empty(CheckResultValidator.Validate(run.Result));
    }

    // ---- the verdict of the whole (DESIGN-A 4.0: check, then caution, then unavailable, then ok; info never counts) ----

    private static void Thirty1DaysUp(RunRig rig) => rig.Hardware.Machine = Hw.Machine(os: Hw.Os(uptimeSec: 2_592_001));

    private static void GpuWithError(RunRig rig, uint code = 43) =>
        rig.Wmi.Rows(
            "Win32_VideoController",
            FakeWmiReader.Row(("PNPDeviceID", @"PCI\VEN_10DE&DEV_2704&SUBSYS_00000000&REV_A1\4&1&0&0019"), ("ConfigManagerErrorCode", code)));

    private static void PageFilesDenied(RunRig rig)
    {
        rig.Wmi.Fails("Win32_PageFileUsage", CheckReason.Denied);
        rig.Wmi.Fails("Win32_ComputerSystem", CheckReason.Denied);
    }

    [Fact]
    public async Task A_caution_row_makes_the_verdict_caution()
    {
        var run = await RunWith(Thirty1DaysUp);

        Assert.Equal(ItemVerdict.Caution, Row(run, "system.os.uptime").Verdict);
        Assert.Equal(CheckVerdict.Caution, run.Result.Verdict);
        Assert.Equal(1, run.Result.Counts.Caution);
    }

    [Fact]
    public async Task A_check_row_makes_the_verdict_check()
    {
        var run = await RunWith(static rig => GpuWithError(rig));

        var status = Row(run, "system.gpu.device_status", "gpu-1");
        Assert.Equal(ItemVerdict.Check, status.Verdict);
        Assert.Equal(CheckValue.Int(43), status.Value);
        Assert.Equal(CheckEvidenceCode.DeviceError, status.Evidence!.Code);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
        Assert.Equal(1, run.Result.Counts.Check);
        Assert.Empty(CheckResultValidator.Validate(run.Result)); // the driver rows of that GPU are info now, and the validator agrees
        Assert.Equal(ItemVerdict.Info, Row(run, "system.gpu.driver_age", "gpu-1").Verdict);
    }

    [Fact]
    public async Task Check_is_above_caution_and_caution_is_above_unavailable()
    {
        var checkAndCaution = await RunWith(static rig =>
        {
            Thirty1DaysUp(rig);
            GpuWithError(rig);
        });
        var cautionAndUnavailable = await RunWith(static rig =>
        {
            Thirty1DaysUp(rig);
            PageFilesDenied(rig);
        });
        var checkAndUnavailable = await RunWith(static rig =>
        {
            GpuWithError(rig);
            PageFilesDenied(rig);
        });

        Assert.Equal(CheckVerdict.Check, checkAndCaution.Result.Verdict);
        Assert.Equal(CheckVerdict.Caution, cautionAndUnavailable.Result.Verdict);
        Assert.True(cautionAndUnavailable.Result.Counts.Unavailable > 0);
        Assert.Equal(CheckVerdict.Check, checkAndUnavailable.Result.Verdict);
    }

    [Fact]
    public async Task A_code_22_is_not_a_fault_and_the_verdict_stays_ok()
    {
        var run = await RunWith(static rig => GpuWithError(rig, 22));

        var status = Row(run, "system.gpu.device_status", "gpu-1");
        Assert.Equal(ItemVerdict.Ok, status.Verdict);
        Assert.Equal(CheckEvidenceCode.DisabledCode22, status.Evidence!.Code);
        Assert.Equal(CheckVerdict.Ok, run.Result.Verdict);
    }

    [Fact]
    public async Task A_drive_that_Windows_calls_unhealthy_is_a_check_for_that_drive_only()
    {
        var run = await RunWith(static rig =>
            rig.Wmi.Rows("MSFT_PhysicalDisk", FakeWmiReader.Disk("0", Hw.SerialOfDisk0, health: 2), FakeWmiReader.Disk("1", Hw.SerialOfDisk1)));

        var first = Row(run, "system.storage.health", "disk-1");
        Assert.Equal(ItemVerdict.Check, first.Verdict);
        Assert.Equal(CheckValue.Text("critical"), first.Value);
        Assert.Equal(ItemVerdict.Ok, Row(run, "system.storage.health", "disk-2").Verdict);
        Assert.Equal(CheckVerdict.Check, run.Result.Verdict);
    }

    [Fact]
    public async Task A_full_volume_is_a_check_and_a_tight_one_a_caution()
    {
        var full = await RunWith(static rig => rig.Hardware.Machine = Hw.Machine(storage:
        [
            Hw.Disk(0, "Samsung SSD 980 PRO 2TB", Hw.SerialOfDisk0, Hw.Volume("C:", 2_000_000_000_000, 20_000_000_000)),
            Hw.Disk(1, "WDC WD40EZRZ-00GXCB0", Hw.SerialOfDisk1, Hw.Volume("D:", 4_000_000_000_000, 400_000_000_000)),
        ]));

        Assert.Equal(ItemVerdict.Check, Row(full, "system.storage.free", "volume-1").Verdict); // 1% free
        Assert.Equal(ItemVerdict.Ok, Row(full, "system.storage.free", "volume-2").Verdict); // exactly 10% free is not under 10%
        Assert.Equal(CheckVerdict.Check, full.Result.Verdict);
    }
}
