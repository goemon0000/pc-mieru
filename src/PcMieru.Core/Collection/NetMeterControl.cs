using System.Globalization;
using System.Security.Cryptography;
using PcMieru.Wire;

namespace PcMieru.Core.Collection;

/// <summary>What the sampling stage needs from the meter: its cumulative counters.</summary>
internal interface INetMeterReader
{
    /// <summary>
    /// Reads the counters, with <paramref name="timeout"/> as the whole budget. False when the meter is not Running or the read
    /// failed (the control then has moved to Failed, with its reason). <paramref name="generation"/> changes every time the
    /// meter (re)starts: counters of two generations are never compared.
    /// </summary>
    bool TryRead(TimeSpan timeout, out NetMeterReading? reading, out long generation);
}

/// <summary>How long the control waits for what. Tests shorten them; the app uses <see cref="Default"/>.</summary>
internal sealed record NetMeterTimings(TimeSpan Connect, TimeSpan FirstRead, TimeSpan ConnectSlice, TimeSpan Watch, TimeSpan StopWait)
{
    /// <summary>
    /// <see cref="Connect"/> starts when the UAC prompt has been answered (the launch call itself waits for the prompt), so it only
    /// has to cover the meter starting up. <see cref="Watch"/> is how often an idle front end looks whether the meter is still there.
    /// </summary>
    public static NetMeterTimings Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3));
}

/// <summary>
/// The real <see cref="INetMeterControl"/>: starts <c>PCMieru.NetMeter.exe</c> from the app folder (one UAC prompt, or none when
/// this process is already elevated), connects to its pipe, and keeps the state honest from then on (SPEC 4.1).
/// <list type="bullet">
/// <item><b>Starting</b> from the launch until the first reply; <b>Running</b> on the first healthy reply.</item>
/// <item><b>Declined</b> when the UAC prompt was answered no; <b>Unsupported</b> when the exe is not there.</item>
/// <item><b>Failed</b> (with plain words) when the meter ends, the pipe breaks or does not answer, the pipe is not the one of the
/// process that was started, or the meter says its ETW session is not healthy.</item>
/// <item><see cref="Stop"/> tells the meter to stop and goes Off. The meter also stops by itself when this process ends.</item>
/// </list>
/// Thread-safe. Every start, stop and failure begins a new epoch, so a slow continuation of an older one (a UAC prompt that
/// is answered after Stop) finds it has been superseded, cleans up what it made and changes nothing.
/// </summary>
public sealed class NetMeterControl : INetMeterControl, INetMeterReader, IDisposable
{
    /// <summary>The exe that sits next to the app.</summary>
    public const string ExeName = "PCMieru.NetMeter.exe";

    private readonly object _gate = new();
    private readonly object _readGate = new();
    private readonly INetMeterLauncher _launcher;
    private readonly INetMeterChannelFactory _channels;
    private readonly string _exePath;
    private readonly Func<bool> _isElevated;
    private readonly Func<NetMeterIdentity> _identity;
    private readonly Func<string, bool> _exists;
    private readonly NetMeterTimings _timings;

    private NetMeterState _state = new(NetMeterStatus.Off);
    private long _epoch;
    private long _generation;
    private Task<NetMeterState>? _starting;
    private Session? _session;
    private Timer? _watch;
    private bool _disposed;

    public NetMeterControl()
        : this(
            new SystemNetMeterLauncher(),
            new NamedPipeNetMeterChannelFactory(),
            Path.Combine(AppContext.BaseDirectory, ExeName),
            Management.ElevationCheck.IsElevated,
            NetMeterIdentity.Current,
            File.Exists,
            NetMeterTimings.Default)
    {
    }

    internal NetMeterControl(
        INetMeterLauncher launcher,
        INetMeterChannelFactory channels,
        string exePath,
        Func<bool> isElevated,
        Func<NetMeterIdentity> identity,
        Func<string, bool> exists,
        NetMeterTimings timings)
    {
        Strings.Register(CollectionStrings.All);
        _launcher = launcher;
        _channels = channels;
        _exePath = exePath;
        _isElevated = isElevated;
        _identity = identity;
        _exists = exists;
        _timings = timings;
    }

