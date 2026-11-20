using System.Reflection;
using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware;

namespace PcMieru.Checks;

/// <summary>
/// What a check needs from the outside (DESIGN-A §2): the clock, the folder results and baselines live under, the source of the drive
/// inspection, the v0.7.4 folder the baseline may read once, the history store, and the name of the app that writes the results.
/// Every path is given by the caller: nothing here looks at the user's profile. The Windows pieces are plugged in by whoever assembles the
/// services (<see cref="CreateLive"/> for the real machine).
/// <para>
/// The hardware provider and the CPU test-session factory of §2 are not here yet: they arrive with the system, CPU and memory checks
/// (DESIGN-A §11 steps 5 to 7) as further constructor parameters and properties of this class.
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
    public CheckServices(
        TimeProvider time,
        string root,
        IStorageInspectionSource storage,
        string? legacyDirectory,
        ICheckHistoryMaintainer history,
        string? app = null)
        : this(time, storage, history, NewBaseline(time, root, legacyDirectory), app, runIds: null)
    {
    }

    internal CheckServices(
        TimeProvider time,
        IStorageInspectionSource storage,
        ICheckHistoryMaintainer history,
        BaselineEngine baseline,
        string? app,
        Func<Guid>? runIds)
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
    }

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
    /// The services of the real machine: the real storage inspection (<see cref="RealStorageInspectionSource"/>), the history under
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
            root,
            new RealStorageInspectionSource(elevated),
            legacyDirectory,
            new CheckHistoryStore(ChecksFolder(root)),
            app);
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
