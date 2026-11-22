using System.Globalization;
using PcMieru.Core;

namespace PcMieru.Checks;

/// <summary>One column of the vocabulary: its name after the source, its unit and how its cells are read.</summary>
internal readonly record struct RunMetricDef(string Name, string Unit, RunMetricKind Kind);

/// <summary>
/// The columns of a run and how each is taken from a <see cref="RecordingSample"/> (DESIGN-B §7.1): everything the sample carries. The disks and the
/// per-logical-processor load are NOT in v0.5; they can be added later as new metrics (a new key, no change to the format).
/// </summary>
internal static class RunMetrics
{
    public const string Core = "core";

    // core.* (the sampler pass). The memory totals are plain numbers in bytes.
    public static readonly RunMetricDef CpuUsage = new("cpu_usage_pct", "%", RunMetricKind.Number);
    public static readonly RunMetricDef CpuMhz = new("cpu_mhz", "MHz", RunMetricKind.Number);
    public static readonly RunMetricDef CpuPackageW = new("cpu_package_w", "W", RunMetricKind.Number);
    public static readonly RunMetricDef MemTotal = new("mem_total_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef MemAvailable = new("mem_available_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef MemCommit = new("mem_commit_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef MemCommitLimit = new("mem_commit_limit_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef MemCache = new("mem_cache_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef MemPagedPool = new("mem_paged_pool_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef MemNonPagedPool = new("mem_nonpaged_pool_bytes", "B", RunMetricKind.Number);
    public static readonly RunMetricDef AcConnected = new("ac_connected", "", RunMetricKind.Flag);
    public static readonly RunMetricDef BatteryPct = new("battery_pct", "%", RunMetricKind.Number);
    public static readonly RunMetricDef BatteryState = new("battery_state", "", RunMetricKind.Text);

    /// <summary>The core columns that do not belong to a GPU, in column order.</summary>
    public static readonly RunMetricDef[] CoreOnly =
    [
        CpuUsage, CpuMhz, CpuPackageW, MemTotal, MemAvailable, MemCommit, MemCommitLimit, MemCache, MemPagedPool, MemNonPagedPool,
        AcConnected, BatteryPct, BatteryState,
    ];

    // A GPU's PDH values travel with the core pass (their frame is the core frame), keyed core.gpu<N>_*.
    public static readonly RunMetricDef GpuUtil = new("util_pct", "%", RunMetricKind.Number);
    public static readonly RunMetricDef GpuVram = new("vram_used_bytes", "B", RunMetricKind.Number);

    // gpu<N>.* (the SensorHost frame).
    public static readonly RunMetricDef GpuTemp = new("temp_c", "°C", RunMetricKind.Number);
    public static readonly RunMetricDef GpuPower = new("power_w", "W", RunMetricKind.Number);
    public static readonly RunMetricDef GpuFan = new("fan_pct", "%", RunMetricKind.Number);
    public static readonly RunMetricDef GpuClock = new("clock_mhz", "MHz", RunMetricKind.Number);
    public static readonly RunMetricDef GpuTempLimit = new("temp_limit_c", "°C", RunMetricKind.Number);
    public static readonly RunMetricDef GpuLimitReasons = new("limit_reasons", "", RunMetricKind.Bitmask);

    public static readonly RunMetricDef[] GpuVendor = [GpuTemp, GpuPower, GpuFan, GpuClock, GpuTempLimit, GpuLimitReasons];

    public static string GpuSource(int number) => "gpu" + number.ToString(CultureInfo.InvariantCulture);

    public static string CoreKey(RunMetricDef def) => Core + "." + def.Name;

    public static string CoreGpuKey(int number, RunMetricDef def) => Core + "." + GpuSource(number) + "_" + def.Name;

    public static string GpuKey(int number, RunMetricDef def) => GpuSource(number) + "." + def.Name;

    /// <summary>The unit and kind of a column key, from the vocabulary. Null for a key this version does not know (the JSON's own entry is then the only description).</summary>
    public static RunMetricDef? Describe(string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == key.Length - 1)
        {
            return null;
        }

        var source = key[..dot];
        var tail = key[(dot + 1)..];
        if (source == Core)
        {
            if (TryGpuPrefix(tail, out var rest))
            {
                tail = rest;
                return Find([GpuUtil, GpuVram], tail);
            }

            return Find(CoreOnly, tail);
        }

        return IsGpuSource(source) ? Find(GpuVendor, tail) : null;
    }

    public static bool IsGpuSource(string source) =>
        source.Length > 3 && source.StartsWith("gpu", StringComparison.Ordinal) && source.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>"gpu3_util_pct" gives (true, "util_pct").</summary>
    public static bool TryGpuPrefix(string tail, out string rest)
    {
        rest = tail;
        if (!tail.StartsWith("gpu", StringComparison.Ordinal))
        {
            return false;
        }

        var under = tail.IndexOf('_', StringComparison.Ordinal);
        if (under <= 3 || tail.AsSpan(3, under - 3).IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return false;
        }

        rest = tail[(under + 1)..];
        return true;
    }

    private static RunMetricDef? Find(RunMetricDef[] defs, string name)
    {
        foreach (var def in defs)
        {
            if (def.Name == name)
            {
                return def;
            }
        }

        return null;
    }

    /// <summary>
    /// The device a metric belongs to for "under load": <c>cpu</c> for the CPU's own columns, <c>gpu&lt;N&gt;</c> for a GPU's (its PDH columns in core
    /// and its vendor columns), null for everything else (memory, power source). The old format's <c>cpu.*</c> and <c>gpuN.*</c> keys map the same way.
    /// </summary>
    public static string? DeviceOf(string key)
    {
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }

        var source = key[..dot];
        var tail = key[(dot + 1)..];
        if (source == Core)
        {
            if (TryGpuPrefix(tail, out _))
            {
                return tail[..tail.IndexOf('_', StringComparison.Ordinal)];
            }

            return tail.StartsWith("cpu_", StringComparison.Ordinal) ? "cpu" : null;
        }

        if (source == "cpu" || IsGpuSource(source))
        {
            return source;
        }

        return null;
    }

    /// <summary>The key of the load column of a device: the CPU's usage or the GPU's PDH utilization.</summary>
    public static string LoadKeyOf(string device) =>
        device == "cpu" ? CoreKey(CpuUsage) : Core + "." + device + "_" + GpuUtil.Name;
}