    public NetMeterState State => Volatile.Read(ref _state);

    public event EventHandler? StateChanged;

    public Task<NetMeterState> StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task<NetMeterState> task;
        var changed = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.Status == NetMeterStatus.Running)
            {
                return Task.FromResult(_state);
            }

            if (_starting is { } running)
            {
                task = running;
            }
            else
            {
                var epoch = ++_epoch;
                changed = SetStateLocked(new NetMeterState(NetMeterStatus.Starting));
                task = _starting = Task.Run(() => StartSafely(epoch));
            }
        }

        if (changed)
        {
            Raise();
        }

        // A caller that gives up only stops waiting: the prompt cannot be taken back, and whatever comes of it is the state.
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    public void Stop() => StopCore(waitForExit: false);

    /// <summary>Stops the meter and waits a moment for it to end (its ETW session goes with it).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopCore(waitForExit: true);
    }

    /// <inheritdoc />
    bool INetMeterReader.TryRead(TimeSpan timeout, out NetMeterReading? reading, out long generation)
    {
        reading = null;
        generation = 0;
        lock (_readGate)
        {
            Session? session;
            long epoch;
            lock (_gate)
            {
                if (_state.Status != NetMeterStatus.Running || _session is null)
                {
                    return false;
                }

                session = _session;
                epoch = _epoch;
                generation = _generation;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var exchange = session.Channel.Read(timeout / 2);
            if (exchange.Reading is null && exchange.Failure is NetMeterFailure.Timeout or NetMeterFailure.Broken && !session.Process.HasExited)
            {
                exchange = ReconnectAndRead(session, epoch, timeout - clock.Elapsed);
            }

            if (exchange.Reading is null)
            {
                Fail(epoch, session, exchange.Failure);
                return false;
            }

            if (!exchange.Reading.SessionHealthy)
            {
                FailWith(epoch, DescribeSessionError(exchange.Reading.ErrorCode));
                return false;
            }

            lock (_gate)
            {
                if (_epoch != epoch)
                {
                    return false; // stopped while we were reading: the state is whatever Stop made it
                }
            }

            reading = exchange.Reading;
            return true;
        }
    }

    /// <summary>
    /// The pipe broke or a reply was late. The meter lets its client come back within a few seconds, so one new connection (the
    /// old one closed first: there is a single instance) is tried before the meter is given up. Counters are cumulative and the
    /// meter's clock keeps going, so nothing is lost across the gap and the generation stays.
    /// </summary>
    private NetMeterExchange ReconnectAndRead(Session session, long epoch, TimeSpan budget)
    {
        var broken = new NetMeterExchange(null, NetMeterFailure.Broken);
        if (budget <= TimeSpan.Zero)
        {
            return broken;
        }

        var fresh = _channels.Create(session.PipeName);
        INetMeterChannel old;
        lock (_gate)
        {
            if (_epoch != epoch || !ReferenceEquals(_session, session))
            {
                fresh.Dispose();
                return broken;
            }

            old = session.Channel;
            session.Channel = fresh;
        }

        old.Dispose();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (!fresh.TryConnect(TimeSpan.FromMilliseconds(Math.Min(500, budget.TotalMilliseconds / 2)))
            || fresh.ServerProcessId != session.Process.Id)
        {
            // Closed here, so the cleanup that follows never sends a Stop to a pipe that is not the meter's.
            fresh.Dispose();
            return broken;
        }

        return fresh.Read(budget - clock.Elapsed);
    }

    private NetMeterState StartSafely(long epoch)
    {
        try
        {
            return StartCore(epoch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Finish(epoch, new NetMeterState(NetMeterStatus.Failed, Describe(CollectionStrings.NetNoAnswer)));
        }
    }

    private NetMeterState StartCore(long epoch)
    {
        if (!_exists(_exePath))
        {
            return Finish(epoch, new NetMeterState(NetMeterStatus.Unsupported, Describe(CollectionStrings.NetMissing)));
        }

        // Refuse what the meter would refuse BEFORE any prompt: a command line it will not accept is not worth a UAC dialog.
        var me = _identity();
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var pipeName = NetMeterArguments.PipePrefix + token;
        if (me.ProcessId == 0 || me.CreatedFileTime <= 0 || !NetMeterArguments.IsSidText(me.UserSid) || !NetMeterArguments.IsPipeName(pipeName))
        {
            return Finish(epoch, new NetMeterState(NetMeterStatus.Failed, Describe(CollectionStrings.NetNoIdentity)));
        }

        var arguments = NetMeterArguments.BuildCommandLine(me.ProcessId, me.CreatedFileTime, token, me.UserSid);
        var launch = _launcher.Launch(_exePath, arguments, elevate: !_isElevated());
        switch (launch.Status)
        {
            case NetMeterLaunchStatus.Declined:
                return Finish(epoch, new NetMeterState(NetMeterStatus.Declined));
            case NetMeterLaunchStatus.Started when launch.Process is not null:
                break;
            default:
                return Finish(epoch, new NetMeterState(NetMeterStatus.Failed, Describe(CollectionStrings.NetLaunchFailed, launch.Error)));
        }

        // From here the meter exists. If this start does not end in Running, it is told to stop (or, when nothing could be
        // told, ends by itself: it gives up after 30 s without a client).
        var process = launch.Process;
        INetMeterChannel channel;
        try
        {
            channel = _channels.Create(pipeName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            process.Dispose();
            throw;
        }

        try
        {
            var failure = Connect(channel, process);
            if (failure is not null)
            {
                // Nothing is sent: either nobody is on the pipe, or the one that is is not the process we started.
                channel.Dispose();
                process.Dispose();
                return Finish(epoch, new NetMeterState(NetMeterStatus.Failed, failure));
            }

            var first = channel.Read(_timings.FirstRead);
            if (first.Reading is null)
            {
                var why = DescribeFailure(process, first.Failure);
                Abandon(channel, process);
                return Finish(epoch, new NetMeterState(NetMeterStatus.Failed, why));
            }

            if (!first.Reading.SessionHealthy)
            {
                var why = DescribeSessionError(first.Reading.ErrorCode);
                Abandon(channel, process);
                return Finish(epoch, new NetMeterState(NetMeterStatus.Failed, why));
            }

            bool superseded;
            lock (_gate)
            {
                superseded = _epoch != epoch;
                if (!superseded)
                {
                    _session = new Session(channel, process, pipeName);
                    _generation++;
                    _watch = new Timer(static s => ((NetMeterControl)s!).WatchSafely(), this, _timings.Watch, _timings.Watch);
                }
            }

            if (superseded)
            {
                // Stop (or another start) came while the prompt was open: this meter is nobody's. Nothing changes.
                Abandon(channel, process);
                return State;
            }

            return Finish(epoch, new NetMeterState(NetMeterStatus.Running));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Abandon(channel, process);
            throw;
        }
    }

    /// <summary>Waits for the pipe in short slices (so an exit of the meter is noticed), then checks it is the started process's pipe. Null when connected.</summary>
    private string? Connect(INetMeterChannel channel, INetMeterProcess process)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!channel.TryConnect(_timings.ConnectSlice))
        {
            if (process.HasExited)
            {
                return Describe(CollectionStrings.NetExited, process.ExitCode);
            }

            if (clock.Elapsed >= _timings.Connect)
            {
                return Describe(CollectionStrings.NetNoAnswer);
            }
        }

        // The name carries a random token, but any process of this user can see a command line: whoever answers must be the
        // process we started, or nothing is sent to it and nothing is trusted from it.
        return channel.ServerProcessId == process.Id ? null : Describe(CollectionStrings.NetNotOurPipe);
    }

    private NetMeterState Finish(long epoch, NetMeterState state)
    {
        var changed = false;
        lock (_gate)
        {
            if (_epoch != epoch)
            {
                return _state;
            }

            _starting = null;
            changed = SetStateLocked(state);
        }

        if (changed)
        {
            Raise();
        }

        return state;
    }

    private void StopCore(bool waitForExit)
    {
        Session? session;
        bool changed;
        lock (_gate)
        {
            _epoch++;
            _starting = null;
            session = TakeSessionLocked();
            changed = SetStateLocked(new NetMeterState(NetMeterStatus.Off));
        }

        if (changed)
        {
            Raise();
        }

        if (session is null)
        {
            return;
        }

        session.Channel.SendStop();
        session.Channel.Dispose();
        if (waitForExit)
        {
            _ = session.Process.WaitForExit(_timings.StopWait);
        }

        session.Process.Dispose();
    }

    private void Fail(long epoch, Session session, NetMeterFailure failure) =>
        FailWith(epoch, DescribeFailure(session.Process, failure));

    private void FailWith(long epoch, string detail)
    {
        Session? session;
        bool changed;
        lock (_gate)
        {
            if (_epoch != epoch)
            {
                return;
            }

            _epoch++;
            session = TakeSessionLocked();
            changed = SetStateLocked(new NetMeterState(NetMeterStatus.Failed, detail));
        }

        if (changed)
        {
            Raise();
        }

        if (session is not null)
        {
            Abandon(session.Channel, session.Process);
        }
    }

    /// <summary>An idle front end (paused, or a one-shot tool) never reads, so it would not notice a meter that died: look now and then.</summary>
    private void WatchSafely()
    {
        try
        {
            Session? session;
            long epoch;
            lock (_gate)
            {
                if (_state.Status != NetMeterStatus.Running || _session is null)
                {
                    return;
                }

                session = _session;
                epoch = _epoch;
            }

            if (session.Process.HasExited)
            {
                FailWith(epoch, Describe(CollectionStrings.NetExited, session.Process.ExitCode));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A timer callback must never throw; the next look (or the next read) will see the same thing.
        }
    }

    private Session? TakeSessionLocked()
    {
        var session = _session;
        _session = null;
        _watch?.Dispose();
        _watch = null;
        return session;
    }

    private bool SetStateLocked(NetMeterState state)
    {
        if (_state == state)
        {
            return false;
        }

        Volatile.Write(ref _state, state);
        return true;
    }

    private void Raise()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A handler that throws must not break the meter.
        }
    }

    /// <summary>Tells a meter that is not going to be used to stop, and lets go of it. It cannot be killed: it is elevated, we are not.</summary>
    private static void Abandon(INetMeterChannel channel, INetMeterProcess process)
    {
        channel.SendStop();
        channel.Dispose();
        process.Dispose();
    }

    private static string DescribeFailure(INetMeterProcess process, NetMeterFailure failure)
    {
        if (process.HasExited)
        {
            return Describe(CollectionStrings.NetExited, process.ExitCode);
        }

        return failure switch
        {
            NetMeterFailure.Timeout => Describe(CollectionStrings.NetNoAnswer),
            NetMeterFailure.BadReply => Describe(CollectionStrings.NetBadReply),
            _ => Describe(CollectionStrings.NetPipeLost),
        };
    }

    /// <summary>What the meter's session error (a Win32 error from the ETW calls) means, in plain words.</summary>
    internal static string DescribeSessionError(uint code) => code switch
    {
        5 => Describe(CollectionStrings.NetSessionDenied),
        995 or 4201 => Describe(CollectionStrings.NetSessionStopped),
        1450 or 1455 => Describe(CollectionStrings.NetSessionResources),
        _ => Describe(CollectionStrings.NetSessionError, unchecked((int)code)),
    };

    private static string Describe(string key, int? code = null) =>
        code is null
            ? Strings.Get(key)
            : Strings.Format(key, new Dictionary<string, string> { ["code"] = code.Value.ToString(CultureInfo.InvariantCulture) });

    /// <summary>The live connection. <see cref="Channel"/> is replaced when the pipe is reconnected.</summary>
    private sealed class Session(INetMeterChannel channel, INetMeterProcess process, string pipeName)
    {
        public INetMeterChannel Channel { get; set; } = channel;

        public INetMeterProcess Process { get; } = process;

        public string PipeName { get; } = pipeName;
    }
}
