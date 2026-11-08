using System.Globalization;
using System.Text;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>
/// Every number the v1 rules compare against, in one place so the tests and docs/MCP.md say the same thing as the code.
/// A rule states a measured fact or a candidate explanation. It never calls something "wrong": a GPU at 100% while a
/// game runs is the GPU doing its job, so there is deliberately no rule about GPU utilization.
/// </summary>
public static class ObservationThresholds
{
    /// <summary>A logical CPU at or above this is "pegged".</summary>
    public const double CoreBusyPercent = 95;

    /// <summary>Single-core candidate only while the whole CPU is below this (otherwise many threads are busy, not one).</summary>
    public const double SingleCoreMaxTotalPercent = 50;

    /// <summary>The pegged state must be measured over at least this long. 900 ms, not 1000, because a 1 s timer jitters.</summary>
    public const double SingleCoreMinMs = 900;

    public const double CommitHighPercent = 90;

    public const double AvailableLowPercent = 10;

    public const double AvailableLowWarningPercent = 5;

    public const long AvailableLowBytes = 2L * 1024 * 1024 * 1024;

    public const long AvailableLowWarningBytes = 1024L * 1024 * 1024;

    public const double ProcessMemoryPercent = 50;

    public const double DiskBusyPercent = 90;

    /// <summary>"5 seconds or more". The run is measured by the intervals of the samples, so 5 samples of 0.99 s count.</summary>
    public const double DiskBusyMinMs = 5000 * 0.95;

    public const double DiskSlowResponseMs = 100;

    /// <summary>The GPU is "high" when it is within this many degrees of the limit the vendor API reports.</summary>
    public const double GpuTemperatureMarginC = 5;

    public const double NvmeLifeUsedNoticePercent = 80;

    public const double NvmeLifeUsedWarningPercent = 100;

    /// <summary>With no threshold from the drive, a spare this low is worth a mention.</summary>
    public const double NvmeSpareNoThresholdPercent = 10;
}

/// <summary>A GPU temperature with the limit the vendor API reported for it. No limit, no reading: the rule never guesses one.</summary>
public sealed record GpuThermalReading(string Id, string Name, double TemperatureC, double LimitC);

/// <summary>Where the engine gets "the limit the vendor API says" for a GPU. Registered by the area that talks to NVML/ADLX.</summary>
public interface IGpuTemperatureLimits
{
    /// <summary>Null when the vendor API did not give a limit for this GPU (Id is GpuInfo.Id).</summary>
    double? LimitC(string gpuId);
}

/// <summary>Nothing known about any limit: the GPU temperature rule stays silent.</summary>
public sealed class NoGpuTemperatureLimits : IGpuTemperatureLimits
{
    public double? LimitC(string gpuId) => null;
}

internal static class ObservationText
{
    public static string N(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A name from the machine (process, drive model) goes into Args, so it is stripped of control characters and capped.</summary>
    public static string Clean(string text, int max = 64)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (sb.Length >= max)
            {
                sb.Append('…');
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }

    public static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}

/// <summary>
/// The v1 observation rules as pure functions over snapshots. Same input, same output; nothing reads the clock or
/// the machine. The window is oldest first and may hold a single snapshot (a one-shot `pcmi perf`): a rule that needs
/// a duration then judges that snapshot over its own interval, and says nothing when the interval is too short.
/// </summary>
public static class ObservationRules
{
    /// <summary>History plus Current, oldest first, each moment once.</summary>
    public static IReadOnlyList<Snapshot> Window(ObservationInput input)
    {
        var list = new List<Snapshot>(input.History.Count + 1);
        foreach (var s in input.History.OrderBy(s => s.TakenAtUtc))
        {
            if (s.TakenAtUtc < input.Current.TakenAtUtc)
            {
                list.Add(s);
            }
        }

        list.Add(input.Current);
        return list;
    }

    /// <summary>The newest samples in a row that all satisfy <paramref name="qualifies"/>, and how many milliseconds they cover.</summary>
    internal static (List<Snapshot> Run, double CoveredMs) TrailingRun(IReadOnlyList<Snapshot> window, Func<Snapshot, bool> qualifies)
    {
        var run = new List<Snapshot>();
        for (var i = window.Count - 1; i >= 0 && qualifies(window[i]); i--)
        {
            run.Add(window[i]);
        }

        run.Reverse();
        return (run, run.Sum(s => (double)s.IntervalMs));
    }

