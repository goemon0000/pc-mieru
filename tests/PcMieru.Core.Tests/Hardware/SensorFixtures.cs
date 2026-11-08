using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>Builders for sensor readings and GPU facts used by the SensorHost client, parser and merge tests.</summary>
internal static class SensorFixtures
{
    public const string Src = "test";

    public static GpuSensorExtras Extras(
        string? vbios = "98.05.28.00.9b",
        string? driver = "610.88",
        string? pciBusId = null,
        double? limitMin = 175,
        double? limitMax = 300,
        double? limitDefault = 250,
        int? gen = 5,
        int? width = 16,
        double? slowdownC = 88)
    {
        return new GpuSensorExtras(
            Vbios: vbios is null ? Value.Unsupported<string>(Src, "none") : Value.Ok(vbios, Src),
            DriverVersion: driver is null ? Value.Unsupported<string>(Src, "none") : Value.Ok(driver, Src),
            PcieGeneration: gen is null ? Value.Unsupported<int>(Src, "none") : Value.Ok(gen.Value, Src),
            PcieWidth: width is null ? Value.Unsupported<int>(Src, "none") : Value.Ok(width.Value, Src),
            PcieMaxGeneration: gen is null ? Value.Unsupported<int>(Src, "none") : Value.Ok(gen.Value, Src),
            PcieMaxWidth: width is null ? Value.Unsupported<int>(Src, "none") : Value.Ok(width.Value, Src),
            PowerLimitMinWatts: limitMin is null ? Value.Unsupported<double>(Src, "none") : Value.Ok(limitMin.Value, Src),
            PowerLimitMaxWatts: limitMax is null ? Value.Unsupported<double>(Src, "none") : Value.Ok(limitMax.Value, Src),
            PowerLimitDefaultWatts: limitDefault is null ? Value.Unsupported<double>(Src, "none") : Value.Ok(limitDefault.Value, Src),
            FanRpm: Value.Unsupported<double>(Src, "none"),
            MemoryClockMhz: Value.Unsupported<double>(Src, "none"),
            UtilizationPercent: Value.Unsupported<double>(Src, "none"),
            MemoryUsedBytes: Value.Unsupported<long>(Src, "none"),
            MemoryTotalBytes: Value.Unsupported<long>(Src, "none"),
            PciBusId: pciBusId)
        {
            TemperatureLimitC = slowdownC is null ? Value.Unsupported<double>(Src, "none") : Value.Ok(slowdownC.Value, Src),
        };
    }

    public static GpuSensorReading Reading(string id, string name = "NVIDIA GeForce RTX 5070", double temp = 40, double? limit = 262.5, GpuSensorExtras? extras = null)
    {
        return new GpuSensorReading(
            id,
            name,
            Value.Ok(temp, Src),
            Value.Ok(30.0, Src),
            limit is null ? Value.Unsupported<double>(Src, "none") : Value.Ok(limit.Value, Src),
            Value.Ok(0.0, Src),
            Value.Ok(727.0, Src),
            extras);
    }

    public static Value<IReadOnlyList<GpuSensorReading>> Readings(params GpuSensorReading[] readings) =>
        Value.Ok<IReadOnlyList<GpuSensorReading>>(readings, "sensorhost");

    public static string FrameLine(params GpuSensorReading[] readings) =>
        PcMieruJson.Serialize(new SensorFrame(SensorFrame.CurrentSchemaVersion, DateTime.UtcNow, Readings(readings)));

    public static GpuHardware Gpu(string? luid, string vendor = "NVIDIA", string? pciLocation = null, string name = "NVIDIA GeForce RTX 5070")
    {
        return new GpuHardware(
            Name: Value.Ok(name, "dxgi"),
            Vendor: Value.Ok(vendor, "dxgi"),
            DriverVersion: Value.Ok("32.0.15.8088", "registry"),
            DriverDate: Value.Unsupported<string>("registry", "none"),
            PciId: Value.Unsupported<string>("dxgi", "none"),
            Luid: luid is null ? Value.Unsupported<string>("dxgi", "none") : Value.Ok(luid, "dxgi"),
            DedicatedBytes: Value.Unsupported<long>("dxgi", "none"),
            SharedBytes: Value.Unsupported<long>("dxgi", "none"),
            Vbios: Value.NotCollected<string>("sensorhost", "comes from the sensor host"),
            PcieGeneration: Value.Unsupported<int>("dxgi", "none"),
            PcieWidth: Value.Unsupported<int>("dxgi", "none"),
            PowerLimitWatts: Value.NotCollected<double>("sensorhost", "comes from the sensor host"),
            Details: pciLocation is null
                ? null
                : new GpuDetails(
                    Kind: Value.Ok("discrete", "dxgi"),
                    SubsystemId: Value.Unsupported<string>("dxgi", "none"),
                    Revision: Value.Unsupported<int>("dxgi", "none"),
                    DriverProvider: Value.Unsupported<string>("registry", "none"),
                    DedicatedSystemBytes: Value.Unsupported<long>("dxgi", "none"),
                    PciLocation: Value.Ok(pciLocation, "kmt"),
                    PcieMaxGeneration: Value.Unsupported<int>("kmt", "none"),
                    PcieMaxWidth: Value.Unsupported<int>("kmt", "none")));
    }

    public static Value<IReadOnlyList<GpuHardware>> Gpus(params GpuHardware[] gpus) =>
        Value.Ok<IReadOnlyList<GpuHardware>>(gpus, "dxgi");
}
