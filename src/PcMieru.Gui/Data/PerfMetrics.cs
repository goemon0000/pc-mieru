using System.Globalization;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Gui;

internal enum PerfKind
{
    Cpu,
    Memory,
    Gpu,
    Disk,
    Network,
}

/// <summary>One thing the Performance page can show: the CPU, the memory, one GPU, one disk, one network adapter.</summary>
internal sealed record PerfPanel(PerfKind Kind, string DeviceId, string Name)
{
    /// <summary>"cpu", "memory", "gpu:1234": stable while the device exists, so the selection survives a refresh.</summary>
    public string Key => DeviceId.Length == 0 ? PerfMetrics.Argument(Kind) : PerfMetrics.Argument(Kind) + ":" + DeviceId;
}

/// <summary>One graph of a panel: what it is called, how many lines it has and how its axis works.</summary>
internal readonly record struct GraphSpec(string TitleKey, int Series, double? FixedMaximum, double AutoFloor, bool BytesAxis, string[] SeriesKeys);

/// <summary>One "caption: value" of the panel's fact list. A value that is missing is a grey dash with the reason as its tip.</summary>
internal readonly record struct StatLine(string Label, string Value, string? Tip, bool Dim);

/// <summary>
/// The facts that do not change second to second (CPU model, cache, memory slots), read once from the hardware model.
/// Every field keeps its own status, so a field the PC could not give shows why instead of a guess.
/// </summary>
internal sealed record PerfFacts(
    Value<string> CpuName,
    Value<int> PhysicalCores,
    Value<int> BaseClockMhz,
    Value<long> L1CacheBytes,
    Value<long> L2CacheBytes,
    Value<long> L3CacheBytes,
    Value<int> SlotsUsed,
    Value<int> SlotsTotal,
    Value<string> MemoryType,
    Value<int> MemorySpeedMtPerSec)
{
    private const string NotRead = "hardware";

    public static PerfFacts Empty { get; } = new(
        Value.NotCollected<string>(NotRead),
        Value.NotCollected<int>(NotRead),
        Value.NotCollected<int>(NotRead),
        Value.NotCollected<long>(NotRead),
        Value.NotCollected<long>(NotRead),
        Value.NotCollected<long>(NotRead),
        Value.NotCollected<int>(NotRead),
        Value.NotCollected<int>(NotRead),
        Value.NotCollected<string>(NotRead),
        Value.NotCollected<int>(NotRead));

    /// <summary>Reads the CPU facts from one hardware answer and the memory facts from another (each asked for its own category).</summary>
    public static PerfFacts From(HardwareInfo cpuInfo, HardwareInfo memoryInfo)
    {
        var memory = memoryInfo.Memory;
        var module = memory.TryGet(out var m) ? m.Modules.FirstOrDefault(x => x.ConfiguredSpeedMtPerSec.IsOk) ?? m.Modules.FirstOrDefault() : null;
        return new PerfFacts(
            Pick(cpuInfo.Cpu, c => c.Name),
            Pick(cpuInfo.Cpu, c => c.PhysicalCores),
            Pick(cpuInfo.Cpu, c => c.BaseClockMhz),
            Pick(cpuInfo.Cpu, c => c.L1CacheBytes),
            Pick(cpuInfo.Cpu, c => c.L2CacheBytes),
            Pick(cpuInfo.Cpu, c => c.L3CacheBytes),
            Pick(memory, x => x.SlotsUsed),
            Pick(memory, x => x.SlotsTotal),
            module is null ? Pick(memory, _ => Value.Unsupported<string>(memory.Source, "no module")) : module.Type.Map(t => t.ToString().ToUpperInvariant()),
            module is null ? Pick(memory, _ => Value.Unsupported<int>(memory.Source, "no module")) : module.ConfiguredSpeedMtPerSec);
    }

    /// <summary>A leaf of an outer value. When the outer value is missing, so is the leaf, for the same reason.</summary>
    private static Value<T> Pick<TOuter, T>(Value<TOuter> outer, Func<TOuter, Value<T>> leaf) =>
        outer.TryGet(out var o)
            ? leaf(o)
            : new Value<T>(default, outer.Status == ValueStatus.Ok ? ValueStatus.NotCollected : outer.Status, outer.Source, outer.Detail);
}

