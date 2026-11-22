using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Hardware.Sensors;

/// <summary>A sensor answer together with where it came from: the readings (or why there are none) and the frame they are from.</summary>
internal readonly record struct GpuSensorView(Value<IReadOnlyList<GpuSensorReading>> Gpus, SourceOrigin Origin);

/// <summary>
/// An <see cref="ISensorClient"/> that also says which SensorHost frame an answer is from. Kept separate from the public interface: the
/// public contract does not change, and a client that does not know about frames (a test fake) simply does not implement this.
/// </summary>
internal interface IFramedSensorClient : ISensorClient
{
    /// <summary>The period (ms) the client currently asks SensorHost for: 1000 while a one-second lease is held, otherwise 2000.</summary>
    int RequestedPeriodMs { get; }

    /// <summary>True while the client knows the SensorHost child is being restarted (it was stopped after a failure and no frame has arrived since). Never blocks.</summary>
    bool IsHostRestarting { get; }

    /// <summary>
    /// The same answer as <see cref="ISensorClient.ReadGpuAsync"/>, with the frame it came from (id, monotonic capture time, nominal period).
    /// When there is no frame the origin says why (<see cref="SourceGap"/>). Like the plain read it never waits on a native vendor call.
    /// </summary>
    Task<GpuSensorView> ReadGpuFramedAsync(CancellationToken ct = default);
}
