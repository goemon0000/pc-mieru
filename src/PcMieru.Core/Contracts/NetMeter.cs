using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core;

// Per-process network attribution (SPEC 4.1). Added after the five-area freeze as its own interface.
// The data comes from PCMieru.NetMeter.exe: an elevated, long-lived process that runs one ETW session
// (Microsoft-Windows-Kernel-Network) and hands per-process byte counters to the non-elevated front end over a
// locked-down named pipe. It is opt-in, because starting it costs one UAC prompt; viewing everything else never elevates.

/// <summary>Where the network meter is.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<NetMeterStatus>))]
public enum NetMeterStatus
{
    /// <summary>Not started (the default). Network values are NotCollected.</summary>
    Off,

    /// <summary>Waiting for the UAC prompt or for the first counters.</summary>
    Starting,

    /// <summary>Counters arrive. Network values are Ok (or carry their own status).</summary>
    Running,

    /// <summary>The user said no to the UAC prompt. Network values are Denied.</summary>
    Declined,

    /// <summary>Started but broke (ETW session refused, pipe lost, meter exited). Detail says why; values are Error.</summary>
    Failed,

    /// <summary>This build or machine cannot run it (Fakes, CI). Values are Unsupported.</summary>
    Unsupported,
}

/// <param name="Detail">Plain words for the UI (already localized), or null.</param>
public sealed record NetMeterState(NetMeterStatus Status, string? Detail = null);

/// <summary>
/// Starts and stops the network meter. One instance per front end, registered by the Collection module.
/// The snapshot source reads it: while it is not Running, every process's network values say why (see <see cref="NetMeterStatus"/>).
/// </summary>
public interface INetMeterControl
{
    NetMeterState State { get; }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Starts the meter: one UAC prompt unless this process is already elevated. Completes when the meter is Running or
    /// when it ended up Declined / Failed / Unsupported. Calling it while Starting or Running returns the current state.
    /// </summary>
    Task<NetMeterState> StartAsync(CancellationToken ct = default);

    /// <summary>Stops the meter (it also stops by itself when this process exits). Back to Off.</summary>
    void Stop();
}