/// <summary>
/// What the Performance page shows, as plain functions of a snapshot: which panels exist, the number for each graph
/// line, the headline value and the fact list. No controls here, so all of it is tested without a window.
/// </summary>
internal static class PerfMetrics
{
    public static string Argument(PerfKind kind) => kind switch
    {
        PerfKind.Cpu => "cpu",
        PerfKind.Memory => "memory",
        PerfKind.Gpu => "gpu",
        PerfKind.Disk => "disk",
        _ => "network",
    };

    /// <summary>CPU, memory, then every GPU, disk and adapter that is up. Adapters that are down are not worth a panel.</summary>
    public static List<PerfPanel> Panels(SystemInfo system)
    {
        var panels = new List<PerfPanel>(4 + system.Gpus.Count + system.Disks.Count + system.Nics.Count)
        {
            new(PerfKind.Cpu, string.Empty, Strings.Get("gui.perf.cpu")),
            new(PerfKind.Memory, string.Empty, Strings.Get("gui.perf.memory")),
        };
        panels.AddRange(system.Gpus.Select(g => new PerfPanel(PerfKind.Gpu, g.Id, g.Name)));
        panels.AddRange(system.Disks.Select(d => new PerfPanel(PerfKind.Disk, d.Id, Strings.Format("gui.perf.disk", Fmt.Dict(("name", d.Name))))));
        panels.AddRange(system.Nics.Where(n => n.IsUp).Select(n => new PerfPanel(PerfKind.Network, n.Id, n.Name)));
        return panels;
    }

    public static GraphSpec[] Graphs(PerfKind kind) => kind switch
    {
        PerfKind.Cpu => [new("gui.perf.g.cpu", 1, 100, 1, false, [])],
        PerfKind.Memory => [new("gui.perf.g.memory", 1, 100, 1, false, [])],
        PerfKind.Gpu => [new("gui.perf.g.gpu", 1, 100, 1, false, []), new("gui.perf.g.gpumem", 1, 100, 1, false, [])],
        PerfKind.Disk => [new("gui.perf.g.diskactive", 1, 100, 1, false, []), new("gui.perf.g.diskrate", 2, null, 100 * 1024, true, ["gui.perf.read", "gui.perf.write"])],
        _ => [new("gui.perf.g.netrate", 2, null, 100 * 1024, true, ["gui.perf.send", "gui.perf.receive"])],
    };

    /// <summary>One point of one line of one graph. NaN when the number was not collected or the device is gone: the line breaks there.</summary>
    public static double Sample(PerfPanel panel, SystemInfo system, int graph, int series)
    {
        switch (panel.Kind)
        {
            case PerfKind.Cpu:
                return Number(system.Cpu.TotalPercent);
            case PerfKind.Memory:
                return system.Memory.TotalPhysicalBytes > 0
                    ? (system.Memory.TotalPhysicalBytes - system.Memory.AvailablePhysicalBytes) * 100.0 / system.Memory.TotalPhysicalBytes
                    : double.NaN;
            case PerfKind.Gpu:
                var gpu = Find(system.Gpus, g => g.Id == panel.DeviceId);
                if (gpu is null)
                {
                    return double.NaN;
                }

                return graph == 0
                    ? Number(gpu.UtilizationPercent)
                    : gpu.DedicatedUsedBytes.TryGet(out var used) && gpu.DedicatedTotalBytes.TryGet(out var total) && total > 0 ? used * 100.0 / total : double.NaN;
            case PerfKind.Disk:
                var disk = Find(system.Disks, d => d.Id == panel.DeviceId);
                if (disk is null)
                {
                    return double.NaN;
                }

                return graph == 0 ? Number(disk.ActivePercent) : Number(series == 0 ? disk.ReadBytesPerSec : disk.WriteBytesPerSec);
            default:
                var nic = Find(system.Nics, n => n.Id == panel.DeviceId);
                return nic is null ? double.NaN : Number(series == 0 ? nic.SendBytesPerSec : nic.ReceiveBytesPerSec);
        }
    }

