using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Core.Hardware;

/// <summary>Deterministic sample hardware for the skeleton and for tests. Not read from this machine: every source is "fake".</summary>
public static class FakeHardwareData
{
    private const string Src = "fake";
    private const long Gib = 1024L * 1024 * 1024;

    private static Value<T> V<T>(T value) => Value.Ok(value, Src);

    private static Value<IReadOnlyList<string>> L(params string[] items) => Value.Ok<IReadOnlyList<string>>(items, Src);

    public static HardwareInfo Create(DateTime collectedAtUtc, HardwareCategory category = HardwareCategory.All)
    {
        bool Want(HardwareCategory c) => category is HardwareCategory.All || category == c;

        Value<T> Pick<T>(HardwareCategory c, Func<T> make)
            where T : notnull =>
            Want(c) ? V(make()) : Value.NotCollected<T>(Src, "not requested");

        return new HardwareInfo(
            HardwareInfo.CurrentSchemaVersion,
            DateTime.SpecifyKind(collectedAtUtc, DateTimeKind.Utc),
            Pick(HardwareCategory.Cpu, Cpu),
            Pick(HardwareCategory.Memory, Memory),
            Pick(HardwareCategory.Board, Board),
            Pick<IReadOnlyList<GpuHardware>>(HardwareCategory.Gpu, Gpus),
            Pick<IReadOnlyList<StorageDevice>>(HardwareCategory.Storage, Storage),
            Pick<IReadOnlyList<NetworkAdapter>>(HardwareCategory.Network, Network),
            Pick<IReadOnlyList<DisplayDevice>>(HardwareCategory.Display, Displays),
            Pick<IReadOnlyList<AudioEndpoint>>(HardwareCategory.Audio, Audio),
            Pick<IReadOnlyList<UsbDevice>>(HardwareCategory.Usb, Usb),
            Want(HardwareCategory.Battery) ? Value.Unsupported<BatteryHardware>(Src, "no battery (desktop)") : Value.NotCollected<BatteryHardware>(Src, "not requested"),
            Pick(HardwareCategory.Os, Os));
    }

    private static CpuHardware Cpu() => new(
        V("AMD Ryzen 9 7950X 16-Core Processor"),
        V("AuthenticAMD"),
        V(25),
        V(97),
        V(2),
        V(16),
        V(32),
        Value.Unsupported<int>(Src, "no hybrid core types on this CPU"),
        Value.Unsupported<int>(Src, "no hybrid core types on this CPU"),
        V(4500),
        V("AM5"),
        V(16L * 64 * 1024),
        V(16L * 1024 * 1024),
        V(64L * 1024 * 1024),
        L("SSE4.2", "AVX", "AVX2", "AVX-512", "AES", "SHA"));

    private static MemoryHardware Memory() => new(
        V(64 * Gib),
        V(4),
        V(2),
        V(256 * Gib),
        [
            Module("DIMM_A2", "Fake Memory Inc.", "FK5600-32G-01"),
            Module("DIMM_B2", "Fake Memory Inc.", "FK5600-32G-02"),
        ]);

    private static MemoryModule Module(string slot, string maker, string part) => new(
        V(slot),
        V(32 * Gib),
        V(MemoryType.Ddr5),
        V(5600),
        V(4800),
        V(maker),
        V(part),
        V("00000000"),
        V(2),
        V(1100),
        V("DIMM"));

    private static BoardHardware Board() => new(
        V("Fake Computer Co."),
        V("FAKE-X670 PRO"),
        V("1.xx"),
        V("SN-FAKE-0000"),
        V("Fake BIOS Vendor"),
        V("F31"),
        V("2025-10-01"),
        V(true),
        V(true),
        V(true),
        V("2.0"));

    private static IReadOnlyList<GpuHardware> Gpus() =>
    [
        new GpuHardware(
            V("NVIDIA GeForce RTX 5070"),
            V("NVIDIA"),
            V("999.99"),
            V("2026-10-01"),
            V("PCI\\VEN_10DE&DEV_0000"),
            V("luid_0x00000000_0x0000d1a2"),
            V(12 * Gib),
            V(32 * Gib),
            V("98.05.xx.00.01"),
            V(5),
            V(16),
            V(250.0)),
    ];

