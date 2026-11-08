namespace PcMieru.Core.Reporting;

/// <summary>
/// The real IObservationEngine: runs every v1 rule and returns the findings, worst first. A pure function of its input
/// (the GPU limits are fixed at construction). The rules themselves live in <see cref="ObservationRules"/>.
/// </summary>
public sealed class ObservationEngine : IObservationEngine
{
    private readonly IGpuTemperatureLimits _gpuLimits;

    public ObservationEngine(IGpuTemperatureLimits? gpuLimits = null)
    {
        _gpuLimits = gpuLimits ?? new NoGpuTemperatureLimits();
    }

    public IReadOnlyList<Observation> Evaluate(ObservationInput input)
    {
        var found = new List<Observation>();
        found.AddRange(ObservationRules.SingleCoreSaturated(input));
        found.AddRange(ObservationRules.CommitHigh(input));
        found.AddRange(ObservationRules.AvailableMemoryLow(input));
        found.AddRange(ObservationRules.ProcessMemoryDominant(input));
        found.AddRange(ObservationRules.DiskBusy(input));
        found.AddRange(ObservationRules.GpuTemperatureHigh(ObservationRules.ThermalReadings(input.Current, _gpuLimits)));
        found.AddRange(ObservationRules.StorageHealth(input.StorageHealth));

        return found
            .OrderByDescending(o => o.Severity)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