    /// <summary>The short current value for the list on the left and the big number of the graph.</summary>
    public static string Headline(PerfPanel panel, SystemInfo system)
    {
        switch (panel.Kind)
        {
            case PerfKind.Cpu:
                return Fmt.Percent(system.Cpu.TotalPercent);
            case PerfKind.Memory:
                var total = Math.Max(1, system.Memory.TotalPhysicalBytes);
                var used = Math.Max(0, system.Memory.TotalPhysicalBytes - system.Memory.AvailablePhysicalBytes);
                return Fmt.Bytes(used) + " / " + Fmt.Bytes(system.Memory.TotalPhysicalBytes) + " (" + (used * 100.0 / total).ToString("0", CultureInfo.CurrentCulture) + " %)";
            case PerfKind.Gpu:
                return Find(system.Gpus, g => g.Id == panel.DeviceId) is { } gpu ? Fmt.Percent(gpu.UtilizationPercent) : Fmt.Dash;
            case PerfKind.Disk:
                return Find(system.Disks, d => d.Id == panel.DeviceId) is { } disk ? Fmt.Percent(disk.ActivePercent) : Fmt.Dash;
            default:
                if (Find(system.Nics, n => n.Id == panel.DeviceId) is not { } nic)
                {
                    return Fmt.Dash;
                }

                return Fmt.Rate(nic.SendBytesPerSec, nic.ReceiveBytesPerSec);
        }
    }

    /// <summary>The line under the panel's heading (the CPU model, the memory size). Empty when there is nothing to say.</summary>
    public static string Detail(PerfPanel panel, SystemInfo system, PerfFacts facts) => panel.Kind switch
    {
        PerfKind.Cpu => facts.CpuName.GetOr(string.Empty),
        PerfKind.Memory => Strings.Format("gui.perf.total", Fmt.Dict(("size", Fmt.Bytes(system.Memory.TotalPhysicalBytes)))),
        PerfKind.Network => Find(system.Nics, n => n.Id == panel.DeviceId) is { } nic && nic.LinkSpeedBitsPerSec.TryGet(out var bits)
            ? Strings.Format("gui.perf.link", Fmt.Dict(("speed", Speed(bits))))
            : string.Empty,
        _ => string.Empty,
    };

