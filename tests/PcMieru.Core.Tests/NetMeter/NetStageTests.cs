using PcMieru.Core.Collection;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.NetMeter;

/// <summary>A meter whose state and readings the test writes. It starts and stops nothing.</summary>
internal sealed class ScriptedMeter : INetMeterControl, INetMeterReader
{
    public NetMeterState State { get; set; } = new(NetMeterStatus.Running);

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    /// <summary>The next reading; null makes the read fail.</summary>
    public Func<NetMeterReading?> Next { get; set; } = static () => null;

    public long Generation { get; set; } = 1;

    public int Reads;

    public Task<NetMeterState> StartAsync(CancellationToken ct = default) => Task.FromResult(State);

    public void Stop()
    {
    }

    public bool TryRead(TimeSpan timeout, out NetMeterReading? reading, out long generation)
    {
        Interlocked.Increment(ref Reads);
        reading = Next();
        generation = Generation;
        return reading is not null;
    }
}

/// <summary>
/// The network stage of <see cref="WindowsSnapshotSource"/>, on the real process list (so these are RealMachine like the other
/// source tests) but with a scripted meter: no ETW, no elevation, nothing started. The test only ever looks at its own process.
/// </summary>
[Trait("Category", "RealMachine")]
public sealed class NetStageTests
{
    private const long Second = 10_000_000;
    private static readonly uint Me = (uint)Environment.ProcessId;

