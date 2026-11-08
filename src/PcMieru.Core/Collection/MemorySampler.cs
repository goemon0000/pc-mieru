using System.Runtime.InteropServices;

namespace PcMieru.Core.Collection;

/// <summary>
/// System memory from GlobalMemoryStatusEx (physical) and GetPerformanceInfo (commit, cache, pools; the counts are
/// in pages, so they are multiplied by PageSize here and nowhere else).
/// </summary>
internal static class MemorySampler
{
    private const string PhysicalSource = "GlobalMemoryStatusEx";
    private const string PerfSource = "GetPerformanceInfo";

    public static MemoryInfo Sample()
    {
        var status = new Win32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Win32.MemoryStatusEx>() };
        var ok = Win32.GlobalMemoryStatusEx(ref status);
        var perf = default(Win32.PerformanceInformation);
        var perfOk = Win32.GetPerformanceInfo(out perf, (uint)Marshal.SizeOf<Win32.PerformanceInformation>());
        return Build(ok ? status : null, perfOk ? perf : null);
    }

    internal static MemoryInfo Build(Win32.MemoryStatusEx? status, Win32.PerformanceInformation? perf)
    {
        var total = status is { } s ? Clamp(s.TotalPhys) : 0;
        var available = status is { } a ? Clamp(a.AvailPhys) : 0;
        if (perf is not { } p || p.PageSize == 0)
        {
            var failed = Value.Failed<long>(PerfSource, "GetPerformanceInfo failed");
            return new MemoryInfo(total, available, failed, failed, failed, failed, failed);
        }

        long Pages(nuint count) => Clamp((ulong)count * p.PageSize);
        return new MemoryInfo(
            total,
            available,
            Value.Ok(Pages(p.CommitTotal), PerfSource),
            Value.Ok(Pages(p.CommitLimit), PerfSource),
            Value.Ok(Pages(p.SystemCache), PerfSource),
            Value.Ok(Pages(p.KernelPaged), PerfSource),
            Value.Ok(Pages(p.KernelNonpaged), PerfSource));
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