    public static List<StatLine> Stats(PerfPanel panel, Snapshot snapshot, PerfFacts facts)
    {
        var system = snapshot.System;
        var lines = new List<StatLine>(16);
        switch (panel.Kind)
        {
            case PerfKind.Cpu:
                lines.Add(Line("gui.perf.s.usage", system.Cpu.TotalPercent, v => v.ToString("0.0", CultureInfo.CurrentCulture) + " %"));
                lines.Add(Line("gui.perf.s.speed", system.Cpu.CurrentMhz, Ghz));
                lines.Add(Plain("gui.perf.s.logical", system.Cpu.LogicalProcessors.ToString(CultureInfo.CurrentCulture)));
                lines.Add(Line("gui.perf.s.cores", facts.PhysicalCores, v => v.ToString(CultureInfo.CurrentCulture)));
                lines.Add(Line("gui.perf.s.base", facts.BaseClockMhz, v => Ghz(v)));
                lines.Add(Line("gui.perf.s.l1", facts.L1CacheBytes, Fmt.Bytes));
                lines.Add(Line("gui.perf.s.l2", facts.L2CacheBytes, Fmt.Bytes));
                lines.Add(Line("gui.perf.s.l3", facts.L3CacheBytes, Fmt.Bytes));
                lines.Add(Plain("gui.perf.s.processes", snapshot.Processes.Count.ToString(CultureInfo.CurrentCulture)));
                lines.Add(Plain("gui.perf.s.uptime", Fmt.Uptime(system.UptimeSec)));
                break;
            case PerfKind.Memory:
                var memory = system.Memory;
                lines.Add(Plain("gui.perf.s.used", Fmt.Bytes(Math.Max(0, memory.TotalPhysicalBytes - memory.AvailablePhysicalBytes))));
                lines.Add(Plain("gui.perf.s.available", Fmt.Bytes(memory.AvailablePhysicalBytes)));
                lines.Add(Commit(memory));
                lines.Add(Line("gui.perf.s.cache", memory.CacheBytes, Fmt.Bytes));
                lines.Add(Line("gui.perf.s.paged", memory.PagedPoolBytes, Fmt.Bytes));
                lines.Add(Line("gui.perf.s.nonpaged", memory.NonPagedPoolBytes, Fmt.Bytes));
                lines.Add(Slots(facts));
                lines.Add(Line("gui.perf.s.memtype", facts.MemoryType, v => v));
                lines.Add(Line("gui.perf.s.memspeed", facts.MemorySpeedMtPerSec, v => v.ToString(CultureInfo.CurrentCulture) + " MT/s"));
                break;
            case PerfKind.Gpu:
                if (Find(system.Gpus, g => g.Id == panel.DeviceId) is not { } gpu)
                {
                    break;
                }

                lines.Add(Line("gui.perf.s.usage", gpu.UtilizationPercent, v => v.ToString("0.0", CultureInfo.CurrentCulture) + " %"));
                lines.Add(Dedicated(gpu));
                lines.Add(Line("gui.perf.s.shared", gpu.SharedUsedBytes, Fmt.Bytes));
                lines.Add(Line("gui.perf.s.temp", gpu.TemperatureC, v => Strings.Format("gui.fmt.temp", Fmt.Dict(("value", v.ToString("0", CultureInfo.CurrentCulture))))));
                lines.Add(Line("gui.perf.s.power", gpu.PowerWatts, v => v.ToString("0.0", CultureInfo.CurrentCulture) + " W"));
                lines.Add(Line("gui.perf.s.fan", gpu.FanPercent, v => v.ToString("0", CultureInfo.CurrentCulture) + " %"));
                lines.Add(Line("gui.perf.s.clock", gpu.ClockMhz, v => v.ToString("0", CultureInfo.CurrentCulture) + " MHz"));
                foreach (var engine in gpu.Engines.OrderBy(e => e.Type, StringComparer.OrdinalIgnoreCase).Take(MaxEngines))
                {
                    lines.Add(new StatLine(
                        Strings.Format("gui.perf.s.engine", Fmt.Dict(("type", engine.Type))),
                        engine.Percent.TryGet(out var p) ? p.ToString("0.0", CultureInfo.CurrentCulture) + " %" : Fmt.Dash,
                        engine.Percent.IsOk ? SourceTip(engine.Percent) : Fmt.MissingTip(engine.Percent),
                        !engine.Percent.IsOk));
                }

                break;
            case PerfKind.Disk:
                if (Find(system.Disks, d => d.Id == panel.DeviceId) is not { } disk)
                {
                    break;
                }

                lines.Add(Line("gui.perf.s.active", disk.ActivePercent, v => v.ToString("0.0", CultureInfo.CurrentCulture) + " %"));
                lines.Add(Line("gui.perf.s.read", disk.ReadBytesPerSec, v => Fmt.Bytes((long)Math.Max(0, v)) + "/s"));
                lines.Add(Line("gui.perf.s.write", disk.WriteBytesPerSec, v => Fmt.Bytes((long)Math.Max(0, v)) + "/s"));
                lines.Add(Line("gui.perf.s.response", disk.ResponseTimeMs, v => v.ToString("0.0", CultureInfo.CurrentCulture) + " ms"));
                break;
            default:
                var found = Find(system.Nics, n => n.Id == panel.DeviceId);
                lines.Add(found is null ? Plain("gui.perf.s.send", Fmt.Dash) : Line("gui.perf.s.send", found.SendBytesPerSec, v => Fmt.Bytes((long)Math.Max(0, v)) + "/s"));
                lines.Add(found is null ? Plain("gui.perf.s.receive", Fmt.Dash) : Line("gui.perf.s.receive", found.ReceiveBytesPerSec, v => Fmt.Bytes((long)Math.Max(0, v)) + "/s"));
                lines.Add(found is null ? Plain("gui.perf.s.link", Fmt.Dash) : Line("gui.perf.s.link", found.LinkSpeedBitsPerSec, Speed));
                lines.Add(Plain("gui.perf.s.state", Strings.Get(found is { IsUp: true } ? "gui.perf.s.up" : "gui.perf.s.down")));
                break;
        }

        return lines;
    }