    // ---- CPU -------------------------------------------------------------------------------------------------

    /// <summary>One logical CPU pegged while the rest of the machine is quiet: one thread may be the bottleneck (a candidate, not a fact).</summary>
    public static IEnumerable<Observation> SingleCoreSaturated(ObservationInput input)
    {
        var logical = input.Current.System.Cpu.PerLogicalPercent.Count;
        if (logical < 2)
        {
            yield break;
        }

        var pegged = PeggedCore(input.Current);
        if (pegged is not { } index)
        {
            yield break;
        }

        var (run, covered) = TrailingRun(Window(input), s => PeggedCore(s) == index);
        if (covered < ObservationThresholds.SingleCoreMinMs)
        {
            yield break;
        }

        var total = run.Average(s => s.System.Cpu.TotalPercent.GetOr(0));
        var core = run.Average(s => s.System.Cpu.PerLogicalPercent[index].GetOr(0));
        var share = 100.0 / logical;
        var top = input.Current.Processes
            .Where(p => p.Key.Pid != 0 && p.CpuPercent.TryGet(out var c) && c >= share * 0.5)
            .OrderByDescending(p => p.CpuPercent.GetOr(0))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var i = index.ToString(CultureInfo.InvariantCulture);
        yield return new Observation(
            "cpu.singleCoreSaturated",
            ObservationSeverity.Notice,
            ObservationCertainty.Candidate,
            "obs.cpu.singleCore",
            ObservationText.Args(
                ("cpu", i),
                ("total", ObservationText.N(total)),
                ("top", top.Length == 0 ? "-" : string.Join(", ", top.Select(p => ObservationText.Clean(p.Name))))),
            [
                new Evidence("cpu.total", ObservationText.N(total), "%"),
                new Evidence("cpu.logical" + i, ObservationText.N(core), "%"),
                new Evidence("window", ObservationText.N(covered / 1000.0), "s"),
            ],
            top.Select(p => p.Key).ToArray());
    }

    /// <summary>The one logical CPU at or above the busy line while the whole CPU is below the quiet line; null when it is not exactly one.</summary>
    private static int? PeggedCore(Snapshot snapshot)
    {
        var cpu = snapshot.System.Cpu;
        if (!cpu.TotalPercent.TryGet(out var total) || total >= ObservationThresholds.SingleCoreMaxTotalPercent)
        {
            return null;
        }

        int? found = null;
        for (var i = 0; i < cpu.PerLogicalPercent.Count; i++)
        {
            if (cpu.PerLogicalPercent[i].TryGet(out var one) && one >= ObservationThresholds.CoreBusyPercent)
            {
                if (found is not null)
                {
                    return null;
                }

                found = i;
            }
        }

        return found;
    }

    // ---- Memory ----------------------------------------------------------------------------------------------

    /// <summary>Commit charge above 90% of the commit limit: new allocations may start to fail. Looked at now, not averaged.</summary>
    public static IEnumerable<Observation> CommitHigh(ObservationInput input)
    {
        var mem = input.Current.System.Memory;
        if (!mem.CommitTotalBytes.TryGet(out var commit) || !mem.CommitLimitBytes.TryGet(out var limit) || limit <= 0)
        {
            yield break;
        }

        var percent = 100.0 * commit / limit;
        if (percent <= ObservationThresholds.CommitHighPercent)
        {
            yield break;
        }

        var top = TopByMemory(input.Current, 3, p => p.CommitBytes);
        yield return new Observation(
            "memory.commitHigh",
            ObservationSeverity.Warning,
            ObservationCertainty.Fact,
            "obs.memory.commitHigh",
            ObservationText.Args(("percent", ObservationText.N(percent, "0"))),
            [
                new Evidence("memory.commitPercent", ObservationText.N(percent), "%"),
                new Evidence("memory.commitMb", ObservationText.Mb(commit), "MB"),
                new Evidence("memory.commitLimitMb", ObservationText.Mb(limit), "MB"),
            ],
            top.Select(p => p.Key).ToArray());
    }

