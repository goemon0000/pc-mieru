using PcMieru.Checks.SystemInfo;
using PcMieru.Core;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// Fault injection for the three groups of questions of the system check (DESIGN-A 6.1, 14.1), one group at a time and on a clock the test
/// moves by hand: what does a question that fails, hangs or answers late do to the rest? The answer is always "only its own rows". Nothing
/// here calls Windows, reads the machine or waits for a real deadline.
/// </summary>
public sealed class SystemFaultTests
{
    private static readonly DateTime Window = EventCountQuery.WindowStartFor(Hw.Now);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static string IdOf(EventCountKind kind) => EventCountQuery.Create(kind, Window).ItemId;

    private static IReadOnlyList<string> AllIds() => [.. EventCountQuery.AllKinds.Select(IdOf)];

    private static Task<IReadOnlyDictionary<string, CheckReading<long>>> Events(FakeEventCounter counter, ManualTimeProvider clock, CancellationToken ct = default) =>
        SystemCheckProcedure.ReadEventsAsync(counter, Window, clock, ct);

    /// <summary>Moves the clock a second at a time until the task is over (a timer that is made late is still met), and gives its result.</summary>
    private static async Task<T> RunOutTheClock<T>(Task<T> task, ManualTimeProvider clock)
    {
        Assert.True(QueryTest.Eventually(() =>
        {
            clock.Advance(OneSecond);
            return task.IsCompleted;
        }), "the task did not end although the clock ran on");
        return await task;
    }

    /// <summary>
    /// Waits until the provider has been asked for all five categories, and then a moment of real time, so that the categories that answer at
    /// once have really answered before the test moves the clock (a timer that is already made could otherwise meet a call a few
    /// microseconds from its end).
    /// </summary>
    private static async Task Settle(FakeHardware hardware)
    {
        Assert.True(QueryTest.Eventually(() => hardware.Asked.Count == 5), "the provider was not asked for all five categories");
        await Task.Delay(100);
    }

    // ================= the event log: eleven counts, one shared deadline =================