    /// <summary>The most facts one panel can list (GPU: seven values plus the engines). The page keeps this many caption/value pairs.</summary>
    public const int MaxStats = 24;

    private const int MaxEngines = 12;

    public static string Ghz(double mhz) => (mhz / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " GHz";

    /// <summary>The adapter's negotiated link speed ("1 Gbps"). What the adapter agreed with the other end, not a measurement.</summary>
    public static string Speed(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => (bitsPerSecond / 1e9).ToString("0.##", CultureInfo.CurrentCulture) + " Gbps",
        >= 1_000_000 => (bitsPerSecond / 1e6).ToString("0.##", CultureInfo.CurrentCulture) + " Mbps",
        _ => (bitsPerSecond / 1e3).ToString("0.##", CultureInfo.CurrentCulture) + " Kbps",
    };

    private static double Number(Value<double> value) => value.TryGet(out var v) ? v : double.NaN;

    private static T? Find<T>(IReadOnlyList<T> items, Func<T, bool> match)
        where T : class
    {
        foreach (var item in items)
        {
            if (match(item))
            {
                return item;
            }
        }

        return null;
    }

    private static string SourceTip<T>(Value<T> value) =>
        string.IsNullOrWhiteSpace(value.Source) ? string.Empty : Strings.Format("gui.common.na.source", Fmt.Dict(("source", value.Source)));

    private static StatLine Plain(string labelKey, string value) => new(Strings.Get(labelKey), value, null, false);

    private static StatLine Line<T>(string labelKey, Value<T> value, Func<T, string> format) =>
        value.TryGet(out var v)
            ? new StatLine(Strings.Get(labelKey), format(v), SourceTip(value), false)
            : new StatLine(Strings.Get(labelKey), Fmt.Dash, Fmt.MissingTip(value), true);

    private static StatLine Commit(MemoryInfo memory)
    {
        var label = Strings.Get("gui.perf.s.commit");
        if (memory.CommitTotalBytes.TryGet(out var total) && memory.CommitLimitBytes.TryGet(out var limit))
        {
            return new StatLine(label, Fmt.Bytes(total) + " / " + Fmt.Bytes(limit), SourceTip(memory.CommitTotalBytes), false);
        }

        var missing = memory.CommitTotalBytes.IsOk ? memory.CommitLimitBytes : memory.CommitTotalBytes;
        return new StatLine(label, Fmt.Dash, Fmt.MissingTip(missing), true);
    }

    private static StatLine Dedicated(GpuInfo gpu)
    {
        var label = Strings.Get("gui.perf.s.dedicated");
        if (gpu.DedicatedUsedBytes.TryGet(out var used) && gpu.DedicatedTotalBytes.TryGet(out var total))
        {
            return new StatLine(label, Fmt.Bytes(used) + " / " + Fmt.Bytes(total), SourceTip(gpu.DedicatedUsedBytes), false);
        }

        var missing = gpu.DedicatedUsedBytes.IsOk ? gpu.DedicatedTotalBytes : gpu.DedicatedUsedBytes;
        return new StatLine(label, Fmt.Dash, Fmt.MissingTip(missing), true);
    }

    private static StatLine Slots(PerfFacts facts)
    {
        var label = Strings.Get("gui.perf.s.slots");
        if (facts.SlotsUsed.TryGet(out var used) && facts.SlotsTotal.TryGet(out var total))
        {
            return new StatLine(label, used.ToString(CultureInfo.CurrentCulture) + " / " + total.ToString(CultureInfo.CurrentCulture), SourceTip(facts.SlotsTotal), false);
        }

        var missing = facts.SlotsUsed.IsOk ? facts.SlotsTotal : facts.SlotsUsed;
        return new StatLine(label, Fmt.Dash, Fmt.MissingTip(missing), true);
    }
}