    /// <summary>
    /// Available physical memory is low. Evaluated on its own, apart from commit: the two fail differently (one is
    /// allocation failure, the other is paging). Low means both a small share of the total and a small absolute amount.
    /// </summary>
    public static IEnumerable<Observation> AvailableMemoryLow(ObservationInput input)
    {
        var mem = input.Current.System.Memory;
        if (mem.TotalPhysicalBytes <= 0 || mem.AvailablePhysicalBytes < 0)
        {
            yield break;
        }

        var percent = 100.0 * mem.AvailablePhysicalBytes / mem.TotalPhysicalBytes;
        if (percent >= ObservationThresholds.AvailableLowPercent || mem.AvailablePhysicalBytes >= ObservationThresholds.AvailableLowBytes)
        {
            yield break;
        }

        var warning = percent < ObservationThresholds.AvailableLowWarningPercent && mem.AvailablePhysicalBytes < ObservationThresholds.AvailableLowWarningBytes;
        var top = TopByMemory(input.Current, 3, p => p.PrivateWorkingSetBytes);
        yield return new Observation(
            "memory.availableLow",
            warning ? ObservationSeverity.Warning : ObservationSeverity.Notice,
            ObservationCertainty.Fact,
            "obs.memory.availableLow",
            ObservationText.Args(
                ("availableMb", ObservationText.Mb(mem.AvailablePhysicalBytes)),
                ("percent", ObservationText.N(percent, "0")),
                ("top", top.Count == 0 ? "-" : string.Join(", ", top.Select(p => ObservationText.Clean(p.Name) + " " + ObservationText.Mb(p.PrivateWorkingSetBytes) + " MB")))),
            [
                new Evidence("memory.availableMb", ObservationText.Mb(mem.AvailablePhysicalBytes), "MB"),
                new Evidence("memory.totalMb", ObservationText.Mb(mem.TotalPhysicalBytes), "MB"),
                new Evidence("memory.availablePercent", ObservationText.N(percent), "%"),
            ],
            top.Select(p => p.Key).ToArray());
    }

    /// <summary>One process holding more than half of the physical memory (its private working set, so shared pages are not double counted).</summary>
    public static IEnumerable<Observation> ProcessMemoryDominant(ObservationInput input)
    {
        var total = input.Current.System.Memory.TotalPhysicalBytes;
        if (total <= 0)
        {
            yield break;
        }

        foreach (var p in input.Current.Processes.Where(p => p.Key.Pid != 0).OrderByDescending(p => p.PrivateWorkingSetBytes).Take(1))
        {
            var percent = 100.0 * p.PrivateWorkingSetBytes / total;
            if (percent <= ObservationThresholds.ProcessMemoryPercent)
            {
                continue;
            }

            yield return new Observation(
                "memory.processDominant",
                ObservationSeverity.Notice,
                ObservationCertainty.Fact,
                "obs.memory.processDominant",
                ObservationText.Args(
                    ("name", ObservationText.Clean(p.Name)),
                    ("percent", ObservationText.N(percent, "0")),
                    ("usedMb", ObservationText.Mb(p.PrivateWorkingSetBytes))),
                [
                    new Evidence("process.privateWorkingSetMb", ObservationText.Mb(p.PrivateWorkingSetBytes), "MB"),
                    new Evidence("memory.totalMb", ObservationText.Mb(total), "MB"),
                    new Evidence("process.memoryPercent", ObservationText.N(percent), "%"),
                ],
                [p.Key]);
        }
    }

    private static IReadOnlyList<ProcessInfo> TopByMemory(Snapshot snapshot, int count, Func<ProcessInfo, long> size) =>
        snapshot.Processes
            .Where(p => p.Key.Pid != 0 && size(p) > 0)
            .OrderByDescending(size)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();

    // ---- Disk ------------------------------------------------------------------------------------------------