    private static IReadOnlyList<StorageDevice> Storage() =>
    [
        new StorageDevice(
            0,
            V("Samsung SSD 980 PRO 2TB"),
            V("S6B0NG0R000000X"),
            V("5B2QGXA7"),
            V(StorageBus.Nvme),
            V(2_000_398_934_016),
            V(true),
            V("GPT"),
            [new StorageVolume("C:", "System", "NTFS", V(1_999_000_000_000), V(1_100_000_000_000))]),
        new StorageDevice(
            1,
            V("ST4000DM004-2CV104"),
            V("ZFN00000"),
            V("0001"),
            V(StorageBus.Sata),
            V(4_000_787_030_016),
            V(false),
            V("GPT"),
            [new StorageVolume("D:", "Data", "NTFS", V(3_999_000_000_000), V(2_000_000_000_000))]),
    ];

    private static IReadOnlyList<NetworkAdapter> Network() =>
    [
        new NetworkAdapter(
            V("Intel(R) Ethernet Controller I225-V"),
            V(NetworkKind.Ethernet),
            V(2_500_000_000L),
            V("00-00-5E-00-53-01"),
            L("192.0.2.10", "fe80::1"),
            L("192.0.2.1"),
            V("1.1.4.43"),
            Value.Unsupported<string>(Src, "not a wireless adapter"),
            Value.Unsupported<string>(Src, "not a wireless adapter"),
            Value.Unsupported<int>(Src, "not a wireless adapter")),
    ];

    private static IReadOnlyList<DisplayDevice> Displays() =>
    [
        new DisplayDevice(
            V("Fake Display Corp."),
            V("FD-2790Q"),
            V(2024),
            V(14),
            V(597),
            V(336),
            V("2560x1440 @ 165 Hz"),
            V("2560x1440"),
            V(165.0),
            V(false),
            V(DisplayConnection.DisplayPort),
            V("NVIDIA GeForce RTX 5070")),
    ];

    private static IReadOnlyList<AudioEndpoint> Audio() =>
    [
        new AudioEndpoint(V("Speakers (Fake Audio)"), V(AudioDirection.Output), V(true), V(48000), V(24)),
        new AudioEndpoint(V("Microphone (Fake Audio)"), V(AudioDirection.Input), V(true), V(48000), V(16)),
    ];

    private static IReadOnlyList<UsbDevice> Usb() =>
    [
        new UsbDevice(V("USB Composite Device"), V("046D"), V("C52B"), V("Fake Peripherals"), V("USB\\ROOT_HUB30")),
    ];

    private static OsHardware Os() => new(
        V("Windows 11 Pro"),
        V("25H2"),
        V("10.0.26300"),
        V("x64"),
        V(93_784L),
        V("Balanced"),
        V(true),
        V(true));
}

public sealed class FakeHardwareProvider : IHardwareProvider, IFakeImplementation
{
    private readonly Func<DateTime> _clock;

    public FakeHardwareProvider(Func<DateTime>? clock = null) => _clock = clock ?? (static () => DateTime.UtcNow);

    public Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(FakeHardwareData.Create(_clock(), category));
    }

    public void Invalidate()
    {
    }
}

public sealed class FakeStorageHealthProvider : IStorageHealthProvider, IFakeImplementation
{
    public Task<IReadOnlyList<DriveHealth>> ReadAsync(bool allowElevation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<DriveHealth> result =
        [
            new DriveHealth(0, "Samsung SSD 980 PRO 2TB", Value.Ok(Nvme(), "fake:nvme-health-log")),
            new DriveHealth(
                1,
                "ST4000DM004-2CV104",
                allowElevation
                    ? Value.Ok(Sata(), "fake:ata-smart")
                    : Value.Denied<StorageHealth>("fake:ata-smart", "needs administrator (pcmi health --elevate)")),
        ];
        return Task.FromResult(result);
    }

    private static StorageHealth Nvme() => new(
        StorageHealthKind.Nvme,
        Value.Ok(41.0, "fake:nvme-health-log"),
        Value.Ok(7.0, "fake:nvme-health-log"),
        Value.Ok(100.0, "fake:nvme-health-log"),
        Value.Ok(10.0, "fake:nvme-health-log"),
        Value.Ok(0, "fake:nvme-health-log"),
        Value.Ok(new U128(UInt128.Parse("48200000000000")), "fake:nvme-health-log"),
        Value.Ok(new U128(UInt128.Parse("91500000000000")), "fake:nvme-health-log"),
        Value.Ok(new U128(2210UL), "fake:nvme-health-log"),
        Value.Ok(new U128(612UL), "fake:nvme-health-log"),
        Value.Ok(new U128(18UL), "fake:nvme-health-log"),
        Value.Ok(new U128(0UL), "fake:nvme-health-log"),
        []);

