using System.Reflection;
using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Checks.SystemInfo;
using PcMieru.Core.Hardware;

namespace PcMieru.Checks;

/// <summary>
/// What a check needs from the outside (DESIGN-A §2): the clock, the folder results and baselines live under, the source of the drive
/// inspection, the v0.7.4 folder the baseline may read once, the history store, and the name of the app that writes the results.
/// Every path is given by the caller: nothing here looks at the user's profile. The Windows pieces are plugged in by whoever assembles the
/// services (<see cref="CreateLive"/> for the real machine).
/// <para>
/// The hardware provider arrived with the system check (<see cref="HardwareFactory"/>), as did the two readers it asks Windows through
/// (<see cref="Wmi"/> and <see cref="Events"/>, which the storage check's disk health uses too). The CPU test-session factory of §2 arrives
/// with the CPU and memory checks (DESIGN-A §11 steps 6 and 7).
/// </para>
/// </summary>
public sealed class CheckServices
{
    /// <summary>The check's own app string, "PCMieru x.y.z" from the Checks assembly's informational version.</summary>
    public static string DefaultApp { get; } = AppStringOf(typeof(CheckServices).Assembly);

    /// <param name="time">The clock. Start and end times of a run come from it.</param>
    /// <param name="root">
    /// The product's data folder (the app passes <c>%LOCALAPPDATA%\PCMieru</c>). The baseline is kept under <c>&lt;root&gt;\checks\baseline</c>
    /// (DESIGN-A §4), inside the folder the history store of <see cref="CreateLive"/> uses (<c>&lt;root&gt;\checks</c>): the history scan
    /// looks at files only, so the <c>baseline</c> folder is not a result.
    /// </param>
    /// <param name="storage">The source of the storage check's input.</param>
    /// <param name="legacyDirectory">The checks folder the v0.7.4 app kept, only read, or null for no import.</param>
    /// <param name="history">Where a finished run is saved.</param>
    /// <param name="app">The <c>app</c> field of the results ("PCMieru 0.4.0"). Defaults to <see cref="DefaultApp"/>.</param>
    /// <param name="hardware">
    /// The source of the system check's hardware facts (OS, CPU, memory, GPUs, disks). Null: every system check gets a new
    /// <see cref="RealHardwareProvider"/> of its own (made on a worker when the check asks, nothing cached from an earlier run). A provider
    /// that is given is shared by every system check of these services and is used as it is: it is never asked to invalidate its cache, because
    /// its gates may be held by a call that an earlier run abandoned and the check must not wait on them. It is the owner's to keep fresh.
    /// </param>
    public CheckServices(
        TimeProvider time,
        string root,
        IStorageInspectionSource storage,
        string? legacyDirectory,
        ICheckHistoryMaintainer history,
        string? app = null,
        IHardwareProvider? hardware = null)
        : this(time, storage, history, NewBaseline(time, root, legacyDirectory), app, runIds: null, hardware is null ? null : () => hardware)
    {
    }

    /// <param name="hardwareFactory">
    /// Makes the hardware provider of one system check (null: <c>() =&gt; new RealHardwareProvider()</c>). It is called on a worker, never on
    /// the caller's thread, and only when a call is to be made.
    /// </param>
    /// <param name="wmi">The WMI reader of the system check and of the storage check's disk health (null: the real one, <see cref="ManagementWmiReader"/>).</param>
    /// <param name="events">The System-log counter of the system check (null: the real one, <see cref="WevtEventCounter"/>).</param>
    internal CheckServices(
        TimeProvider time,
        IStorageInspectionSource storage,
        ICheckHistoryMaintainer history,
        BaselineEngine baseline,
        string? app,
        Func<Guid>? runIds,
        Func<IHardwareProvider>? hardwareFactory = null,
        IWmiReader? wmi = null,
        IEventCounter? events = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(baseline);
        Time = time;
        Storage = storage;
        History = history;
        Baseline = baseline;
        App = string.IsNullOrWhiteSpace(app) ? DefaultApp : app;
        RunIds = runIds ?? Guid.NewGuid;
        HardwareFactory = hardwareFactory ?? NewRealHardware;
        Wmi = wmi ?? new ManagementWmiReader();
        Events = events ?? new WevtEventCounter();
    }