    [Fact]
    public async Task The_eleven_counts_are_asked_one_at_a_time_in_the_order_of_the_kinds_over_one_window_and_one_deadline_of_five_seconds()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter();
        var inFlight = 0;
        var mostInFlight = 0;
        var gate = new object();
        foreach (var id in AllIds())
        {
            counter.Then(id, async (_, _, _) =>
            {
                lock (gate)
                {
                    inFlight++;
                    mostInFlight = Math.Max(mostInFlight, inFlight);
                }

                await Task.Yield();
                lock (gate)
                {
                    inFlight--;
                }

                return CheckReading<long>.Ok(0);
            });
        }

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(11, AllIds().Count);
        Assert.Equal(AllIds(), counter.Asked);
        Assert.Equal(1, mostInFlight);
        Assert.Equal(AllIds().Order(StringComparer.Ordinal), counts.Keys.Order(StringComparer.Ordinal));
        Assert.All(counts.Values, static r =>
        {
            Assert.True(r.HasValue);
            Assert.Equal(0, r.Value);
        });
        Assert.All(counter.Calls, static c => Assert.Equal(Window, c.Query.WindowStartUtc));
        Assert.All(counter.Calls, static c => Assert.Equal(TimeSpan.FromSeconds(5), c.Deadline.Duration));
        Assert.Single(counter.Calls.Select(static c => c.Deadline).Distinct()); // the SAME deadline, not eleven of five seconds each
    }

    [Fact]
    public async Task The_counts_are_read_as_the_counter_gave_them()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter()
            .Count(IdOf(EventCountKind.Whea), 2)
            .Count(IdOf(EventCountKind.Disk), 11)
            .Count(IdOf(EventCountKind.KernelPower41), 0);

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(2, counts[IdOf(EventCountKind.Whea)].Value);
        Assert.Equal(11, counts[IdOf(EventCountKind.Disk)].Value);
        Assert.Equal(0, counts[IdOf(EventCountKind.KernelPower41)].Value);
    }

    [Fact]
    public async Task One_count_that_times_out_is_only_its_own_reading_and_the_rest_are_still_asked()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter().Fails(IdOf(EventCountKind.Disk), CheckReason.Timeout);

        var counts = await QueryTest.Done(Events(counter, clock));

        var bad = counts[IdOf(EventCountKind.Disk)];
        Assert.False(bad.HasValue);
        Assert.Equal(CheckReason.Timeout, bad.Reason);
        Assert.Equal(10, counts.Count(static kv => kv.Value.HasValue));
        Assert.Equal(AllIds(), counter.Asked);
    }

    [Fact]
    public async Task A_count_that_throws_is_an_error_for_itself_and_the_rest_are_still_asked()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter().Throws(IdOf(EventCountKind.Ntfs), new InvalidOperationException("the log is broken"));

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(CheckReason.Error, counts[IdOf(EventCountKind.Ntfs)].Reason);
        Assert.Equal(10, counts.Count(static kv => kv.Value.HasValue));
        Assert.Equal(AllIds(), counter.Asked);
    }

    [Fact]
    public async Task A_count_that_ends_with_a_cancel_that_is_not_the_callers_is_an_error_for_itself()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter().Throws(IdOf(EventCountKind.Volmgr), new OperationCanceledException());

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(CheckReason.Error, counts[IdOf(EventCountKind.Volmgr)].Reason);
        Assert.Equal(AllIds(), counter.Asked);
    }

    [Theory]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Unsupported)]
    [InlineData(CheckReason.Error)]
    public async Task The_reason_of_a_count_that_did_not_come_is_kept(CheckReason reason)
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter().Fails(IdOf(EventCountKind.Whea), reason);

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(reason, counts[IdOf(EventCountKind.Whea)].Reason);
    }

    [Fact]
    public async Task When_the_shared_deadline_is_gone_the_counts_after_it_are_timeouts_and_are_not_even_asked()
    {
        var clock = new ManualTimeProvider();
        var kinds = EventCountQuery.AllKinds.ToList();
        var stuck = EventCountKind.Ntfs;
        var cut = kinds.IndexOf(stuck);
        var counter = new FakeEventCounter().Then(IdOf(stuck), (_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(5)); // this count used all of the five seconds and gave up
            return Task.FromResult(CheckReading<long>.TimedOut());
        });

        var counts = await QueryTest.Done(Events(counter, clock));

        // Asked: the ones before it and itself; not asked: the six after it.
        Assert.Equal(cut + 1, counter.Asked.Count);
        Assert.Equal(kinds.Take(cut + 1).Select(IdOf), counter.Asked);
        Assert.Equal(11, counts.Count);
        foreach (var kind in kinds.Take(cut))
        {
            Assert.Equal(0, counts[IdOf(kind)].Value);
        }

        foreach (var kind in kinds.Skip(cut))
        {
            Assert.False(counts[IdOf(kind)].HasValue);
            Assert.Equal(CheckReason.Timeout, counts[IdOf(kind)].Reason);
        }
    }

    [Fact]
    public async Task What_a_count_gave_in_time_stands_even_when_it_used_the_last_of_the_deadline()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter().Then(IdOf(EventCountKind.Disk), (_, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return Task.FromResult(CheckReading<long>.Ok(3)); // the reader answered; the deadline is the reader's to keep, not the check's to second-guess
        });

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(3, counts[IdOf(EventCountKind.Disk)].Value);
        Assert.Equal(CheckReason.Timeout, counts[IdOf(EventCountKind.DiskWarning)].Reason);
        Assert.Equal(CheckReason.Timeout, counts[IdOf(EventCountKind.KernelPower41)].Reason);
        Assert.Equal(AllIds().Take(3), counter.Asked); // whea, whea warning, disk, and nothing after it
    }

    [Fact]
    public async Task The_deadline_of_the_counts_is_the_monotonic_clock_and_not_the_wall_clock()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter().Then(IdOf(EventCountKind.Whea), (_, _, _) =>
        {
            clock.Wall = clock.Wall.AddDays(30); // the user sets the date a month ahead in the middle of the run
            return Task.FromResult(CheckReading<long>.Ok(0));
        });

        var counts = await QueryTest.Done(Events(counter, clock));

        Assert.Equal(11, counts.Count(static kv => kv.Value.HasValue));
        Assert.Equal(AllIds(), counter.Asked);
    }

    [Fact]
    public async Task A_stop_by_the_caller_in_the_middle_of_the_counts_is_passed_on_and_the_rest_are_not_asked()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var counter = new FakeEventCounter().Then(IdOf(EventCountKind.Disk), (_, _, _) =>
        {
            cts.Cancel();
            return Task.FromCanceled<CheckReading<long>>(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Events(counter, clock, cts.Token));

        Assert.Equal(AllIds().Take(3), counter.Asked);
    }

    [Fact]
    public async Task A_token_that_is_already_stopped_asks_nothing()
    {
        var clock = new ManualTimeProvider();
        var counter = new FakeEventCounter();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Events(counter, clock, cts.Token));

        Assert.Empty(counter.Asked);
    }

    [Fact]
    public async Task The_counts_refuse_what_is_missing()
    {
        var clock = new ManualTimeProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(() => SystemCheckProcedure.ReadEventsAsync(null!, Window, clock, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SystemCheckProcedure.ReadEventsAsync(new FakeEventCounter(), Window, null!, CancellationToken.None));
    }

    // ================= the hardware of Core: five categories, one 10 s deadline =================

    [Fact]
    public async Task Every_category_is_asked_once_and_read_from_the_machine()
    {
        var clock = new ManualTimeProvider();
        var hardware = new FakeHardware();

        var facts = await QueryTest.Done(HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None));

        Assert.Equal(
            new[] { HardwareCategory.Cpu, HardwareCategory.Memory, HardwareCategory.Gpu, HardwareCategory.Storage, HardwareCategory.Os },
            hardware.Asked.Order()); // the order of the enum, not the order they were asked in (that is up to the thread pool)
        Assert.True(facts.Os.HasValue);
        Assert.True(facts.Cpu.HasValue);
        Assert.True(facts.Memory.HasValue);
        Assert.True(facts.Gpus.HasValue);
        Assert.True(facts.Storage.HasValue);
        Assert.Equal(2, facts.Storage.Value.Count);
        Assert.Equal(2, facts.Memory.Value.Modules.Count);
    }

    [Fact]
    public async Task A_category_that_throws_is_an_error_for_itself_and_the_others_are_read()
    {
        var clock = new ManualTimeProvider();
        var hardware = new FakeHardware().Throws(HardwareCategory.Gpu, new InvalidOperationException("the adapter list broke"));

        var facts = await QueryTest.Done(HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None));

        Assert.Equal(CheckReason.Error, facts.Gpus.Reason);
        Assert.True(facts.Os.HasValue);
        Assert.True(facts.Cpu.HasValue);
        Assert.True(facts.Memory.HasValue);
        Assert.True(facts.Storage.HasValue);
    }

    [Fact]
    public async Task A_category_that_throws_before_it_returns_a_task_is_the_same()
    {
        var clock = new ManualTimeProvider();
        var hardware = new FakeHardware().Then(HardwareCategory.Memory, _ => throw new InvalidOperationException("thrown on the calling thread"));

        var facts = await QueryTest.Done(HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None));

        Assert.Equal(CheckReason.Error, facts.Memory.Reason);
        Assert.True(facts.Os.HasValue);
    }

    [Fact]
    public async Task A_category_that_ends_with_a_cancel_that_is_not_the_callers_is_an_error_for_itself()
    {
        var clock = new ManualTimeProvider();
        var hardware = new FakeHardware().Throws(HardwareCategory.Cpu, new OperationCanceledException());

        var facts = await QueryTest.Done(HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None));

        Assert.Equal(CheckReason.Error, facts.Cpu.Reason);
        Assert.True(facts.Os.HasValue);
    }

    [Fact]
    public async Task A_category_that_Core_could_not_read_has_the_reason_Core_gave()
    {
        var clock = new ManualTimeProvider();
        var machine = Hw.Machine() with
        {
            Memory = Value.Denied<MemoryHardware>("test"),
            Gpus = Value.Unsupported<IReadOnlyList<GpuHardware>>("test"),
            Os = Value.Failed<OsHardware>("test"),
        };
        var hardware = new FakeHardware(machine);

        var facts = await QueryTest.Done(HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None));

        Assert.Equal(CheckReason.Denied, facts.Memory.Reason);
        Assert.Equal(CheckReason.Unsupported, facts.Gpus.Reason);
        Assert.Equal(CheckReason.Error, facts.Os.Reason);
        Assert.True(facts.Cpu.HasValue);
        Assert.True(facts.Storage.HasValue);
    }

    [Fact]
    public async Task A_category_that_has_not_answered_in_ten_seconds_is_a_timeout_and_its_late_answer_is_thrown_away()
    {
        var clock = new ManualTimeProvider();
        var late = new TaskCompletionSource<HardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSeen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = new FakeHardware().Then(HardwareCategory.Memory, ct =>
        {
            stopSeen.TrySetResult(ct);
            return late.Task;
        });

        var read = HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None);
        await Settle(hardware);
        var facts = await RunOutTheClock(read, clock);

        Assert.False(facts.Memory.HasValue);
        Assert.Equal(CheckReason.Timeout, facts.Memory.Reason);
        Assert.True(facts.Os.HasValue);
        Assert.True(facts.Cpu.HasValue);
        Assert.True(facts.Gpus.HasValue);
        Assert.True(facts.Storage.HasValue);
        Assert.True((await QueryTest.Done(stopSeen.Task)).IsCancellationRequested, "the provider was not asked to stop the call that ran out of time");

        // The answer comes at last: it is not used, and nothing throws.
        late.SetResult(Hw.Machine());
        Assert.False(facts.Memory.HasValue);
        Assert.Equal(CheckReason.Timeout, facts.Memory.Reason);
    }

    [Fact]
    public async Task A_category_that_fails_after_it_was_given_up_changes_nothing()
    {
        var clock = new ManualTimeProvider();
        var late = new TaskCompletionSource<HardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = new FakeHardware().Then(HardwareCategory.Storage, _ => late.Task);

        var read = HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None);
        await Settle(hardware);
        var facts = await RunOutTheClock(read, clock);
        late.SetException(new InvalidOperationException("late and broken"));
        await Task.Delay(50); // the continuation that looks at the failure has run

        Assert.Equal(CheckReason.Timeout, facts.Storage.Reason);
        Assert.True(facts.Os.HasValue);
    }

    [Fact]
    public async Task The_ten_seconds_are_one_deadline_for_all_the_categories_and_not_ten_seconds_each()
    {
        var clock = new ManualTimeProvider();
        var late = new TaskCompletionSource<HardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = new FakeHardware()
            .Then(HardwareCategory.Memory, _ => late.Task)
            .Then(HardwareCategory.Gpu, _ => late.Task);

        var read = HardwareFacts.ReadAsync(hardware, clock, CancellationToken.None);
        await Settle(hardware);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(50);
        Assert.False(read.IsCompleted); // nine seconds are not enough for the two that wait
        var facts = await RunOutTheClock(read, clock);

        Assert.Equal(CheckReason.Timeout, facts.Memory.Reason);
        Assert.Equal(CheckReason.Timeout, facts.Gpus.Reason);
        Assert.True(facts.Os.HasValue);
        Assert.Equal(TimeSpan.FromSeconds(10), HardwareFacts.Budget);
    }

    [Fact]
    public async Task A_stop_by_the_caller_is_passed_on_and_the_call_that_was_running_is_asked_to_stop()
    {
        var clock = new ManualTimeProvider();
        var never = new TaskCompletionSource<HardwareInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSeen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = new FakeHardware().Then(HardwareCategory.Gpu, ct =>
        {
            stopSeen.TrySetResult(ct);
            return never.Task;
        });
        using var cts = new CancellationTokenSource();

        var read = HardwareFacts.ReadAsync(hardware, clock, cts.Token);
        await Settle(hardware);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(QueryTest.Patience));
        Assert.True((await QueryTest.Done(stopSeen.Task)).IsCancellationRequested);
    }

    [Fact]
    public async Task The_hardware_refuses_what_is_missing()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => HardwareFacts.ReadAsync(null!, new ManualTimeProvider(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => HardwareFacts.ReadAsync(new FakeHardware(), null!, CancellationToken.None));
    }

    [Theory]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Error)]
    public void Nothing_came_is_every_category_with_the_one_reason(CheckReason reason)
    {
        var facts = HardwareFacts.Missing(reason);

        Assert.Equal(reason, facts.Os.Reason);
        Assert.Equal(reason, facts.Cpu.Reason);
        Assert.Equal(reason, facts.Memory.Reason);
        Assert.Equal(reason, facts.Gpus.Reason);
        Assert.Equal(reason, facts.Storage.Reason);
    }

    // ================= the WMI questions: four, one after the other =================

    [Fact]
    public async Task The_four_questions_are_asked_once_each_in_a_fixed_order_in_their_scopes()
    {
        var clock = new ManualTimeProvider();
        var wmi = FakeWmiReader.Typical(FakeWmiReader.MachineDisks());

        var facts = await QueryTest.Done(SystemWmiFacts.ReadAsync(wmi, clock, CancellationToken.None));

        Assert.Equal(["Win32_PageFileUsage", "Win32_ComputerSystem", "Win32_VideoController", "MSFT_PhysicalDisk"], wmi.Asked);
        Assert.Equal([WmiScopes.Cimv2, WmiScopes.Cimv2, WmiScopes.Cimv2, WmiScopes.Storage], wmi.Calls.Select(static c => c.Scope));
        Assert.True(facts.PageFiles.HasValue);
        Assert.True(facts.PageFileAutomatic.HasValue);
        Assert.True(facts.VideoControllers.HasValue);
        Assert.True(facts.DiskHealth.Of(0, Hw.SerialOfDisk0).HasValue);
        Assert.Equal(4096, facts.PageFiles.Value[0].AllocatedMb);
        Assert.True(facts.PageFileAutomatic.Value);
    }

    [Fact]
    public async Task The_two_page_file_questions_share_one_deadline_and_the_others_have_their_own()
    {
        var clock = new ManualTimeProvider();
        var wmi = FakeWmiReader.Typical(FakeWmiReader.MachineDisks())
            .Then("Win32_PageFileUsage", (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(2)); // the first question used two of the five seconds
                return Task.FromResult(WmiScript.Of([FakeWmiReader.Row(("Name", "p"), ("AllocatedBaseSize", 1u), ("CurrentUsage", 1u), ("PeakUsage", 1u))]));
            });

        await QueryTest.Done(SystemWmiFacts.ReadAsync(wmi, clock, CancellationToken.None));

        var calls = wmi.Calls;
        Assert.Same(calls[0].Deadline, calls[1].Deadline);
        Assert.Equal(TimeSpan.FromSeconds(3), calls[1].Deadline.Remaining);
        Assert.NotSame(calls[1].Deadline, calls[2].Deadline);
        Assert.Equal(TimeSpan.FromSeconds(5), calls[2].Deadline.Remaining);
        Assert.NotSame(calls[2].Deadline, calls[3].Deadline);
        Assert.Equal(TimeSpan.FromSeconds(5), calls[3].Deadline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), SystemWmiFacts.Budget);
    }

    [Theory]
    [InlineData("Win32_PageFileUsage")]
    [InlineData("Win32_ComputerSystem")]
    [InlineData("Win32_VideoController")]
    [InlineData("MSFT_PhysicalDisk")]
    public async Task One_question_that_is_denied_is_only_its_own_reading_and_the_others_are_still_asked(string denied)
    {
        var clock = new ManualTimeProvider();
        var wmi = FakeWmiReader.Typical(FakeWmiReader.MachineDisks()).Fails(denied, CheckReason.Denied);

        var facts = await QueryTest.Done(SystemWmiFacts.ReadAsync(wmi, clock, CancellationToken.None));

        Assert.Equal(4, wmi.Asked.Count);
        Assert.Equal(denied == "Win32_PageFileUsage" ? CheckReason.Denied : (CheckReason?)null, facts.PageFiles.Reason);
        Assert.Equal(denied == "Win32_ComputerSystem" ? CheckReason.Denied : (CheckReason?)null, facts.PageFileAutomatic.Reason);
        Assert.Equal(denied == "Win32_VideoController" ? CheckReason.Denied : (CheckReason?)null, facts.VideoControllers.Reason);
        var health = facts.DiskHealth.Of(0, Hw.SerialOfDisk0);
        Assert.Equal(denied == "MSFT_PhysicalDisk", !health.HasValue);
        Assert.Equal(denied == "MSFT_PhysicalDisk" ? CheckReason.Denied : (CheckReason?)null, health.Reason);
    }

    [Fact]
    public async Task A_question_that_times_out_is_a_timeout_and_one_that_throws_is_an_error()
    {
        var clock = new ManualTimeProvider();
        var wmi = FakeWmiReader.Typical(FakeWmiReader.MachineDisks())
            .Fails("Win32_PageFileUsage", CheckReason.Timeout)
            .Throws("Win32_VideoController", new InvalidOperationException("provider broke"));

        var facts = await QueryTest.Done(SystemWmiFacts.ReadAsync(wmi, clock, CancellationToken.None));

        Assert.Equal(CheckReason.Timeout, facts.PageFiles.Reason);
        Assert.Equal(CheckReason.Error, facts.VideoControllers.Reason);
        Assert.True(facts.PageFileAutomatic.HasValue);
        Assert.True(facts.DiskHealth.Of(0, Hw.SerialOfDisk0).HasValue);
        Assert.Equal(4, wmi.Asked.Count);
    }

    [Fact]
    public async Task Every_question_denied_is_four_readings_with_the_one_reason()
    {
        var clock = new ManualTimeProvider();
        var wmi = new FakeWmiReader()
            .Fails("Win32_PageFileUsage", CheckReason.Denied)
            .Fails("Win32_ComputerSystem", CheckReason.Denied)
            .Fails("Win32_VideoController", CheckReason.Denied)
            .Fails("MSFT_PhysicalDisk", CheckReason.Denied);

        var facts = await QueryTest.Done(SystemWmiFacts.ReadAsync(wmi, clock, CancellationToken.None));

        Assert.Equal(CheckReason.Denied, facts.PageFiles.Reason);
        Assert.Equal(CheckReason.Denied, facts.PageFileAutomatic.Reason);
        Assert.Equal(CheckReason.Denied, facts.VideoControllers.Reason);
        Assert.Equal(CheckReason.Denied, facts.DiskHealth.Of(0, null).Reason);
        Assert.Equal(CheckReason.Denied, facts.DiskHealth.Of(7, "anything").Reason);
    }

    [Fact]
    public async Task A_computer_system_that_does_not_say_one_value_is_an_error_for_the_automatic_flag_only()
    {
        var clock = new ManualTimeProvider();

        var none = FakeWmiReader.Typical(FakeWmiReader.MachineDisks()).Rows("Win32_ComputerSystem");
        var two = FakeWmiReader.Typical(FakeWmiReader.MachineDisks())
            .Rows("Win32_ComputerSystem", FakeWmiReader.Row(("AutomaticManagedPagefile", true)), FakeWmiReader.Row(("AutomaticManagedPagefile", false)));
        var empty = FakeWmiReader.Typical(FakeWmiReader.MachineDisks()).Rows("Win32_ComputerSystem", FakeWmiReader.Row());

        foreach (var wmi in new[] { none, two, empty })
        {
            var facts = await QueryTest.Done(SystemWmiFacts.ReadAsync(wmi, clock, CancellationToken.None));

            Assert.Equal(CheckReason.Error, facts.PageFileAutomatic.Reason);
            Assert.True(facts.PageFiles.HasValue);
            Assert.True(facts.VideoControllers.HasValue);
        }
    }

    [Fact]
    public async Task A_stop_by_the_caller_in_the_middle_of_the_questions_is_passed_on_and_the_rest_are_not_asked()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var wmi = FakeWmiReader.Typical(FakeWmiReader.MachineDisks()).Then("Win32_ComputerSystem", (_, _) =>
        {
            cts.Cancel();
            return Task.FromCanceled<WmiScript>(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemWmiFacts.ReadAsync(wmi, clock, cts.Token));

        Assert.Equal(["Win32_PageFileUsage", "Win32_ComputerSystem"], wmi.Asked);
    }

    [Fact]
    public async Task The_questions_refuse_what_is_missing()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => SystemWmiFacts.ReadAsync(null!, new ManualTimeProvider(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SystemWmiFacts.ReadAsync(new FakeWmiReader(), null!, CancellationToken.None));
    }

    [Theory]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Timeout)]
    public void Nothing_came_from_WMI_is_every_reading_with_the_one_reason(CheckReason reason)
    {
        var facts = SystemWmiFacts.Missing(reason);

        Assert.Equal(reason, facts.PageFiles.Reason);
        Assert.Equal(reason, facts.PageFileAutomatic.Reason);
        Assert.Equal(reason, facts.VideoControllers.Reason);
        Assert.Equal(reason, facts.DiskHealth.Of(0, null).Reason);
    }
}