    private static StorageHealth Sata() => new(
        StorageHealthKind.Sata,
        Value.Ok(34.0, "fake:ata-smart"),
        Value.NotCollected<double>("fake:ata-smart", "SATA HDD has no wear indicator"),
        Value.NotCollected<double>("fake:ata-smart"),
        Value.NotCollected<double>("fake:ata-smart"),
        Value.NotCollected<int>("fake:ata-smart"),
        Value.NotCollected<U128>("fake:ata-smart"),
        Value.NotCollected<U128>("fake:ata-smart"),
        Value.Ok(new U128(31000UL), "fake:ata-smart"),
        Value.Ok(new U128(2400UL), "fake:ata-smart"),
        Value.Ok(new U128(120UL), "fake:ata-smart"),
        Value.NotCollected<U128>("fake:ata-smart"),
        [
            new SmartAttribute(5, "Reallocated_Sector_Ct", 100, 100, 10, 0UL, true),
            new SmartAttribute(9, "Power_On_Hours", 60, 60, 0, 31000UL, true),
            new SmartAttribute(194, "Temperature_Celsius", 34, 40, 0, 34UL, false),
        ]);
}

public sealed class FakeSensorClient : ISensorClient, IFakeImplementation
{
    public Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<GpuSensorReading> gpus =
        [
            new GpuSensorReading(
                "luid_0x00000000_0x0000d1a2",
                "NVIDIA GeForce RTX 5070",
                Value.Ok(60.0, "fake:nvml"),
                Value.Ok(175.0, "fake:nvml"),
                Value.Ok(250.0, "fake:nvml"),
                Value.Unsupported<double>("fake:nvml", "this board reports no fan"),
                Value.Ok(2500.0, "fake:nvml")),
        ];
        return Task.FromResult(Value.Ok(gpus, "fake:sensorhost"));
    }

    public void Dispose()
    {
    }
}

public static class HardwareModule
{
    /// <summary>
    /// The real implementations, or the Fakes under <c>options.ForceFakes</c>. SensorHostClient starts PCMieru.SensorHost.exe only when
    /// the first GPU reading is asked for, and it is disposed with the services.
    /// </summary>
    public static void Register(ServiceRegistry registry, PcMieruOptions options)
    {
        Strings.Register(HardwareStrings.All);
        if (options.ForceFakes)
        {
            registry.Set<IHardwareProvider>(new FakeHardwareProvider(options.Clock));
            registry.Set<IStorageHealthProvider>(new FakeStorageHealthProvider());
            registry.Set<ISensorClient>(new FakeSensorClient());
            return;
        }

        var sensors = new SensorHostClient();
        registry.Set<ISensorClient>(sensors);
        registry.Set<IHardwareProvider>(new RealHardwareProvider(options.Clock, sensors));
        // The Helper is registered by the Management module, which comes after this one: look it up when a read needs it, not now.
        registry.Set<IStorageHealthProvider>(new RealStorageHealthProvider(new HelperStorageReader(() => registry.TryGet<IHelperClient>(out var helper) ? helper : null)));
    }
}

public static class HardwareStrings
{
    public static readonly StringEntry[] All =
    [
        new("hw.category.cpu", "CPU", "CPU"),
        new("hw.category.memory", "メモリ", "Memory"),
        new("hw.category.board", "マザーボード", "Motherboard"),
        new("hw.category.gpu", "GPU", "GPU"),
        new("hw.category.storage", "ストレージ", "Storage"),
        new("hw.category.network", "ネットワーク", "Network"),
        new("hw.category.display", "ディスプレイ", "Display"),
        new("hw.category.audio", "オーディオ", "Audio"),
        new("hw.category.usb", "USB", "USB"),
        new("hw.category.battery", "バッテリー", "Battery"),
        new("hw.category.os", "OS", "OS"),
        new("hw.limit.psu", "一般の ATX 電源は型番も出力も読み取れません", "A standard ATX power supply cannot report its model or wattage"),
        new("hw.limit.noBattery", "この PC にバッテリーはありません", "This PC has no battery"),
        new("hw.limit.sataSmart", "SATA の詳しい健康情報は、管理者の許可をもらったときだけ読めます", "Detailed SATA health can only be read after you approve administrator access"),
        new("hw.limit.wifiLocation", "Wi-Fi の名前は、Windows の位置情報の許可がないと読めません", "The Wi-Fi network name cannot be read without Windows location permission"),
        new("hw.limit.usbPower", "USB 機器の消費電力は読み取れません", "USB device power draw cannot be read"),
        new("hw.limit.linkNow", "リンク速度は今の値です。使っていないときは自動で下がります", "Link speed is the current value. It drops by itself when idle"),
        new("hw.limit.gpuVendor", "GPU の温度や電力は NVIDIA / AMD のドライバから読みます。それ以外の GPU は対象外です", "GPU temperature and power are read from the NVIDIA / AMD driver. Other GPUs are not covered"),
    ];
}