    /// <summary>A disk at 90% or more for 5 seconds or more, with the response time over those seconds when it was measured.</summary>
    public static IEnumerable<Observation> DiskBusy(ObservationInput input)
    {
        var window = Window(input);
        foreach (var disk in input.Current.System.Disks)
        {
            var id = disk.Id;
            var (run, covered) = TrailingRun(window, s => DiskOf(s, id) is { } d && d.ActivePercent.TryGet(out var a) && a >= ObservationThresholds.DiskBusyPercent);
            if (run.Count == 0 || covered < ObservationThresholds.DiskBusyMinMs)
            {
                continue;
            }

            var disks = run.Select(s => DiskOf(s, id)!).ToArray();
            var active = disks.Average(d => d.ActivePercent.GetOr(0));
            var responses = disks.Where(d => d.ResponseTimeMs.IsOk).Select(d => d.ResponseTimeMs.GetOr(0)).ToArray();
            var response = responses.Length > 0 ? responses.Average() : (double?)null;
            var heavy = input.Current.Processes
                .Where(p => p.Key.Pid != 0)
                .Select(p => (Process: p, Io: p.IoReadBytesPerSec.GetOr(0) + p.IoWriteBytesPerSec.GetOr(0)))
                .Where(x => x.Io > 0)
                .OrderByDescending(x => x.Io)
                .ThenBy(x => x.Process.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            var read = disks.Average(d => d.ReadBytesPerSec.GetOr(0));
            var write = disks.Average(d => d.WriteBytesPerSec.GetOr(0));

            var evidence = new List<Evidence>
            {
                new("disk.activePercent", ObservationText.N(active), "%"),
                new("window", ObservationText.N(covered / 1000.0), "s"),
                new("disk.readBytesPerSec", ObservationText.N(read, "0"), "B/s"),
                new("disk.writeBytesPerSec", ObservationText.N(write, "0"), "B/s"),
            };
            if (response is { } ms)
            {
                evidence.Add(new Evidence("disk.responseMs", ObservationText.N(ms), "ms"));
            }

            var args = new Dictionary<string, string>
            {
                ["disk"] = ObservationText.Clean(string.IsNullOrWhiteSpace(disk.Name) ? disk.Id : disk.Name),
                ["percent"] = ObservationText.N(active, "0"),
                ["seconds"] = ObservationText.N(Math.Round(covered / 1000.0), "0"),
                ["top"] = heavy.Length == 0 ? "-" : string.Join(", ", heavy.Select(x => ObservationText.Clean(x.Process.Name))),
            };
            if (response is { } r)
            {
                args["response"] = ObservationText.N(r);
            }

            yield return new Observation(
                "disk.busy:" + id,
                response is >= ObservationThresholds.DiskSlowResponseMs ? ObservationSeverity.Warning : ObservationSeverity.Notice,
                ObservationCertainty.Fact,
                response is null ? "obs.disk.busyNoResponse" : "obs.disk.busy",
                args,
                evidence,
                heavy.Select(x => x.Process.Key).ToArray());
        }
    }

    private static DiskInfo? DiskOf(Snapshot snapshot, string id) =>
        snapshot.System.Disks.FirstOrDefault(d => d.Id == id);

    // ---- GPU -------------------------------------------------------------------------------------------------

    /// <summary>GPU temperature near the limit the vendor API reported. Utilization is never judged: a busy GPU in a game is working as intended.</summary>
    public static IEnumerable<Observation> GpuTemperatureHigh(IEnumerable<GpuThermalReading> readings)
    {
        foreach (var reading in readings)
        {
            if (!double.IsFinite(reading.TemperatureC) || !double.IsFinite(reading.LimitC) || reading.LimitC <= 0)
            {
                continue;
            }

            if (reading.TemperatureC < reading.LimitC - ObservationThresholds.GpuTemperatureMarginC)
            {
                continue;
            }

            yield return new Observation(
                "gpu.temperatureHigh:" + reading.Id,
                ObservationSeverity.Warning,
                ObservationCertainty.Fact,
                "obs.gpu.temperatureHigh",
                ObservationText.Args(
                    ("gpu", ObservationText.Clean(reading.Name)),
                    ("temp", ObservationText.N(reading.TemperatureC, "0")),
                    ("limit", ObservationText.N(reading.LimitC, "0"))),
                [
                    new Evidence("gpu.temperatureC", ObservationText.N(reading.TemperatureC), "°C"),
                    new Evidence("gpu.limitC", ObservationText.N(reading.LimitC), "°C"),
                ],
                []);
        }
    }

    /// <summary>Pairs each GPU's temperature with its limit; a GPU with no limit or no temperature is left out.</summary>
    public static IEnumerable<GpuThermalReading> ThermalReadings(Snapshot snapshot, IGpuTemperatureLimits limits)
    {
        foreach (var gpu in snapshot.System.Gpus)
        {
            if (gpu.TemperatureC.TryGet(out var temperature) && limits.LimitC(gpu.Id) is { } limit)
            {
                yield return new GpuThermalReading(gpu.Id, gpu.Name, temperature, limit);
            }
        }
    }

    // ---- Storage health --------------------------------------------------------------------------------------

    private static readonly (int Bit, string Key)[] CriticalWarningBits =
    [
        (0, "spare"),
        (1, "temperature"),
        (2, "reliability"),
        (3, "readOnly"),
        (4, "backup"),
        (5, "persistent"),
    ];

    /// <summary>
    /// NVMe: any critical-warning flag, available spare at or under the drive's own threshold, and the percentage of
    /// life used. A drive whose health could not be read (denied, unsupported) says nothing here: not knowing is not a finding.
    /// SATA SMART attributes are not judged: their meaning differs per vendor.
    /// </summary>
    public static IEnumerable<Observation> StorageHealth(IReadOnlyList<DriveHealth>? drives)
    {
        if (drives is null)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (!drive.Health.TryGet(out var health) || health.Kind != StorageHealthKind.Nvme)
            {
                continue;
            }

            var name = drive.PhysicalDriveNumber.ToString(CultureInfo.InvariantCulture) + ": " + ObservationText.Clean(drive.Model);
            var n = drive.PhysicalDriveNumber.ToString(CultureInfo.InvariantCulture);

            if (health.CriticalWarning.TryGet(out var warning) && warning != 0)
            {
                var known = 0;
                foreach (var (bit, key) in CriticalWarningBits)
                {
                    if ((warning & (1 << bit)) == 0)
                    {
                        continue;
                    }

                    known |= 1 << bit;
                    yield return new Observation(
                        $"storage.nvme.warning.{key}:{n}",
                        ObservationSeverity.Warning,
                        ObservationCertainty.Fact,
                        "obs.storage.warning." + key,
                        ObservationText.Args(("drive", name)),
                        [new Evidence("storage.criticalWarning", "0x" + warning.ToString("X2", CultureInfo.InvariantCulture), string.Empty)],
                        []);
                }

                if ((warning & ~known) != 0)
                {
                    yield return new Observation(
                        $"storage.nvme.warning.other:{n}",
                        ObservationSeverity.Warning,
                        ObservationCertainty.Fact,
                        "obs.storage.warning.other",
                        ObservationText.Args(("drive", name), ("value", "0x" + warning.ToString("X2", CultureInfo.InvariantCulture))),
                        [new Evidence("storage.criticalWarning", "0x" + warning.ToString("X2", CultureInfo.InvariantCulture), string.Empty)],
                        []);
                }
            }

            if (health.AvailableSparePercent.TryGet(out var spare))
            {
                var hasThreshold = health.AvailableSpareThresholdPercent.TryGet(out var threshold);
                var low = hasThreshold ? spare <= threshold : spare < ObservationThresholds.NvmeSpareNoThresholdPercent;
                if (low)
                {
                    var evidence = new List<Evidence> { new("storage.availableSparePercent", ObservationText.N(spare), "%") };
                    var args = new Dictionary<string, string> { ["drive"] = name, ["spare"] = ObservationText.N(spare, "0") };
                    if (hasThreshold)
                    {
                        evidence.Add(new Evidence("storage.spareThresholdPercent", ObservationText.N(threshold), "%"));
                        args["threshold"] = ObservationText.N(threshold, "0");
                    }

                    yield return new Observation(
                        $"storage.nvme.spareLow:{n}",
                        hasThreshold ? ObservationSeverity.Warning : ObservationSeverity.Notice,
                        ObservationCertainty.Fact,
                        hasThreshold ? "obs.storage.spareLow" : "obs.storage.spareLowNoThreshold",
                        args,
                        evidence,
                        []);
                }
            }

            if (health.PercentageUsed.TryGet(out var used) && used >= ObservationThresholds.NvmeLifeUsedNoticePercent)
            {
                yield return new Observation(
                    $"storage.nvme.lifeUsed:{n}",
                    used >= ObservationThresholds.NvmeLifeUsedWarningPercent ? ObservationSeverity.Warning : ObservationSeverity.Notice,
                    ObservationCertainty.Fact,
                    "obs.storage.lifeUsed",
                    ObservationText.Args(("drive", name), ("used", ObservationText.N(used, "0"))),
                    [new Evidence("storage.percentageUsed", ObservationText.N(used), "%")],
                    []);
            }
        }
    }
}
