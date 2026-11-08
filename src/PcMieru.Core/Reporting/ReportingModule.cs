using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

public static class ReportingModule
{
    /// <summary>Registers the real engine, catalog and redactor, then composes the report and insights on top of the other areas.</summary>
    public static void Register(ServiceRegistry registry, PcMieruOptions options)
    {
        Strings.Register(ReportingStrings.All);

        // The GPU temperature limit is not in the shared contract yet (docs/contract-requests/E.md). Until a hardware
        // area registers an IGpuTemperatureLimits, the rule has no limit to compare with and stays silent.
        registry.TryGet<IGpuTemperatureLimits>(out var gpuLimits);
        var engine = new ObservationEngine(gpuLimits);
        var redactor = new SimpleRedactor(RedactionContext.FromEnvironment(), options.Redact);
        registry.Set<IObservationEngine>(engine);
        registry.Set<IKnownProcessCatalog>(new KnownProcessCatalog());
        registry.Set<IRedactor>(redactor);

        var snapshots = registry.Get<ISnapshotSource>();
        var hardware = registry.Get<IHardwareProvider>();
        var storage = registry.Get<IStorageHealthProvider>();
        var reports = new ReportBuilder(snapshots, engine, hardware, storage, redactor, options.Clock);
        registry.Set<IReportBuilder>(reports);
        registry.Set<IReadOnlyInsights>(new Insights(
            snapshots,
            registry.Get<IProcessDetailsProvider>(),
            hardware,
            storage,
            engine,
            reports,
            options.DefaultSampleWindow));
    }
}
