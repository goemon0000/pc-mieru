namespace PcMieru.Gui;

/// <summary>One line of the Overview's "heavy apps" list: every process of one product added together.</summary>
internal sealed record TopApp(string Name, double Cpu, long MemoryBytes, int ProcessCount, ProcessKey Heaviest);

/// <summary>
/// The five heaviest apps right now, by product name. Processes are first grouped by image name (cheap), the heaviest
/// groups get their product name looked up, and groups of the same product are merged (the many processes of one browser
/// become one line). "Heavy" is the share of the PC used: CPU share plus memory share, so an idle PC still ranks by memory.
/// </summary>
internal static class TopApps
{
    /// <summary>How many image-name groups get a product lookup. More than the count shown, so merging still fills the list.</summary>
    private const int Candidates = 10;

    /// <summary>
    /// Parts of the kernel that Task Manager lists as processes. They are not apps (nobody can close "Memory Compression"),
    /// and on a real PC they would otherwise take a place in the list by memory alone.
    /// </summary>
    private static readonly HashSet<string> KernelNames = new(StringComparer.OrdinalIgnoreCase) { "System", "Registry", "Memory Compression", "Secure System" };

    /// <summary>
    /// The product name that Windows' own files carry. As a merge key it would put every Windows component into one line
    /// ("Microsoft Windows Operating System (99)"), so it counts as "no product name" and the image name is shown instead.
    /// </summary>
    internal static bool IsGenericProduct(string? product) =>
        product is null || product.Contains("Windows", StringComparison.OrdinalIgnoreCase) && product.Contains("Operating System", StringComparison.OrdinalIgnoreCase);

    public static List<TopApp> Compute(IReadOnlyList<ProcessInfo> processes, long totalPhysicalBytes, Func<ProcessKey, string?> product, int count = 5)
    {
        var total = Math.Max(1, totalPhysicalBytes);
        var groups = processes
            .Where(p => p.Key.Pid != 0 && !KernelNames.Contains(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var heaviest = g.OrderByDescending(p => p.CpuPercent.GetOr(0)).ThenByDescending(p => p.PrivateWorkingSetBytes).First();
                return (Name: g.Key, Cpu: g.Sum(p => p.CpuPercent.GetOr(0)), Memory: g.Sum(p => p.PrivateWorkingSetBytes), Count: g.Count(), Heaviest: heaviest.Key);
            })
            .OrderByDescending(g => Score(g.Cpu, g.Memory, total))
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Candidates);

        var merged = new Dictionary<string, TopApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var name = product(g.Heaviest) is { Length: > 0 } p && !IsGenericProduct(p) ? p : DisplayName(g.Name);
            if (merged.TryGetValue(name, out var existing))
            {
                var better = Score(g.Cpu, g.Memory, total) > Score(existing.Cpu, existing.MemoryBytes, total) ? g.Heaviest : existing.Heaviest;
                merged[name] = existing with { Cpu = existing.Cpu + g.Cpu, MemoryBytes = existing.MemoryBytes + g.Memory, ProcessCount = existing.ProcessCount + g.Count, Heaviest = better };
            }
            else
            {
                merged[name] = new TopApp(name, g.Cpu, g.Memory, g.Count, g.Heaviest);
            }
        }

        return merged.Values
            .OrderByDescending(a => Score(a.Cpu, a.MemoryBytes, total))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }

    private static double Score(double cpuPercent, long memoryBytes, long totalBytes) => (cpuPercent / 100.0) + ((double)memoryBytes / totalBytes);

    /// <summary>"chrome.exe" becomes "chrome" when no product name is known.</summary>
    public static string DisplayName(string imageName) =>
        imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;
}
