using System.ComponentModel;
using System.Threading.Channels;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>A SensorHost stand-in: lines are pushed by the test, and Kill/CloseInput/Dispose are counted.</summary>
internal sealed class FakeSensorProcess : ISensorProcess
{
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
    private readonly List<int> _periods = [];
    private int _killed;
    private int _killEntered;
    private int _inputClosed;
    private int _disposed;

    /// <summary>When false, killing the process does not end its output (a hung host whose late line still arrives).</summary>
    public bool KillEndsOutput { get; set; } = true;

    public bool ExitsOnCloseInput { get; set; } = true;

    public bool HasExited { get; set; }

    public int? ExitCode { get; set; }

    public string StderrTail { get; set; } = string.Empty;

    public int KillCount => Volatile.Read(ref _killed);

    public int CloseInputCount => Volatile.Read(ref _inputClosed);

    public bool IsDisposed => Volatile.Read(ref _disposed) > 0;

    /// <summary>When set, <see cref="Kill"/> blocks until the gate is opened (a kill that takes its time).</summary>
    public ManualResetEventSlim? KillGate { get; set; }

    /// <summary>True once <see cref="Kill"/> has been called (it may still be blocked on <see cref="KillGate"/>).</summary>
    public bool KillEntered => Volatile.Read(ref _killEntered) != 0;

    public void Push(string line) => _lines.Writer.TryWrite(line);

    public void EndOutput() => _lines.Writer.TryComplete();

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (await _lines.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_lines.Reader.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }

    public void CloseInput() => Interlocked.Increment(ref _inputClosed);

    /// <summary>Every period the client asked this process for after it was started, in order (the start period is on the factory).</summary>
    public IReadOnlyList<int> Periods
    {
        get
        {
            lock (_periods)
            {
                return [.. _periods];
            }
        }
    }

    public void SetPeriod(int periodMs)
    {
        lock (_periods)
        {
            _periods.Add(periodMs);
        }
    }

    public bool WaitForExit(int milliseconds) => ExitsOnCloseInput;

    public void Kill()
    {
        Volatile.Write(ref _killEntered, 1);
        KillGate?.Wait();
        Interlocked.Increment(ref _killed);
        HasExited = true;
        if (KillEndsOutput)
        {
            EndOutput();
        }
    }

    public void Dispose() => Interlocked.Increment(ref _disposed);
}

internal sealed class FakeSensorProcessFactory : ISensorProcessFactory
{
    private readonly List<FakeSensorProcess> _started = [];
    private readonly List<(int IntervalMs, long Generation)> _launches = [];
    private readonly Queue<Exception> _failures = new();
    private int _startEntered;

    public IReadOnlyList<FakeSensorProcess> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public int StartAttempts { get; private set; }

    /// <summary>When set, <see cref="Start"/> blocks until the gate is opened (a launch that takes its time).</summary>
    public ManualResetEventSlim? StartGate { get; set; }

    /// <summary>True once <see cref="Start"/> has been called (it may still be blocked on <see cref="StartGate"/>).</summary>
    public bool StartEntered => Volatile.Read(ref _startEntered) != 0;

    public FakeSensorProcess Last => Started[^1];

    /// <summary>The <c>--interval-ms</c> each launch (failed ones too) was asked to start with, and the <c>--generation</c> it was given.</summary>
    public IReadOnlyList<(int IntervalMs, long Generation)> Launches
    {
        get
        {
            lock (_started)
            {
                return [.. _launches];
            }
        }
    }

    public void FailNext(Exception ex) => _failures.Enqueue(ex);

    public ISensorProcess Start(int intervalMs, long generation)
    {
        StartAttempts++;
        lock (_started)
        {
            _launches.Add((intervalMs, generation));
        }

        Volatile.Write(ref _startEntered, 1);
        StartGate?.Wait();
        if (_failures.TryDequeue(out var ex))
        {
            throw ex;
        }

        var process = new FakeSensorProcess();
        lock (_started)
        {
            _started.Add(process);
        }

        return process;
    }
}

public class SensorHostClientTests : IDisposable
{
    private readonly FakeSensorProcessFactory _factory = new();
    private readonly List<SensorHostClient> _clients = [];
    private long _now = 1_000_000;

    public void Dispose()
    {
        foreach (var c in _clients)
        {
            c.Dispose();
        }
    }