    private static WindowsSnapshotSource NewSource(INetMeterControl? meter) =>
        new(static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false }, null, new SignatureVerifier(), () => meter);

    private static ProcessInfo Own(Snapshot snapshot) => snapshot.Processes.Single(p => p.Key.Pid == Me);

    private static NetMeterReading Reading(long seconds, ProcessKey key, ulong send, ulong receive, ulong lost = 0) =>
        new(seconds * Second, lost, 0, 0, true, 0, [new NetMeterEntry(key.Pid, key.CreateTime, send, receive)]);

    [Fact]
    public async Task Without_a_meter_every_process_says_the_meter_is_off()
    {
        using var source = NewSource(null);

        var snapshot = await source.SampleAsync();

        var off = NetMeterValues.Missing(new NetMeterState(NetMeterStatus.Off));
        Assert.All(snapshot.Processes, p =>
        {
            Assert.Equal(off, p.NetSendBytesPerSec);
            Assert.Equal(off, p.NetReceiveBytesPerSec);
        });
    }

    public static TheoryData<NetMeterStatus, string?> StatesWithoutNumbers() => new()
    {
        { NetMeterStatus.Off, null },
        { NetMeterStatus.Starting, null },
        { NetMeterStatus.Declined, null },
        { NetMeterStatus.Failed, "the plain words of why" },
        { NetMeterStatus.Unsupported, "not in the app folder" },
    };

    [Theory]
    [MemberData(nameof(StatesWithoutNumbers))]
    public async Task A_meter_that_is_not_Running_gives_every_process_the_value_for_that_state_and_is_not_read(NetMeterStatus status, string? detail)
    {
        var state = new NetMeterState(status, detail);
        var meter = new ScriptedMeter { State = state };
        using var source = NewSource(meter);

        var snapshot = await source.SampleAsync();

        var expected = NetMeterValues.Missing(state);
        Assert.All(snapshot.Processes, p =>
        {
            Assert.Equal(expected, p.NetSendBytesPerSec);
            Assert.Equal(expected, p.NetReceiveBytesPerSec);
        });
        Assert.Equal(0, meter.Reads);
        Assert.False(expected.TryGet(out _));
        if (detail is not null)
        {
            Assert.Equal(detail, expected.Detail);
        }
    }

    [Fact]
    public async Task A_Running_meter_gives_a_baseline_first_and_then_bytes_per_second_for_the_process_it_names()
    {
        var meter = new ScriptedMeter();
        using var source = NewSource(meter);
        ProcessKey? key = null;
        long seconds = 100;
        ulong send = 1_000;
        ulong receive = 5_000;
        meter.Next = () => Reading(seconds, key ?? new ProcessKey(Me, 1), send, receive);

        // The key of this process is only known from a sample; the first one has no entry that matches and is a baseline anyway.
        var first = await source.SampleAsync();
        key = Own(first).Key;
        Assert.Equal(ValueStatus.NotCollected, Own(first).NetSendBytesPerSec.Status);
        Assert.Contains("baseline", Own(first).NetSendBytesPerSec.Detail);

        // A baseline for the right key (its window against the first, wrong-key reading means nothing), then 2 s of the
        // meter's clock and 1,000 / 500 bytes more.
        seconds += 1;
        _ = await source.SampleAsync();
        seconds += 2;
        send += 1_000;
        receive += 500;
        var third = await source.SampleAsync();

        var own = Own(third);
        Assert.True(own.NetSendBytesPerSec.IsOk, own.NetSendBytesPerSec.Detail);
        Assert.Equal(500, own.NetSendBytesPerSec.Data, 6);
        Assert.Equal(250, own.NetReceiveBytesPerSec.Data, 6);
        Assert.Equal(NetMeterValues.Source, own.NetSendBytesPerSec.Source);
    }

    [Fact]
    public async Task A_second_sample_that_arrives_before_the_meter_has_newer_counters_repeats_the_rates_and_says_so()
    {
        var meter = new ScriptedMeter();
        using var source = NewSource(meter);
        ProcessKey key = default;
        long seconds = 100;
        ulong send = 0;
        meter.Next = () => Reading(seconds, key, send, 0);

        key = Own(await source.SampleAsync()).Key;
        seconds += 1;
        _ = await source.SampleAsync(); // baseline for the real key
        seconds += 1;
        send += 800;
        var advanced = Own(await source.SampleAsync());
        Assert.Equal(800, advanced.NetSendBytesPerSec.Data, 6);

        var repeated = Own(await source.SampleAsync()); // same meter timestamp

        Assert.True(repeated.NetSendBytesPerSec.IsOk);
        Assert.Equal(800, repeated.NetSendBytesPerSec.Data, 6);
        Assert.Equal(NetRateCalculator.DetailRepeated, repeated.NetSendBytesPerSec.Detail);
    }

    [Fact]
    public async Task A_new_meter_run_in_the_middle_is_a_new_baseline_not_a_comparison_with_the_old_counters()
    {
        var meter = new ScriptedMeter();
        using var source = NewSource(meter);
        ProcessKey key = default;
        long seconds = 100;
        ulong send = 9_000_000;
        meter.Next = () => Reading(seconds, key, send, 0);
        key = Own(await source.SampleAsync()).Key;
        seconds += 1;
        _ = await source.SampleAsync();

        meter.Generation = 2;
        seconds = 3;
        send = 10;
        var restarted = Own(await source.SampleAsync());

        Assert.Equal(ValueStatus.NotCollected, restarted.NetSendBytesPerSec.Status);
        Assert.Contains("baseline", restarted.NetSendBytesPerSec.Detail);
    }

    [Fact]
    public async Task A_baseline_request_drops_the_meter_baseline_too()
    {
        var meter = new ScriptedMeter();
        using var source = NewSource(meter);
        ProcessKey key = default;
        long seconds = 100;
        meter.Next = () => Reading(seconds, key, 0, 0);
        key = Own(await source.SampleAsync()).Key;
        seconds++;
        _ = await source.SampleAsync();
        seconds++;
        Assert.True(Own(await source.SampleAsync()).NetSendBytesPerSec.IsOk);

        source.ResetBaseline();
        seconds++;
        var afterReset = Own(await source.SampleAsync());

        Assert.Equal(ValueStatus.NotCollected, afterReset.NetSendBytesPerSec.Status);
    }

    [Fact]
    public async Task Events_Windows_dropped_make_the_rates_an_error_in_that_sample_only()
    {
        var meter = new ScriptedMeter();
        using var source = NewSource(meter);
        ProcessKey key = default;
        long seconds = 100;
        ulong lost = 0;
        ulong send = 0;
        meter.Next = () => Reading(seconds, key, send, 0, lost);
        key = Own(await source.SampleAsync()).Key;
        seconds++;
        _ = await source.SampleAsync();

        seconds++;
        lost = 6;
        send += 1_000;
        var dropped = Own(await source.SampleAsync());

        Assert.Equal(ValueStatus.Error, dropped.NetSendBytesPerSec.Status);
        Assert.Contains("6", dropped.NetSendBytesPerSec.Detail);

        seconds++;
        send += 1_000;
        var next = Own(await source.SampleAsync());
        Assert.True(next.NetSendBytesPerSec.IsOk, next.NetSendBytesPerSec.Detail);
        Assert.Equal(1_000, next.NetSendBytesPerSec.Data, 6);
    }

    [Fact]
    public async Task A_read_that_fails_after_the_control_has_moved_to_Failed_gives_its_words_to_every_process()
    {
        var meter = new ScriptedMeter();
        meter.Next = () =>
        {
            meter.State = new NetMeterState(NetMeterStatus.Failed, "the connection to the network meter was lost");
            return null;
        };
        using var source = NewSource(meter);

        var snapshot = await source.SampleAsync();

        Assert.All(snapshot.Processes, p =>
        {
            Assert.Equal(ValueStatus.Error, p.NetSendBytesPerSec.Status);
            Assert.Equal("the connection to the network meter was lost", p.NetSendBytesPerSec.Detail);
        });
    }

    [Fact]
    public async Task A_read_that_fails_while_the_control_still_says_Running_is_an_error_with_the_general_words()
    {
        var meter = new ScriptedMeter();
        using var source = NewSource(meter);

        var snapshot = await source.SampleAsync();

        Assert.All(snapshot.Processes, p =>
        {
            Assert.Equal(ValueStatus.Error, p.NetSendBytesPerSec.Status);
            Assert.Equal(NetMeterValues.FailedDetail, p.NetSendBytesPerSec.Detail);
        });
    }

    [Fact]
    public async Task A_control_that_cannot_be_read_from_is_Unsupported_and_never_a_number()
    {
        using var source = NewSource(new FakeNetMeterControl());

        var snapshot = await source.SampleAsync();

        Assert.All(snapshot.Processes, p =>
        {
            Assert.Equal(ValueStatus.Unsupported, p.NetSendBytesPerSec.Status);
            Assert.Equal(NetMeterValues.UnsupportedDetail, p.NetReceiveBytesPerSec.Detail);
        });
    }

    [Fact]
    public async Task A_meter_that_is_replaced_between_samples_is_the_one_read_next()
    {
        var first = new ScriptedMeter { State = new NetMeterState(NetMeterStatus.Declined) };
        var second = new ScriptedMeter { State = new NetMeterState(NetMeterStatus.Starting) };
        INetMeterControl current = first;
        using var source = new WindowsSnapshotSource(
            static () => DateTime.UtcNow, new SamplerSettings { GpuDefaultOn = false }, null, new SignatureVerifier(), () => current);

        var a = Own(await source.SampleAsync());
        current = second;
        var b = Own(await source.SampleAsync());

        Assert.Equal(ValueStatus.Denied, a.NetSendBytesPerSec.Status);
        Assert.Equal(NetMeterValues.StartingDetail, b.NetSendBytesPerSec.Detail);
    }
}
