using System.Collections.Concurrent;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Smbios;

namespace PcMieru.Core.Hardware;

/// <summary>
/// Real <see cref="IHardwareProvider"/>. Each category is collected once and cached until <see cref="Invalidate"/>, because most of it
/// (SMBIOS, EDID, driver data) cannot change while the app runs. A category that was not asked for is reported as NotCollected.
/// Nothing here needs administrator rights.
/// </summary>
public sealed class RealHardwareProvider(Func<DateTime>? clock = null) : IHardwareProvider
{
    private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
    private readonly ConcurrentDictionary<HardwareCategory, object> _cache = new();
    private readonly object _smbiosGate = new();
    private (SmbiosTable? Table, string? Error)? _smbios;

    public async Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        bool Want(HardwareCategory c) => category is HardwareCategory.All || category == c;

        Task<Value<T>> Run<T>(HardwareCategory c, Func<Value<T>> collect) =>
            Want(c)
                ? Task.Run(() => (Value<T>)_cache.GetOrAdd(c, _ => collect()), ct)
                : Task.FromResult(Value.NotCollected<T>("hardware", "not requested"));

        var cpu = Run(HardwareCategory.Cpu, () => CpuCollector.Collect(Smbios().Table));
        var memory = Run(HardwareCategory.Memory, () => MemoryCollector.Collect(Smbios().Table, Smbios().Error));
        var board = Run(HardwareCategory.Board, () => BoardCollector.Collect(Smbios().Table, Smbios().Error));
        var gpus = Run(HardwareCategory.Gpu, () => Value.NotCollected<IReadOnlyList<GpuHardware>>("hardware", "collector not written yet"));
        var storage = Run(HardwareCategory.Storage, StorageCollector.Collect);
        var network = Run(HardwareCategory.Network, () => Value.NotCollected<IReadOnlyList<NetworkAdapter>>("hardware", "collector not written yet"));
        var displays = Run(HardwareCategory.Display, () => Value.NotCollected<IReadOnlyList<DisplayDevice>>("hardware", "collector not written yet"));
        var audio = Run(HardwareCategory.Audio, () => Value.NotCollected<IReadOnlyList<AudioEndpoint>>("hardware", "collector not written yet"));
        var usb = Run(HardwareCategory.Usb, () => Value.NotCollected<IReadOnlyList<UsbDevice>>("hardware", "collector not written yet"));
        var battery = Run(HardwareCategory.Battery, () => Value.NotCollected<BatteryHardware>("hardware", "collector not written yet"));
        var os = Run(HardwareCategory.Os, () => Value.NotCollected<OsHardware>("hardware", "collector not written yet"));

        await Task.WhenAll(cpu, memory, board, gpus, storage, network, displays, audio, usb, battery, os).ConfigureAwait(false);
        return new HardwareInfo(
            HardwareInfo.CurrentSchemaVersion,
            _clock(),
            cpu.Result,
            memory.Result,
            board.Result,
            gpus.Result,
            storage.Result,
            network.Result,
            displays.Result,
            audio.Result,
            usb.Result,
            battery.Result,
            os.Result);
    }

    public void Invalidate()
    {
        _cache.Clear();
        lock (_smbiosGate)
        {
            _smbios = null;
        }
    }

    private (SmbiosTable? Table, string? Error) Smbios()
    {
        lock (_smbiosGate)
        {
            if (_smbios is null)
            {
                var table = SmbiosReader.Read(out var error);
                _smbios = (table, error);
            }

            return _smbios.Value;
        }
    }
}
