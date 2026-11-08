using System.Collections.Concurrent;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
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
    private readonly ConcurrentDictionary<HardwareCategory, (DateTime At, object Value)> _live = new();
    private readonly object _smbiosGate = new();
    private readonly object _guardGate = new();
    private (SmbiosTable? Table, string? Error)? _smbios;
    private (DeviceGuardState? State, int HResult)? _deviceGuard;

    private static readonly TimeSpan LiveLifetime = TimeSpan.FromSeconds(5);

    public async Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        bool Want(HardwareCategory c) => category is HardwareCategory.All || category == c;

        Task<Value<T>> Run<T>(HardwareCategory c, Func<Value<T>> collect) =>
            Want(c)
                ? Task.Run(() => (Value<T>)_cache.GetOrAdd(c, _ => collect()), ct)
                : Task.FromResult(Value.NotCollected<T>("hardware", "not requested"));

        // Charge level and uptime move all the time, so these two are re-read after a few seconds instead of being kept until Invalidate.
        Task<Value<T>> RunLive<T>(HardwareCategory c, Func<Value<T>> collect) =>
            Want(c)
                ? Task.Run(() => Live(c, collect), ct)
                : Task.FromResult(Value.NotCollected<T>("hardware", "not requested"));

        var cpu = Run(HardwareCategory.Cpu, () => CpuCollector.Collect(Smbios().Table));
        var memory = Run(HardwareCategory.Memory, () => MemoryCollector.Collect(Smbios().Table, Smbios().Error));
        var board = Run(HardwareCategory.Board, () => BoardCollector.Collect(Smbios().Table, Smbios().Error));
        var gpus = Run(HardwareCategory.Gpu, GpuCollector.Collect);
        var storage = Run(HardwareCategory.Storage, StorageCollector.Collect);
        var network = Run(HardwareCategory.Network, NetworkCollector.Collect);
        var displays = Run(HardwareCategory.Display, () => DisplayCollector.Collect());
        var audio = Run(HardwareCategory.Audio, AudioCollector.Collect);
        var usb = Run(HardwareCategory.Usb, UsbCollector.Collect);
        var battery = RunLive(HardwareCategory.Battery, BatteryCollector.Collect);
        var os = RunLive(HardwareCategory.Os, () => OsCollector.Collect(DeviceGuard, _clock()));

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
        _live.Clear();
        lock (_smbiosGate)
        {
            _smbios = null;
        }

        lock (_guardGate)
        {
            _deviceGuard = null;
        }
    }

    private Value<T> Live<T>(HardwareCategory category, Func<Value<T>> collect)
    {
        var now = _clock();
        if (_live.TryGetValue(category, out var entry) && now - entry.At < LiveLifetime && entry.Value is Value<T> cached)
        {
            return cached;
        }

        var fresh = collect();
        _live[category] = (now, fresh);
        return fresh;
    }

    /// <summary>The Win32_DeviceGuard answer, read once until <see cref="Invalidate"/>.</summary>
    private (DeviceGuardState? State, int HResult) DeviceGuard()
    {
        lock (_guardGate)
        {
            return _deviceGuard ??= OsCollector.ReadDeviceGuard();
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
