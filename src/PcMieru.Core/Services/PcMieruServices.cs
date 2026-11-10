using PcMieru.Core.Hardware;
using PcMieru.Core.Management;
using PcMieru.Core.Reporting;
using PcMieru.Core.Collection;

namespace PcMieru.Core;

/// <summary>Start-up switches shared by GUI, CLI and MCP.</summary>
public sealed record PcMieruOptions
{
    /// <summary>Output redaction. Only `pcmi mcp --no-redact` / `pcmi report --no-redact` turn it off.</summary>
    public bool Redact { get; init; } = true;

    /// <summary>Use Fakes even where a real implementation exists (tests, demos).</summary>
    public bool ForceFakes { get; init; }

    /// <summary>Clock for Fakes and for anything time-stamped in tests. Real code uses it too instead of DateTime.UtcNow.</summary>
    public Func<DateTime> Clock { get; init; } = static () => DateTime.UtcNow;

    public int FakeSeed { get; init; } = 1;

    /// <summary>Observation window for one-shot reads (CLI, MCP) that need CPU numbers.</summary>
    public TimeSpan DefaultSampleWindow { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Small service locator: type to instance. Each area fills its own slots from its Module.Register.</summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<Type, object> _items = [];

    public void Set<T>(T instance)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _items[typeof(T)] = instance;
    }

    public T Get<T>()
        where T : class =>
        _items.TryGetValue(typeof(T), out var item)
            ? (T)item
            : throw new InvalidOperationException($"{typeof(T).Name} is not registered (check the Module.Register order).");

    public bool TryGet<T>(out T? instance)
        where T : class
    {
        var found = _items.TryGetValue(typeof(T), out var item);
        instance = found ? (T)item! : null;
        return found;
    }

    public IEnumerable<object> All => _items.Values.Distinct();
}

/// <summary>
/// Everything the front ends need, built in one place. Creation order is fixed: Collection, Hardware, Management,
/// Reporting (last, because it composes the others). Every area still on a Fake is listed in <see cref="FakeAreas"/>;
/// the GUI shows it in the title bar and the CLI says it on stderr, so sample data is never mistaken for a measurement.
/// </summary>
public sealed class PcMieruServices : IDisposable
{
    private readonly ServiceRegistry _registry;

    private PcMieruServices(ServiceRegistry registry, PcMieruOptions options)
    {
        _registry = registry;
        Options = options;
    }

    public PcMieruOptions Options { get; }

    public ISnapshotSource Snapshots => _registry.Get<ISnapshotSource>();

    /// <summary>
    /// The one loop that samples <see cref="Snapshots"/> on a beat. Nothing starts it here: the GUI does. While it runs, reports
    /// read its history instead of sampling a second time; a front end that never starts it (pcmi, MCP) gets one-off samples as before.
    /// </summary>
    public SamplerLoop Sampler => _registry.Get<SamplerLoop>();

    public IProcessDetailsProvider ProcessDetails => _registry.Get<IProcessDetailsProvider>();

    public IProcessGroupSource ProcessGroups => _registry.Get<IProcessGroupSource>();

    public IProcessIconSource ProcessIcons => _registry.Get<IProcessIconSource>();

    /// <summary>The opt-in per-process network meter (one UAC prompt to start). See SPEC 5.3a.</summary>
    public INetMeterControl NetMeter => _registry.Get<INetMeterControl>();

    public IHardwareProvider Hardware => _registry.Get<IHardwareProvider>();

    public IStorageHealthProvider StorageHealth => _registry.Get<IStorageHealthProvider>();

    public ISensorClient Sensors => _registry.Get<ISensorClient>();

    public IProcessControl ProcessControl => _registry.Get<IProcessControl>();

    public IProtectionPolicy Protection => _registry.Get<IProtectionPolicy>();

    public IServiceManager Services => _registry.Get<IServiceManager>();

    public IStartupManager Startup => _registry.Get<IStartupManager>();

    public ISessionProvider Sessions => _registry.Get<ISessionProvider>();

    public IHelperClient Helper => _registry.Get<IHelperClient>();

    public ITaskLauncher Tasks => _registry.Get<ITaskLauncher>();

    public IObservationEngine Observations => _registry.Get<IObservationEngine>();

    public IKnownProcessCatalog KnownProcesses => _registry.Get<IKnownProcessCatalog>();

    public IRedactor Redactor => _registry.Get<IRedactor>();

    public IReportBuilder Reports => _registry.Get<IReportBuilder>();

    public IReadOnlyInsights Insights => _registry.Get<IReadOnlyInsights>();

    /// <summary>Types (interface names) still served by a Fake.</summary>
    public IReadOnlyList<string> FakeAreas =>
        _registry.All.OfType<IFakeImplementation>().Select(f => f.GetType().Name).Distinct().Order().ToArray();

    public bool UsesFakeData => FakeAreas.Count > 0;

    public T Get<T>()
        where T : class => _registry.Get<T>();

    /// <param name="configure">Replaces registrations after the modules ran (tests).</param>
    public static PcMieruServices Create(PcMieruOptions? options = null, Action<ServiceRegistry>? configure = null)
    {
        options ??= new PcMieruOptions();
        var registry = new ServiceRegistry();
        CollectionModule.Register(registry, options);
        HardwareModule.Register(registry, options);
        ManagementModule.Register(registry, options);
        ReportingModule.Register(registry, options);
        configure?.Invoke(registry);
        return new PcMieruServices(registry, options);
    }

    public void Dispose()
    {
        // The loop reads the source, so it stops first.
        if (_registry.TryGet<SamplerLoop>(out var loop))
        {
            loop!.Dispose();
        }

        foreach (var item in _registry.All.OfType<IDisposable>())
        {
            item.Dispose();
        }
    }
}