    private SensorHostClient NewClient(TimeSpan? firstReadingWait = null, TimeSpan? idleStopAfter = null)
    {
        var options = new SensorHostOptions
        {
            StaleAfter = TimeSpan.FromSeconds(6),
            FirstReadingWait = firstReadingWait ?? TimeSpan.FromSeconds(10),
            BackoffStart = TimeSpan.FromSeconds(1),
            BackoffMax = TimeSpan.FromSeconds(30),
            IdleStopAfter = idleStopAfter ?? TimeSpan.FromSeconds(60),
            WatchdogInterval = TimeSpan.Zero,
        };
        var client = new SensorHostClient(_factory, options, () => Interlocked.Read(ref _now));
        _clients.Add(client);
        return client;
    }

    private void Advance(double seconds) => Interlocked.Add(ref _now, (long)(seconds * 1000));

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("waited for: " + what);
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Asks for a reading, waits for the process to be launched, answers with one frame and returns what the read gave.</summary>
    private async Task<Value<IReadOnlyList<GpuSensorReading>>> FirstFrame(SensorHostClient client, string id = "luid_a", double temp = 40)
    {
        var started = _factory.Started.Count;
        var read = client.ReadGpuAsync();
        await WaitUntil(() => _factory.Started.Count > started, "the process to be launched");
        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading(id, temp: temp)));
        return await read;
    }

    [Fact]
    public void NothingIsLaunchedUntilTheFirstRead()
    {
        var client = NewClient();
        Advance(100);
        client.Tick();
        Assert.Equal(0, _factory.StartAttempts);
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task TheFirstReadLaunchesTheHostAndWaitsForItsFirstFrame()
    {
        var client = NewClient();

        var value = await FirstFrame(client);

        Assert.True(value.IsOk);
        Assert.Equal("luid_a", value.Data![0].Id);
        Assert.Equal(40, value.Data[0].TemperatureC.Data);
        Assert.Equal(1, _factory.StartAttempts);
        Assert.True(client.IsRunning);
        Assert.Equal(0, client.ConsecutiveFailures);
    }

    [Fact]
    public async Task ALaterReadReturnsTheNewestFrameWithoutLaunchingAgain()
    {
        var client = NewClient();
        await FirstFrame(client, temp: 40);

        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", temp: 55)));
        await WaitUntil(() => client.ReadGpuAsync().Result.Data?[0].TemperatureC.Data == 55, "the second frame");

        Assert.Equal(1, _factory.StartAttempts);
    }

    [Fact]
    public async Task AFrameThatIsTooOldIsNeverReturnedAsCurrent()
    {
        var client = NewClient();
        await FirstFrame(client);

        // The watchdog has not looked yet, but the age check on the read itself must already refuse the old frame.
        Advance(6.5);
        var value = await client.ReadGpuAsync();

        Assert.False(value.IsOk);
        Assert.Equal(ValueStatus.Timeout, value.Status);
        Assert.Contains("stopped answering", value.Detail);
    }

    [Fact]
    public async Task ASilentHostIsKilledAndRestartedAfterSixSeconds()
    {
        var client = NewClient();
        await FirstFrame(client);
        var first = _factory.Last;

        Advance(5.9);
        client.Tick();
        Assert.False(first.IsDisposed);
        Assert.True(client.IsRunning);

        Advance(0.2);
        client.Tick();

        Assert.Equal(1, first.KillCount);
        Assert.True(first.IsDisposed);
        Assert.False(client.IsRunning);
        Assert.Equal(1, client.ConsecutiveFailures);

        // Backoff of one second before the next launch.
        Advance(0.9);
        client.Tick();
        Assert.Equal(1, _factory.StartAttempts);
        Advance(0.2);
        client.Tick();
        Assert.Equal(2, _factory.StartAttempts);
        Assert.True(client.IsRunning);
    }

    [Fact]
    public async Task ARestartedHostThatNeverSpeaksIsJudgedFromItsLaunchTime()
    {
        var client = NewClient();
        await FirstFrame(client);
        Advance(7);
        client.Tick();
        Advance(1.1);
        client.Tick();
        Assert.Equal(2, _factory.StartAttempts);

        // No frame at all from the second process: the six seconds count from its start, not from the old frame.
        Advance(5.9);
        client.Tick();
        Assert.True(client.IsRunning);
        Advance(0.2);
        client.Tick();
        Assert.False(client.IsRunning);
        Assert.Equal(2, client.ConsecutiveFailures);
    }

    [Fact]
    public async Task BackoffDoublesFromOneSecondAndStopsAtThirty()
    {
        // The idle stop is turned off here: this walk takes longer than a minute of fake time without anybody asking.
        var client = NewClient(idleStopAfter: TimeSpan.FromHours(1));
        await FirstFrame(client);

        var expectedDelaysSeconds = new[] { 1, 2, 4, 8, 16, 30, 30 };
        for (var i = 0; i < expectedDelaysSeconds.Length; i++)
        {
            // Let the running process go stale.
            Advance(6.1);
            client.Tick();
            Assert.False(client.IsRunning);
            Assert.Equal(i + 1, client.ConsecutiveFailures);

            var delay = expectedDelaysSeconds[i];
            var startsBefore = _factory.StartAttempts;
            Advance(delay - 0.1);
            client.Tick();
            Assert.Equal(startsBefore, _factory.StartAttempts);
            Advance(0.2);
            client.Tick();
            Assert.Equal(startsBefore + 1, _factory.StartAttempts);
            Assert.True(client.IsRunning, $"launch after a {delay} s wait");
        }
    }

    [Fact]
    public async Task AGoodFrameResetsTheFailureCountAndTheBackoff()
    {
        var client = NewClient();
        await FirstFrame(client);
        Advance(6.5);
        client.Tick();
        Advance(1.1);
        client.Tick();
        Assert.Equal(1, client.ConsecutiveFailures);

        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", temp: 41)));
        await WaitUntil(() => client.ConsecutiveFailures == 0, "the failure count to reset");

        // The next failure starts again at one second.
        Advance(6.5);
        client.Tick();
        Assert.Equal(1, client.ConsecutiveFailures);
        var starts = _factory.StartAttempts;
        Advance(1.1);
        client.Tick();
        Assert.Equal(starts + 1, _factory.StartAttempts);
    }

    [Fact]
    public async Task AnExitedHostIsReportedWithItsLastErrorOutputAndRestarted()
    {
        var client = NewClient();
        await FirstFrame(client);
        _factory.Last.HasExited = true;
        _factory.Last.ExitCode = 3;
        _factory.Last.StderrTail = "sensorhost: NVML did not start";

        Advance(0.5);
        client.Tick();
        Assert.False(client.IsRunning);

        var value = await client.ReadGpuAsync();
        Assert.Equal(ValueStatus.Error, value.Status);
        Assert.Contains("exited (code 3)", value.Detail);
        Assert.Contains("NVML did not start", value.Detail);
        Assert.Contains("trying again in 1 s", value.Detail);

        Advance(1.1);
        client.Tick();
        Assert.Equal(2, _factory.StartAttempts);
    }

    [Fact]
    public async Task ALateLineFromAKilledHostIsIgnored()
    {
        var client = NewClient(firstReadingWait: TimeSpan.FromMilliseconds(300));
        await FirstFrame(client);
        var old = _factory.Last;
        old.KillEndsOutput = false;

        Advance(6.5);
        client.Tick();
        Advance(1.1);
        client.Tick();
        Assert.Equal(2, _factory.StartAttempts);

        old.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("from_the_dead", temp: 99)));
        await Task.Delay(100);
        var value = await client.ReadGpuAsync();

        Assert.False(value.IsOk);
        Assert.Equal(ValueStatus.Timeout, value.Status);
        Assert.Contains("has not produced a reading yet", value.Detail);
    }

    [Fact]
    public async Task AMissingExecutableIsPermanentlyUnsupportedAndNeverRetried()
    {
        var client = NewClient();
        _factory.FailNext(new FileNotFoundException("PCMieru.SensorHost.exe was not found next to the app"));

        var first = await client.ReadGpuAsync();
        Assert.Equal(ValueStatus.Unsupported, first.Status);
        Assert.Contains("was not found", first.Detail);

        Advance(3600);
        client.Tick();
        var second = await client.ReadGpuAsync();

        Assert.Equal(ValueStatus.Unsupported, second.Status);
        Assert.Equal(1, _factory.StartAttempts);
        Assert.Equal(0, client.ConsecutiveFailures);
    }

    [Fact]
    public async Task ALaunchFailureIsAnErrorWithBackoffNotAHotLoop()
    {
        var client = NewClient();
        _factory.FailNext(new Win32Exception(5, "Access is denied"));

        var first = await client.ReadGpuAsync();
        Assert.Equal(ValueStatus.Error, first.Status);
        Assert.Contains("could not be started", first.Detail);
        Assert.Equal(1, client.ConsecutiveFailures);

        // Asking again at once does not launch again.
        await client.ReadGpuAsync();
        Assert.Equal(1, _factory.StartAttempts);

        Advance(1.1);
        var value = await FirstFrame(client);
        Assert.True(value.IsOk);
        Assert.Equal(2, _factory.StartAttempts);
    }

    [Fact]
    public async Task AHostIsStoppedWhenNobodyAsksForAMinuteAndStartedAgainByTheNextAsk()
    {
        var client = NewClient();
        await FirstFrame(client);
        var first = _factory.Last;

        Advance(61);
        client.Tick();

        Assert.False(client.IsRunning);
        Assert.Equal(1, first.KillCount);
        Assert.Equal(0, client.ConsecutiveFailures);

        // No backoff after an idle stop: the next ask launches immediately.
        var value = await FirstFrame(client, id: "luid_b");
        Assert.True(value.IsOk);
        Assert.Equal("luid_b", value.Data![0].Id);
        Assert.Equal(2, _factory.StartAttempts);
    }

    [Fact]
    public async Task AFailingHostIsNotRelaunchedForAnAppThatStoppedAsking()
    {
        var client = NewClient();
        await FirstFrame(client);
        Advance(6.5);
        client.Tick();
        Assert.Equal(1, client.ConsecutiveFailures);

        Advance(61);
        client.Tick();
        Advance(120);
        client.Tick();

        Assert.Equal(1, _factory.StartAttempts);
        Assert.False(client.IsRunning);

        // The next ask starts it again (its backoff has long passed).
        var value = await FirstFrame(client, id: "luid_c");
        Assert.True(value.IsOk);
        Assert.Equal(2, _factory.StartAttempts);
    }

    [Fact]
    public async Task ReadingsKeepTheHostAliveWhileSomeoneIsAsking()
    {
        var client = NewClient();
        await FirstFrame(client);

        for (var i = 0; i < 5; i++)
        {
            Advance(30);
            _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a", temp: 40 + i)));
            await WaitUntil(() => client.ReadGpuAsync().Result.Data?[0].TemperatureC.Data == 40 + i, "a fresh frame");
            client.Tick();
        }

        Assert.True(client.IsRunning);
        Assert.Equal(1, _factory.StartAttempts);
    }

    [Fact]
    public async Task UnreadableLinesDoNotCountAsReadings()
    {
        var client = NewClient(firstReadingWait: TimeSpan.FromMilliseconds(300));
        var started = _factory.Started.Count;
        var read = client.ReadGpuAsync();
        await WaitUntil(() => _factory.Started.Count > started, "the process to be launched");
        _factory.Last.Push("garbage that is not json");

        // A schema 2 frame without its sequence, period and capture time is not a frame either (it is unreadable, not a standing mismatch).
        _factory.Last.Push(PcMieruJson.Serialize(new SensorFrame(SensorFrame.CurrentSchemaVersion, DateTime.UtcNow, SensorFixtures.Readings(SensorFixtures.Reading("x")))));

        var value = await read;

        Assert.Equal(ValueStatus.Timeout, value.Status);
        Assert.Contains("has not produced a reading yet", value.Detail);

        // The reason of the unreadable line is kept for the later "failed" text, and a good line still works afterwards.
        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a")));
        await WaitUntil(() => client.ReadGpuAsync().Result.IsOk, "a good frame after the garbage");
    }

    private static Task<GpuSensorView> ReadFramed(SensorHostClient client) => ((IFramedSensorClient)client).ReadGpuFramedAsync(CancellationToken.None);

    [Fact]
    public async Task AFrameOfAnotherSchemaIsAVisibleStandingFailureNotARestartLoop()
    {
        // An old child (schema 1, no sequence or capture time) next to a new parent: the parent must say so, in words, and must not
        // keep killing and restarting a child that can never speak its schema.
        var client = NewClient();
        var started = _factory.Started.Count;
        var read = client.ReadGpuAsync();
        await WaitUntil(() => _factory.Started.Count > started, "the process to be launched");
        _factory.Last.Push(PcMieruJson.Serialize(new SensorFrame("1", DateTime.UtcNow, SensorFixtures.Readings(SensorFixtures.Reading("luid_a")))));

        var value = await read;

        Assert.Equal(ValueStatus.Error, value.Status);
        Assert.Contains("does not match this app", value.Detail);
        Assert.Contains("schema 1", value.Detail);
        Assert.Contains("expects 2", value.Detail);

        var view = await ReadFramed(client);
        Assert.Equal(SourceGap.ReadFailed, view.Origin.Gap);

        // Time passing does not make it try again and again.
        Advance(120);
        client.Tick();
        Advance(120);
        client.Tick();
        Assert.Equal(1, _factory.StartAttempts);
        Assert.False(client.IsRunning);
        Assert.Contains("does not match this app", (await client.ReadGpuAsync()).Detail);
    }

    [Fact]
    public async Task ANewHostIsStartedAtTwoSecondsWhenNoLeaseIsHeld()
    {
        var client = NewClient();

        await FirstFrame(client);

        Assert.Equal([(2000, 1L)], _factory.Launches);
        Assert.Empty(_factory.Last.Periods);
        Assert.Equal(2000, ((IFramedSensorClient)client).RequestedPeriodMs);
    }

    [Fact]
    public async Task ALeaseOnARunningHostAsksForOneSecondOnceAndTheLastReleaseAsksForTwo()
    {
        var client = NewClient();
        await FirstFrame(client);
        var process = _factory.Last;

        var a = client.AcquireOneSecondLease();
        var b = client.AcquireOneSecondLease();
        Assert.Equal([1000], process.Periods);
        Assert.Equal(1000, ((IFramedSensorClient)client).RequestedPeriodMs);

        a.Dispose();
        Assert.Equal([1000], process.Periods);
        Assert.Equal(1000, ((IFramedSensorClient)client).RequestedPeriodMs);

        b.Dispose();
        Assert.Equal([1000, 2000], process.Periods);
        Assert.Equal(2000, ((IFramedSensorClient)client).RequestedPeriodMs);
    }

    [Fact]
    public async Task ADoubleDisposeGivesBackOneCountOnly()
    {
        var client = NewClient();
        await FirstFrame(client);
        var process = _factory.Last;
        var a = client.AcquireOneSecondLease();
        var b = client.AcquireOneSecondLease();

        a.Dispose();
        a.Dispose();

        // b still holds: one second stays, and nothing was asked of the child.
        Assert.Equal([1000], process.Periods);
        Assert.Equal(1000, ((IFramedSensorClient)client).RequestedPeriodMs);

        b.Dispose();
        Assert.Equal([1000, 2000], process.Periods);
    }

    [Fact]
    public async Task AHostRestartedWhileALeaseIsHeldIsStartedAtOneSecondAtOnce()
    {
        var client = NewClient();
        using var lease = client.AcquireOneSecondLease();
        await FirstFrame(client);
        Assert.Equal((1000, 1L), _factory.Launches[0]);

        Advance(6.5);
        client.Tick();
        Advance(1.1);
        client.Tick();

        Assert.Equal(2, _factory.Launches.Count);
        Assert.Equal((1000, 2L), _factory.Launches[1]);
        Assert.Empty(_factory.Last.Periods);
    }

    [Fact]
    public async Task AHostRestartedAfterTheLeaseWasReleasedIsStartedAtTwoSeconds()
    {
        var client = NewClient();
        var lease = client.AcquireOneSecondLease();
        await FirstFrame(client);
        lease.Dispose();

        Advance(6.5);
        client.Tick();
        Advance(1.1);
        client.Tick();

        Assert.Equal(2, _factory.Launches.Count);
        Assert.Equal((2000, 2L), _factory.Launches[1]);
    }

    [Fact]
    public async Task AcquiringALeaseNeverWaitsForAChildThatIsBeingLaunched()
    {
        // A lease is taken on the UI thread (the recording starts on a click): it must not wait behind a launch that is still inside Start.
        using var gate = new ManualResetEventSlim(false);
        _factory.StartGate = gate;
        var client = NewClient();
        var read = Task.Run(() => client.ReadGpuAsync()); // the launch happens on this thread, under the client's lock
        try
        {
            await WaitUntil(() => _factory.StartEntered, "the launch to be inside Start");

            var acquire = Task.Run(client.AcquireOneSecondLease);
            var finished = await Task.WhenAny(acquire, Task.Delay(5_000)) == acquire;
            Assert.True(finished, "taking a lease waited for a SensorHost launch that had not finished");
            using var lease = await acquire; // held to the end of the test: the launch must find it taken when it ends

            gate.Set();
            await WaitUntil(() => _factory.Started.Count == 1, "the launch to finish");
            var process = _factory.Last;
            await WaitUntil(() => process.Periods.Count == 1, "the new child to be asked for one second");
            Assert.Equal([1000], process.Periods); // it was started at two seconds before the lease; the launch noticed the lease and asked again
            process.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a")));
            _ = await read;
        }
        finally
        {
            gate.Set(); // a failed assert above must not leave the launch blocked
        }
    }

    [Fact]
    public async Task TakingAndGivingBackALeaseNeverWaitsForAChildThatIsBeingKilled()
    {
        var client = NewClient();
        await FirstFrame(client);
        var process = _factory.Last;
        using var gate = new ManualResetEventSlim(false);
        process.KillGate = gate;
        Advance(6.5); // the host has gone silent: the watchdog kills it, under the client's lock
        var tick = Task.Run(client.Tick);
        try
        {
            await WaitUntil(() => process.KillEntered, "the kill to start");

            var acquire = Task.Run(client.AcquireOneSecondLease);
            Assert.True(await Task.WhenAny(acquire, Task.Delay(5_000)) == acquire, "taking a lease waited for a kill that had not finished");
            var lease = await acquire;
            var release = Task.Run(lease.Dispose);
            Assert.True(await Task.WhenAny(release, Task.Delay(5_000)) == release, "giving a lease back waited for a kill that had not finished");
            await release;
        }
        finally
        {
            gate.Set();
        }

        await tick;
        Assert.Equal(2000, ((IFramedSensorClient)client).RequestedPeriodMs);
    }

    [Fact]
    public async Task AHostThatWasStoppedAfterAFailureIsRestartingUntilAFrameArrives()
    {
        var client = NewClient();
        var framed = (IFramedSensorClient)client;
        Assert.False(framed.IsHostRestarting);
        await FirstFrame(client);
        Assert.False(framed.IsHostRestarting);

        Advance(6.5);
        client.Tick(); // silent for six seconds: killed after a failure
        Assert.True(framed.IsHostRestarting);

        Advance(1.1);
        client.Tick(); // started again; no frame yet
        Assert.True(framed.IsHostRestarting);

        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a")));
        await WaitUntil(() => !framed.IsHostRestarting, "the first frame of the new host");
    }

    [Fact]
    public async Task AHostStoppedBecauseNobodyAskedIsNotRestarting()
    {
        var client = NewClient(idleStopAfter: TimeSpan.FromSeconds(5));
        await FirstFrame(client);

        Advance(5.5);
        client.Tick(); // idle: stopped on purpose, not a failure

        Assert.True(_factory.Last.IsDisposed);
        Assert.False(((IFramedSensorClient)client).IsHostRestarting);
    }

    [Fact]
    public async Task ALeaseWithNoHostRunningNeedsNoRequestBecauseTheNextLaunchStartsAtOneSecond()
    {
        var client = NewClient();
        using var lease = client.AcquireOneSecondLease();
        Assert.False(client.IsRunning);

        await FirstFrame(client);

        Assert.Equal([(1000, 1L)], _factory.Launches);
        Assert.Empty(_factory.Last.Periods);
    }

    [Fact]
    public async Task EveryFrameKeepsItsGenerationSequencePeriodAndCaptureTime()
    {
        var client = NewClient();
        var started = _factory.Started.Count;
        var read = ReadFramed(client);
        await WaitUntil(() => _factory.Started.Count > started, "the process to be launched");

        // A 2 s frame from before a switch is passed as what it is, even though the client is already asking for one second.
        using var lease = client.AcquireOneSecondLease();
        _factory.Last.Push(SensorFixtures.Serialize(SensorFixtures.Frame(1, 41, 2000, SensorFixtures.Reading("luid_a"))));
        var view = await read;

        Assert.True(view.Gpus.IsOk);
        Assert.Equal(new FrameId(1, 41), view.Origin.Frame);
        Assert.Equal(2000, view.Origin.PeriodMs);
        Assert.Equal(1_000_000 + (41 * 1_000_000L), view.Origin.AcquiredUs);
        Assert.Equal(SourceGap.None, view.Origin.Gap);

        _factory.Last.Push(SensorFixtures.Serialize(SensorFixtures.Frame(1, 42, 1000, SensorFixtures.Reading("luid_a"))));
        await WaitUntil(() => ReadFramed(client).Result.Origin.Frame == new FrameId(1, 42), "the next frame");
        Assert.Equal(1000, (await ReadFramed(client)).Origin.PeriodMs);
    }

    [Fact]
    public async Task AMissingReadingSaysWhyItIsMissingInItsOrigin()
    {
        var client = NewClient();
        await FirstFrame(client);

        Advance(6.5);
        client.Tick();
        var view = await ReadFramed(client);

        Assert.False(view.Gpus.IsOk);
        Assert.Null(view.Origin.Frame);
        Assert.Equal(SourceGap.HostRestart, view.Origin.Gap);
    }

    [Fact]
    public async Task BlankLinesAreSkipped()
    {
        var client = NewClient();
        var started = _factory.Started.Count;
        var read = client.ReadGpuAsync();
        await WaitUntil(() => _factory.Started.Count > started, "the process to be launched");
        _factory.Last.Push(string.Empty);
        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a")));

        Assert.True((await read).IsOk);
    }

    [Fact]
    public async Task ACancelledReadThrowsAndLeavesTheHostRunning()
    {
        var client = NewClient();
        await FirstFrame(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadGpuAsync(cts.Token));

        Assert.True(client.IsRunning);
    }

    [Fact]
    public async Task DisposeClosesTheInputKillsAHostThatDoesNotLeaveAndAnswersGone()
    {
        var client = NewClient();
        await FirstFrame(client);
        var process = _factory.Last;
        process.ExitsOnCloseInput = false;

        client.Dispose();
        client.Dispose();

        Assert.Equal(1, process.CloseInputCount);
        Assert.Equal(1, process.KillCount);
        Assert.True(process.IsDisposed);
        var value = await client.ReadGpuAsync();
        Assert.Equal(ValueStatus.Gone, value.Status);
        client.Tick();
        Assert.Equal(1, _factory.StartAttempts);
    }

    [Fact]
    public async Task ACooperativeHostIsNotKilledOnDispose()
    {
        var client = NewClient();
        await FirstFrame(client);
        var process = _factory.Last;
        process.ExitsOnCloseInput = true;

        client.Dispose();

        Assert.Equal(1, process.CloseInputCount);
        Assert.Equal(0, process.KillCount);
        Assert.True(process.IsDisposed);
    }

    [Fact]
    public async Task DisposingWhileAReadIsWaitingAnswersGone()
    {
        var client = NewClient();
        var started = _factory.Started.Count;
        var read = client.ReadGpuAsync();
        await WaitUntil(() => _factory.Started.Count > started, "the process to be launched");

        client.Dispose();

        Assert.Equal(ValueStatus.Gone, (await read).Status);
    }

    [Fact]
    public void TheDefaultTimingsAreTheProductsNotTheTestsShortcuts()
    {
        var o = SensorHostOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(6), o.StaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(1), o.BackoffStart);
        Assert.Equal(TimeSpan.FromSeconds(30), o.BackoffMax);
        Assert.Equal(TimeSpan.FromMilliseconds(500), o.WatchdogInterval);
    }

    [Fact]
    public async Task ARealMissingExecutablePathGivesUnsupportedWithoutThrowing()
    {
        using var client = new SensorHostClient(Path.Combine(Path.GetTempPath(), "pcmieru-no-such-dir", "PCMieru.SensorHost.exe"));

        var value = await client.ReadGpuAsync();

        Assert.Equal(ValueStatus.Unsupported, value.Status);
        Assert.Contains("was not found", value.Detail);
    }
}
