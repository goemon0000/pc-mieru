using System.ComponentModel;
using System.Threading.Channels;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>A SensorHost stand-in: lines are pushed by the test, and Kill/CloseInput/Dispose are counted.</summary>
internal sealed class FakeSensorProcess : ISensorProcess
{
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
    private int _killed;
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

    public bool WaitForExit(int milliseconds) => ExitsOnCloseInput;

    public void Kill()
    {
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
    private readonly Queue<Exception> _failures = new();

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

    public FakeSensorProcess Last => Started[^1];

    public void FailNext(Exception ex) => _failures.Enqueue(ex);

    public ISensorProcess Start()
    {
        StartAttempts++;
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
        _factory.Last.Push(PcMieruJson.Serialize(new SensorFrame("99", DateTime.UtcNow, SensorFixtures.Readings(SensorFixtures.Reading("x")))));

        var value = await read;

        Assert.Equal(ValueStatus.Timeout, value.Status);
        Assert.Contains("has not produced a reading yet", value.Detail);

        // The reason of the unreadable line is kept for the later "failed" text, and a good line still works afterwards.
        _factory.Last.Push(SensorFixtures.FrameLine(SensorFixtures.Reading("luid_a")));
        await WaitUntil(() => client.ReadGpuAsync().Result.IsOk, "a good frame after the garbage");
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
