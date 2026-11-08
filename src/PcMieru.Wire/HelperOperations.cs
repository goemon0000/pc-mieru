namespace PcMieru.Wire;

/// <summary>The fixed request table of the Helper (SPEC section 8). Anything not listed is refused.</summary>
public enum HelperOperation
{
    ServiceStart,
    ServiceStop,
    ServiceRestart,
    StartupHklmSet,
    ProcessTerminate,
    ProcessPriority,
    ProcessEfficiency,
    SmartRead,
    NvmeHealth,
}

public static class HelperOperations
{
    public sealed record Spec(HelperOperation Op, string Name, IReadOnlyList<string> Options);

    /// <summary>Option names are listed without the leading "--". --result and --nonce are always required.</summary>
    public static IReadOnlyList<Spec> All { get; } =
    [
        new(HelperOperation.ServiceStart, "service-start", ["name"]),
        new(HelperOperation.ServiceStop, "service-stop", ["name"]),
        new(HelperOperation.ServiceRestart, "service-restart", ["name"]),
        new(HelperOperation.StartupHklmSet, "startup-hklm-set", ["provider", "name", "state"]),
        new(HelperOperation.ProcessTerminate, "process-terminate", ["pid", "created"]),
        new(HelperOperation.ProcessPriority, "process-priority", ["pid", "created", "priority"]),
        new(HelperOperation.ProcessEfficiency, "process-efficiency", ["pid", "created", "state"]),
        new(HelperOperation.SmartRead, "smart-read", ["drive"]),
        new(HelperOperation.NvmeHealth, "nvme-health", ["drive"]),
    ];

    public static Spec Get(HelperOperation op) => All.First(s => s.Op == op);

    public static bool TryGet(string name, out Spec spec)
    {
        foreach (var s in All)
        {
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
            {
                spec = s;
                return true;
            }
        }

        spec = null!;
        return false;
    }
}