    // A provider of the real machine for one system check: it reads nothing until the check asks.
    private static IHardwareProvider NewRealHardware() => new RealHardwareProvider();

    /// <summary>The folder of the saved results and of the baseline (<c>&lt;root&gt;\checks</c>, DESIGN-A §4).</summary>
    internal static string ChecksFolder(string root) => Path.Combine(root, "checks");

    // The engine puts its files in "<its root>\baseline", and so does its lock: the engine's root is the checks folder, never the data folder.
    private static BaselineEngine NewBaseline(TimeProvider time, string root, string? legacyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var checks = ChecksFolder(root);
        return new BaselineEngine(checks, legacyDirectory, PhysicalBaselineFileSystem.Instance, PhysicalHistoryFileReader.Instance, new FileBaselineLock(Path.Combine(checks, "baseline")), time, null);
    }

    public TimeProvider Time { get; }

    public IStorageInspectionSource Storage { get; }

    public ICheckHistoryMaintainer History { get; }

    public string App { get; }

    internal BaselineEngine Baseline { get; }

    /// <summary>Makes the ID of a new run. A test fixes it.</summary>
    internal Func<Guid> RunIds { get; }

    /// <summary>
    /// Makes the provider of the system check's hardware facts, one per run (or the one shared provider of the public constructor, which it
    /// gives back every time). A run never calls <see cref="IHardwareProvider.Invalidate"/> on what it gets: a fresh provider has nothing
    /// cached, and Invalidate takes the gates an abandoned call may hold, so it must not run where a caller waits (DESIGN-A 6.1).
    /// </summary>
    internal Func<IHardwareProvider> HardwareFactory { get; }

    /// <summary>The hardware calls of the system check that are still running, kept across the runs of these services (§6.1: they do not pile up).</summary>
    internal HardwareCalls HardwareCalls { get; } = new();

    /// <summary>WMI, for the system check's page files and GPU status and for the disk health of the system and storage checks. Each question has its own 5 s deadline (§6.1).</summary>
    internal IWmiReader Wmi { get; }

    /// <summary>The System-log counts of the system check. The eleven queries of a run share one 5 s deadline (§6.1).</summary>
    internal IEventCounter Events { get; }

    /// <summary>
    /// The services of the real machine: the real storage inspection (<see cref="RealStorageInspectionSource"/>), the real hardware provider,
    /// WMI and System-log readers (system check; a new hardware provider for every system check), the history under
    /// <c>&lt;root&gt;\checks</c> and the baseline under <c>&lt;root&gt;\checks\baseline</c> (DESIGN-A §4). <paramref name="root"/> is the
    /// product's data folder (the app passes <c>%LOCALAPPDATA%\PCMieru</c>) and <paramref name="legacyDirectory"/> the v0.7.4 folder or null: both are the caller's
    /// decision, nothing here looks at the user's profile, and nothing is read or created until a run starts. No elevated reader is wired
    /// unless <paramref name="elevated"/> is given: without one, a request with <see cref="CheckRequest.AllowElevation"/> keeps the
    /// denied drives denied.
    /// </summary>
    public static CheckServices CreateLive(string root, string? legacyDirectory, string? app = null, IElevatedStorageReader? elevated = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new CheckServices(
            TimeProvider.System,
            new RealStorageInspectionSource(elevated),
            new CheckHistoryStore(ChecksFolder(root)),
            NewBaseline(TimeProvider.System, root, legacyDirectory),
            app,
            runIds: null,
            hardwareFactory: NewRealHardware); // a provider of its own for every system check: nothing cached, no gate held by an abandoned call
    }

    /// <summary>"PCMieru " and the informational version of the assembly without the build metadata ("+sha").</summary>
    private static string AppStringOf(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString(3) ?? "0";
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return "PCMieru " + (plus >= 0 ? version[..plus] : version);
    }
}
