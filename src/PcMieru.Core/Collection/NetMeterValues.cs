namespace PcMieru.Core.Collection;

/// <summary>
/// What a process's network values are while the meter is not Running. Shared by the real snapshot source and the Fakes so
/// both say the same thing for the same meter state.
/// </summary>
public static class NetMeterValues
{
    public const string Source = "etw:Microsoft-Windows-Kernel-Network";

    public const string OffDetail = "network meter is off";
    public const string StartingDetail = "network meter is starting";
    public const string DeclinedDetail = "the administrator prompt was declined";
    public const string FailedDetail = "network meter stopped working";
    public const string UnsupportedDetail = "network meter is not available here";

    /// <summary>The value every process gets while the meter is in <paramref name="state"/> (not Running).</summary>
    public static Value<double> Missing(NetMeterState state, string source = Source) => state.Status switch
    {
        NetMeterStatus.Off => Value.NotCollected<double>(source, OffDetail),
        NetMeterStatus.Starting => Value.NotCollected<double>(source, StartingDetail),
        NetMeterStatus.Declined => Value.Denied<double>(source, DeclinedDetail),
        NetMeterStatus.Failed => Value.Failed<double>(source, state.Detail ?? FailedDetail),
        NetMeterStatus.Unsupported => Value.Unsupported<double>(source, state.Detail ?? UnsupportedDetail),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state.Status, "Running has real values, not a missing one."),
    };
}

/// <summary>Fake meter: starts at once with no prompt. Starts Running so sample screens show the network column filled.</summary>
public sealed class FakeNetMeterControl : INetMeterControl, IFakeImplementation
{
    private NetMeterState _state = new(NetMeterStatus.Running);

    public NetMeterState State => _state;

    public event EventHandler? StateChanged;

    public Task<NetMeterState> StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Set(new NetMeterState(NetMeterStatus.Running));
        return Task.FromResult(_state);
    }

    public void Stop() => Set(new NetMeterState(NetMeterStatus.Off));

    private void Set(NetMeterState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Stand-in for the real meter until PCMieru.NetMeter.exe is wired up: always Unsupported, never prompts.
/// TODO(net-core): replace with the real NetMeterControl.
/// </summary>
public sealed class UnbuiltNetMeterControl : INetMeterControl
{
    public NetMeterState State { get; } = new(NetMeterStatus.Unsupported);

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task<NetMeterState> StartAsync(CancellationToken ct = default) => Task.FromResult(State);

    public void Stop()
    {
    }
}
