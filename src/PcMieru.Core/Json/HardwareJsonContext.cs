using System.Text.Json.Serialization;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core;

// Owner C (Hardware + SensorHost). Register here: hardware roots and the T of every Value<T> in the hardware model.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HardwareInfo))]
[JsonSerializable(typeof(StorageHealthReport))]
[JsonSerializable(typeof(SensorFrame))]
[JsonSerializable(typeof(CpuHardware))]
[JsonSerializable(typeof(MemoryHardware))]
[JsonSerializable(typeof(BoardHardware))]
[JsonSerializable(typeof(BatteryHardware))]
[JsonSerializable(typeof(OsHardware))]
[JsonSerializable(typeof(StorageHealth))]
[JsonSerializable(typeof(IReadOnlyList<GpuHardware>))]
[JsonSerializable(typeof(IReadOnlyList<StorageDevice>))]
[JsonSerializable(typeof(IReadOnlyList<NetworkAdapter>))]
[JsonSerializable(typeof(IReadOnlyList<DisplayDevice>))]
[JsonSerializable(typeof(IReadOnlyList<AudioEndpoint>))]
[JsonSerializable(typeof(IReadOnlyList<UsbDevice>))]
[JsonSerializable(typeof(IReadOnlyList<GpuSensorReading>))]
[JsonSerializable(typeof(IReadOnlyList<double>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(MemoryType))]
[JsonSerializable(typeof(StorageBus))]
[JsonSerializable(typeof(NetworkKind))]
[JsonSerializable(typeof(DisplayConnection))]
[JsonSerializable(typeof(AudioDirection))]
public partial class HardwareJsonContext : JsonSerializerContext;
