using PcMieru.Core.Collection;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.NetMeter;

internal sealed class FakeMeterProcess(uint id) : INetMeterProcess
{
    public uint Id => id;

    public volatile bool Exited;

    public int Code;

    public volatile bool Disposed;

    public int Waits;

    public bool HasExited => Exited;

    public int ExitCode => Code;

    public bool WaitForExit(TimeSpan timeout)
    {
        Interlocked.Increment(ref Waits);
        return Exited;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeLauncher : INetMeterLauncher
{
    public NetMeterLaunchStatus Status = NetMeterLaunchStatus.Started;

    public int Error;

    public FakeMeterProcess Process = new(4242);

    /// <summary>Runs inside Launch, like the UAC prompt that the real call waits for.</summary>
    public Action? Before;

    public Exception? Throw;

    public List<(string Exe, string Arguments, bool Elevate)> Calls { get; } = [];

    public NetMeterLaunch Launch(string exePath, string arguments, bool elevate)
    {
        lock (Calls)
        {
            Calls.Add((exePath, arguments, elevate));
        }

        Before?.Invoke();
        if (Throw is not null)
        {
            throw Throw;
        }

        return Status == NetMeterLaunchStatus.Started
            ? new NetMeterLaunch(Status, Process)
            : new NetMeterLaunch(Status, null, Error);
    }
}

internal sealed class FakeMeterChannel(string pipeName) : INetMeterChannel
{
    public string PipeName => pipeName;

    public bool Connects = true;

    public uint Server = 4242;

    /// <summary>What a Read returns. Defaults to one healthy reply per call.</summary>
    public Func<NetMeterExchange> OnRead = () => FakeMeterChannel.Healthy();

    public int ConnectCalls;
    public int Reads;
    public int Stops;
    public volatile bool Disposed;

    public uint ServerProcessId => Server;

    public static NetMeterExchange Healthy(long seconds = 1, uint error = 0) =>
        new(new NetMeterReading(seconds * 10_000_000, 0, 0, 0, true, error, []), NetMeterFailure.None);

    public static NetMeterExchange Unhealthy(uint error) =>
        new(new NetMeterReading(0, 0, 0, 0, false, error, []), NetMeterFailure.None);

    public static NetMeterExchange Failure(NetMeterFailure failure) => new(null, failure);

    public bool TryConnect(TimeSpan timeout)
    {
        Interlocked.Increment(ref ConnectCalls);
        if (!Connects)
        {
            Thread.Sleep(Math.Min(5, (int)timeout.TotalMilliseconds));
        }

        return Connects;
    }

    public NetMeterExchange Read(TimeSpan timeout)
    {
        Interlocked.Increment(ref Reads);
        return OnRead();
    }

    /// <summary>Like the real channel, a closed one sends nothing.</summary>
    public void SendStop()
    {
        if (!Disposed)
        {
            Interlocked.Increment(ref Stops);
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeMeterChannels : INetMeterChannelFactory
{
    /// <summary>Scripts each channel as it is created (the first one is index 0).</summary>
    public Action<FakeMeterChannel, int>? Configure;

    public Exception? Throw;

    public List<FakeMeterChannel> Created { get; } = [];

    public INetMeterChannel Create(string pipeName)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        var channel = new FakeMeterChannel(pipeName);
        lock (Created)
        {
            Configure?.Invoke(channel, Created.Count);
            Created.Add(channel);
        }

        return channel;
    }
}

public sealed class NetMeterControlTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const uint MyPid = 1234;
    private const long MyCreated = 133_000_000_000_000_000;

    private readonly FakeLauncher _launcher = new();
    private readonly FakeMeterChannels _channels = new();
    private readonly List<NetMeterStatus> _seen = [];
    private readonly NetMeterControl _control;
    private bool _elevated;
    private bool _exeExists = true;
    private NetMeterIdentity _identity = new(MyPid, MyCreated, Sid);

    public NetMeterControlTests()
    {
        _control = new NetMeterControl(
            _launcher,
            _channels,
            @"C:\app\PCMieru.NetMeter.exe",
            () => _elevated,
            () => _identity,
            _ => _exeExists,
            new NetMeterTimings(
                Connect: TimeSpan.FromMilliseconds(300),
                FirstRead: TimeSpan.FromMilliseconds(300),
                ConnectSlice: TimeSpan.FromMilliseconds(10),
                Watch: TimeSpan.FromMilliseconds(40),
                StopWait: TimeSpan.FromMilliseconds(50)));
        _control.StateChanged += (_, _) =>
        {
            lock (_seen)
            {
                _seen.Add(_control.State.Status);
            }
        };
    }

    public void Dispose() => _control.Dispose();

    private INetMeterReader Reader => _control;

    private IReadOnlyList<NetMeterStatus> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    private async Task<NetMeterState> StartAsync() => await _control.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

    private async Task<NetMeterState> StartRunningAsync()
    {
        var state = await StartAsync();
        Assert.Equal(NetMeterStatus.Running, state.Status);
        return state;
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for: " + what);
            Thread.Sleep(5);
        }
    }

    private static string Plain(string key) => Strings.Get(key);

    private static string Plain(string key, int code) =>
        Strings.Format(key, new Dictionary<string, string> { ["code"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    // ---- starting ----

    [Fact]
    public void A_new_control_is_off_and_has_started_nothing()
    {
        Assert.Equal(new NetMeterState(NetMeterStatus.Off), _control.State);
        Assert.Empty(_launcher.Calls);
        Assert.Empty(_channels.Created);
    }

    [Fact]
    public async Task A_healthy_first_reply_makes_it_Running_after_Starting_and_both_changes_are_announced()
    {
        var state = await StartRunningAsync();

        Assert.Equal(new NetMeterState(NetMeterStatus.Running), state);
        Assert.Equal(new NetMeterState(NetMeterStatus.Running), _control.State);
        Assert.Equal([NetMeterStatus.Starting, NetMeterStatus.Running], Seen);
        Assert.Single(_launcher.Calls);
    }

    [Fact]
    public async Task The_launch_is_elevated_with_the_exact_command_line_and_the_pipe_of_that_line_is_the_one_connected()
    {
        _ = await StartRunningAsync();

        var call = Assert.Single(_launcher.Calls);
        Assert.Equal(@"C:\app\PCMieru.NetMeter.exe", call.Exe);
        Assert.True(call.Elevate);
        Assert.True(NetMeterArguments.TryParse(call.Arguments.Split(' '), out var parsed, out var error), error);
        Assert.Equal(MyPid, parsed!.ParentPid);
        Assert.Equal(MyCreated, parsed.ParentCreatedFileTime);
        Assert.Equal(Sid, parsed.UserSid);
        var channel = Assert.Single(_channels.Created);
        Assert.Equal(parsed.PipeName, channel.PipeName);
    }

    [Fact]
    public async Task Every_start_uses_a_fresh_unguessable_pipe_name()
    {
        _ = await StartRunningAsync();
        _control.Stop();
        _ = await StartRunningAsync();

        Assert.Equal(2, _channels.Created.Count);
        Assert.NotEqual(_channels.Created[0].PipeName, _channels.Created[1].PipeName);
        Assert.All(_channels.Created, c => Assert.True(NetMeterArguments.IsPipeName(c.PipeName)));
    }

    [Fact]
    public async Task An_already_elevated_front_end_starts_the_meter_without_asking_for_elevation()
    {
        _elevated = true;

        _ = await StartRunningAsync();

        Assert.False(Assert.Single(_launcher.Calls).Elevate);
    }

    [Fact]
    public async Task A_missing_exe_is_Unsupported_with_a_reason_and_nothing_is_launched()
    {
        _exeExists = false;

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Unsupported, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetMissing), state.Detail);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task A_declined_prompt_is_Declined_and_no_pipe_is_opened()
    {
        _launcher.Status = NetMeterLaunchStatus.Declined;
        _launcher.Error = 1223;

        var state = await StartAsync();

        Assert.Equal(new NetMeterState(NetMeterStatus.Declined), state);
        Assert.Equal([NetMeterStatus.Starting, NetMeterStatus.Declined], Seen);
        Assert.Empty(_channels.Created);
    }

    [Fact]
    public async Task A_declined_start_can_be_asked_again_and_then_works()
    {
        _launcher.Status = NetMeterLaunchStatus.Declined;
        _ = await StartAsync();

        _launcher.Status = NetMeterLaunchStatus.Started;
        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Running, state.Status);
        Assert.Equal(2, _launcher.Calls.Count);
    }

    [Fact]
    public async Task A_launch_that_fails_is_Failed_and_names_the_windows_error()
    {
        _launcher.Status = NetMeterLaunchStatus.Failed;
        _launcher.Error = 740;

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetLaunchFailed, 740), state.Detail);
        Assert.Contains("740", state.Detail);
    }

    [Fact]
    public async Task A_launcher_that_throws_ends_Failed_not_as_an_exception_on_the_caller()
    {
        _launcher.Throw = new InvalidOperationException("boom");

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetNoAnswer), state.Detail);
    }

    [Fact]
    public async Task A_channel_that_cannot_be_made_ends_Failed_and_lets_go_of_the_process()
    {
        _channels.Throw = new InvalidOperationException("no pipe");

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.True(_launcher.Process.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-1;(A;;GA;;;WD)")]
    public async Task An_identity_the_meter_would_refuse_is_Failed_before_any_prompt(string sid)
    {
        _identity = new NetMeterIdentity(MyPid, MyCreated, sid);

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetNoIdentity), state.Detail);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task An_identity_that_cannot_be_read_is_Failed_before_any_prompt()
    {
        _identity = new NetMeterIdentity(MyPid, 0, Sid);

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task A_meter_that_never_opens_its_pipe_is_Failed_without_a_stop_sent_to_nobody()
    {
        _channels.Configure = (c, _) => c.Connects = false;

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetNoAnswer), state.Detail);
        var channel = Assert.Single(_channels.Created);
        Assert.True(channel.ConnectCalls > 1, "the connect is tried in slices");
        Assert.Equal(0, channel.Stops);
        Assert.True(channel.Disposed);
        Assert.True(_launcher.Process.Disposed);
    }

    [Fact]
    public async Task A_meter_that_ends_while_we_wait_for_its_pipe_is_Failed_with_its_exit_code()
    {
        _channels.Configure = (c, _) => c.Connects = false;
        _launcher.Process.Exited = true;
        _launcher.Process.Code = 4;

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetExited, 4), state.Detail);
    }

    [Fact]
    public async Task A_pipe_served_by_another_process_is_refused_and_nothing_is_sent_to_it()
    {
        _channels.Configure = (c, _) => c.Server = 9999;

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetNotOurPipe), state.Detail);
        var channel = Assert.Single(_channels.Created);
        Assert.Equal(0, channel.Reads);
        Assert.Equal(0, channel.Stops);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task A_first_reply_that_does_not_come_is_Failed_and_the_meter_is_told_to_stop()
    {
        _channels.Configure = (c, _) => c.OnRead = () => FakeMeterChannel.Failure(NetMeterFailure.Timeout);

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetNoAnswer), state.Detail);
        var channel = Assert.Single(_channels.Created);
        Assert.Equal(1, channel.Stops);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task A_first_reply_the_protocol_does_not_allow_is_Failed_with_its_own_words()
    {
        _channels.Configure = (c, _) => c.OnRead = () => FakeMeterChannel.Failure(NetMeterFailure.BadReply);

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetBadReply), state.Detail);
    }

    [Fact]
    public async Task A_pipe_that_ends_during_the_first_read_of_a_meter_that_exited_says_the_exit_code()
    {
        _launcher.Process.Code = 3;
        _channels.Configure = (c, _) => c.OnRead = () =>
        {
            _launcher.Process.Exited = true;
            return FakeMeterChannel.Failure(NetMeterFailure.Broken);
        };

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetExited, 3), state.Detail);
    }

    [Theory]
    [InlineData(5u, CollectionStrings.NetSessionDenied)]
    [InlineData(995u, CollectionStrings.NetSessionStopped)]
    [InlineData(4201u, CollectionStrings.NetSessionStopped)]
    [InlineData(1450u, CollectionStrings.NetSessionResources)]
    [InlineData(1455u, CollectionStrings.NetSessionResources)]
    public async Task A_meter_that_says_its_session_is_not_healthy_is_Failed_with_the_meaning_of_the_error(uint code, string key)
    {
        _channels.Configure = (c, _) => c.OnRead = () => FakeMeterChannel.Unhealthy(code);

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(key), state.Detail);
        Assert.Equal(1, Assert.Single(_channels.Created).Stops);
    }

    [Fact]
    public async Task An_unknown_session_error_is_named_by_its_number()
    {
        _channels.Configure = (c, _) => c.OnRead = () => FakeMeterChannel.Unhealthy(87);

        var state = await StartAsync();

        Assert.Equal(Plain(CollectionStrings.NetSessionError, 87), state.Detail);
        Assert.Contains("87", state.Detail);
    }

    [Fact]
    public void Session_errors_have_distinct_plain_words_for_the_known_codes()
    {
        var words = new[] { 5u, 995u, 1450u, 87u }.Select(NetMeterControl.DescribeSessionError).ToArray();

        Assert.Equal(words.Length, words.Distinct().Count());
        Assert.Equal(NetMeterControl.DescribeSessionError(995), NetMeterControl.DescribeSessionError(4201));
        Assert.Equal(NetMeterControl.DescribeSessionError(1450), NetMeterControl.DescribeSessionError(1455));
    }

    // ---- calls while starting or running ----

    [Fact]
    public async Task Callers_that_ask_while_the_prompt_is_open_share_one_start()
    {
        using var inPrompt = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        _launcher.Before = () =>
        {
            inPrompt.Set();
            Assert.True(answer.Wait(TimeSpan.FromSeconds(10)));
        };

        var first = _control.StartAsync();
        Assert.True(inPrompt.Wait(TimeSpan.FromSeconds(10)));
        var second = _control.StartAsync();
        Assert.Equal(NetMeterStatus.Starting, _control.State.Status);
        answer.Set();

        Assert.Equal(NetMeterStatus.Running, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(NetMeterStatus.Running, (await second.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Single(_launcher.Calls);
        Assert.Equal([NetMeterStatus.Starting, NetMeterStatus.Running], Seen);
    }

    [Fact]
    public async Task Asking_again_while_Running_returns_Running_and_launches_nothing()
    {
        _ = await StartRunningAsync();

        var again = await StartAsync();

        Assert.Equal(NetMeterStatus.Running, again.Status);
        Assert.Single(_launcher.Calls);
    }

    [Fact]
    public async Task A_caller_that_gives_up_waiting_does_not_cancel_the_start()
    {
        using var inPrompt = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        _launcher.Before = () =>
        {
            inPrompt.Set();
            Assert.True(answer.Wait(TimeSpan.FromSeconds(10)));
        };
        using var cts = new CancellationTokenSource();

        var waiting = _control.StartAsync(cts.Token);
        Assert.True(inPrompt.Wait(TimeSpan.FromSeconds(10)));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        answer.Set();

        WaitFor(() => _control.State.Status == NetMeterStatus.Running, "Running after the caller gave up");
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_starts_nothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _control.StartAsync(cts.Token));

        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task Stop_while_the_prompt_is_open_leaves_it_Off_and_the_meter_that_appears_is_told_to_stop()
    {
        using var inPrompt = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        _launcher.Before = () =>
        {
            inPrompt.Set();
            Assert.True(answer.Wait(TimeSpan.FromSeconds(10)));
        };

        var start = _control.StartAsync();
        Assert.True(inPrompt.Wait(TimeSpan.FromSeconds(10)));
        _control.Stop();
        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
        answer.Set();

        var result = await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NetMeterStatus.Off, result.Status);
        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
        var channel = Assert.Single(_channels.Created);
        Assert.Equal(1, channel.Stops);
        Assert.True(channel.Disposed);
        Assert.True(_launcher.Process.Disposed);
        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out _));
    }

    [Fact]
    public async Task Stop_tells_the_meter_closes_the_pipe_goes_Off_and_a_later_start_is_a_new_meter()
    {
        _ = await StartRunningAsync();

        _control.Stop();

        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
        var channel = Assert.Single(_channels.Created);
        Assert.Equal(1, channel.Stops);
        Assert.True(channel.Disposed);
        Assert.True(_launcher.Process.Disposed);
        Assert.Equal([NetMeterStatus.Starting, NetMeterStatus.Running, NetMeterStatus.Off], Seen);

        _launcher.Process = new FakeMeterProcess(5050);
        _channels.Configure = (c, _) => c.Server = 5050;
        _ = await StartRunningAsync();
        Assert.Equal(2, _launcher.Calls.Count);
    }

    [Fact]
    public void Stop_on_a_control_that_never_started_changes_nothing()
    {
        _control.Stop();

        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
        Assert.Empty(Seen);
    }

    [Fact]
    public async Task Dispose_stops_the_meter_waits_for_it_to_end_and_refuses_further_starts()
    {
        _ = await StartRunningAsync();

        _control.Dispose();

        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
        Assert.Equal(1, _channels.Created[0].Stops);
        Assert.Equal(1, _launcher.Process.Waits);
        Assert.True(_launcher.Process.Disposed);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = _control.StartAsync();
        });
        _control.Dispose();
        Assert.Equal(1, _channels.Created[0].Stops);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_break_the_control()
    {
        _control.StateChanged += (_, _) => throw new InvalidOperationException("handler");

        var state = await StartAsync();

        Assert.Equal(NetMeterStatus.Running, state.Status);
    }

    // ---- reading ----

    [Fact]
    public void Nothing_can_be_read_while_Off()
    {
        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out var reading, out _));
        Assert.Null(reading);
    }

    [Fact]
    public async Task A_read_returns_the_reply_and_the_generation_of_this_meter_run()
    {
        _channels.Configure = (c, _) =>
        {
            var seconds = 1;
            c.OnRead = () => FakeMeterChannel.Healthy(seconds++);
        };
        _ = await StartRunningAsync();

        Assert.True(Reader.TryRead(TimeSpan.FromSeconds(1), out var reading, out var generation));

        Assert.Equal(2 * 10_000_000, reading!.TimestampTicks); // the first reply was taken by the start
        Assert.Equal(1, generation);
        Assert.Equal(NetMeterStatus.Running, _control.State.Status);
    }

    [Fact]
    public async Task A_restart_is_a_new_generation_so_counters_of_two_runs_are_never_compared()
    {
        _ = await StartRunningAsync();
        Assert.True(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out var first));
        _control.Stop();
        _ = await StartRunningAsync();

        Assert.True(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out var second));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_late_reply_is_met_by_one_new_connection_and_the_run_goes_on_with_the_same_generation()
    {
        _channels.Configure = (c, index) =>
        {
            if (index == 0)
            {
                var replies = 0;
                c.OnRead = () => ++replies == 1 ? FakeMeterChannel.Healthy() : FakeMeterChannel.Failure(NetMeterFailure.Timeout);
            }
        };
        _ = await StartRunningAsync();

        Assert.True(Reader.TryRead(TimeSpan.FromSeconds(1), out var reading, out var generation));

        Assert.NotNull(reading);
        Assert.Equal(1, generation);
        Assert.Equal(NetMeterStatus.Running, _control.State.Status);
        Assert.Equal(2, _channels.Created.Count);
        Assert.True(_channels.Created[0].Disposed, "the old connection is closed first: the meter has a single pipe instance");
        Assert.Equal(_channels.Created[0].PipeName, _channels.Created[1].PipeName);
        Assert.Equal(0, _channels.Created[1].Stops);
        Assert.Equal([NetMeterStatus.Starting, NetMeterStatus.Running], Seen);
    }

    [Fact]
    public async Task A_pipe_that_stays_broken_after_the_new_connection_fails_the_meter_with_the_words_for_a_lost_pipe()
    {
        _channels.Configure = (c, index) =>
        {
            if (index == 0)
            {
                var replies = 0;
                c.OnRead = () => ++replies == 1 ? FakeMeterChannel.Healthy() : FakeMeterChannel.Failure(NetMeterFailure.Broken);
            }
            else
            {
                c.Connects = false;
            }
        };
        _ = await StartRunningAsync();

        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out var reading, out _));

        Assert.Null(reading);
        var state = _control.State;
        Assert.Equal(NetMeterStatus.Failed, state.Status);
        Assert.Equal(Plain(CollectionStrings.NetPipeLost), state.Detail);
        Assert.Equal([NetMeterStatus.Starting, NetMeterStatus.Running, NetMeterStatus.Failed], Seen);
        Assert.True(_launcher.Process.Disposed);
    }

    [Fact]
    public async Task A_broken_pipe_of_a_meter_that_has_exited_is_not_reconnected_and_gives_the_exit_code()
    {
        _launcher.Process.Code = 5;
        _channels.Configure = (c, index) =>
        {
            var replies = 0;
            c.OnRead = () =>
            {
                if (++replies == 1)
                {
                    return FakeMeterChannel.Healthy();
                }

                _launcher.Process.Exited = true;
                return FakeMeterChannel.Failure(NetMeterFailure.Broken);
            };
        };
        _ = await StartRunningAsync();

        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out _));

        Assert.Single(_channels.Created);
        Assert.Equal(Plain(CollectionStrings.NetExited, 5), _control.State.Detail);
    }

    [Fact]
    public async Task A_new_connection_to_a_pipe_of_another_process_is_closed_and_the_meter_is_failed()
    {
        _channels.Configure = (c, index) =>
        {
            if (index == 0)
            {
                var replies = 0;
                c.OnRead = () => ++replies == 1 ? FakeMeterChannel.Healthy() : FakeMeterChannel.Failure(NetMeterFailure.Broken);
            }
            else
            {
                c.Server = 9999;
            }
        };
        _ = await StartRunningAsync();

        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out _));

        Assert.Equal(NetMeterStatus.Failed, _control.State.Status);
        var intruder = _channels.Created[1];
        Assert.Equal(0, intruder.Reads);
        Assert.Equal(0, intruder.Stops);
        Assert.True(intruder.Disposed);
    }

    [Fact]
    public async Task A_reply_the_protocol_does_not_allow_fails_the_meter_without_another_try()
    {
        _channels.Configure = (c, _) =>
        {
            var replies = 0;
            c.OnRead = () => ++replies == 1 ? FakeMeterChannel.Healthy() : FakeMeterChannel.Failure(NetMeterFailure.BadReply);
        };
        _ = await StartRunningAsync();

        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out _));

        Assert.Single(_channels.Created);
        Assert.Equal(Plain(CollectionStrings.NetBadReply), _control.State.Detail);
    }

    [Fact]
    public async Task A_session_that_stops_being_healthy_while_running_fails_the_meter_with_the_meaning_of_the_error()
    {
        _channels.Configure = (c, _) =>
        {
            var replies = 0;
            c.OnRead = () => ++replies == 1 ? FakeMeterChannel.Healthy() : FakeMeterChannel.Unhealthy(4201);
        };
        _ = await StartRunningAsync();

        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out _));

        Assert.Equal(NetMeterStatus.Failed, _control.State.Status);
        Assert.Equal(NetMeterControl.DescribeSessionError(4201), _control.State.Detail);
        Assert.Equal(1, _channels.Created[0].Stops);
    }

    [Fact]
    public async Task A_failed_meter_can_be_started_again_and_is_a_new_generation()
    {
        _channels.Configure = (c, index) =>
        {
            var replies = 0;
            c.OnRead = () => index == 0 && ++replies > 1 ? FakeMeterChannel.Unhealthy(4201) : FakeMeterChannel.Healthy();
        };
        _ = await StartRunningAsync();
        Assert.False(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out _));
        Assert.Equal(NetMeterStatus.Failed, _control.State.Status);

        _launcher.Process = new FakeMeterProcess(4242);
        _ = await StartRunningAsync();

        Assert.Equal(2, _launcher.Calls.Count);
        Assert.True(Reader.TryRead(TimeSpan.FromSeconds(1), out _, out var generation));
        Assert.Equal(2, generation);
    }

    [Fact]
    public async Task A_meter_that_ends_while_nobody_reads_is_noticed_by_the_watch()
    {
        _launcher.Process.Code = 3;
        _ = await StartRunningAsync();

        _launcher.Process.Exited = true;

        WaitFor(() => _control.State.Status == NetMeterStatus.Failed, "the watch to notice the exit");
        Assert.Equal(Plain(CollectionStrings.NetExited, 3), _control.State.Detail);
        Assert.Equal(1, _channels.Created[0].Stops);
    }

    [Fact]
    public async Task Stop_during_a_read_wins_and_the_state_stays_Off()
    {
        using var inRead = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _channels.Configure = (c, _) =>
        {
            var replies = 0;
            c.OnRead = () =>
            {
                if (++replies == 1)
                {
                    return FakeMeterChannel.Healthy();
                }

                inRead.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return FakeMeterChannel.Failure(NetMeterFailure.Broken);
            };
        };
        _ = await StartRunningAsync();

        var read = Task.Run(() => Reader.TryRead(TimeSpan.FromSeconds(5), out _, out _));
        Assert.True(inRead.Wait(TimeSpan.FromSeconds(10)));
        _control.Stop();
        release.Set();

        Assert.False(await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(NetMeterStatus.Off, _control.State.Status);
    }
}
