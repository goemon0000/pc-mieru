namespace PcMieru.Update.Transaction;

/// <summary>The numbers of DESIGN-B 5, in one place.</summary>
public static class TxLimits
{
    /// <summary>The most launch attempts of the new version (each is counted in the journal BEFORE the launch).</summary>
    public const int MaxLaunchAttempts = 3;

    /// <summary>The most AUTOMATIC recovery attempts (counted in the journal). recover.exe, run by a person, always tries.</summary>
    public const int MaxAutomaticRecoveryAttempts = 3;

    /// <summary>The UI thread of the new version must answer and stay alive for this long (a monotonic clock) after it drew its window.</summary>
    public static readonly TimeSpan LaunchUiCheck = TimeSpan.FromSeconds(15);

    /// <summary>The whole of one attempt.</summary>
    public static readonly TimeSpan LaunchTotal = TimeSpan.FromSeconds(120);

    /// <summary>What is kept free beyond the bytes the transaction itself writes (journal, records, the file system's own use).</summary>
    public const long SpaceMarginBytes = 4L * 1024 * 1024;
}

/// <summary>The free space of a volume. A test makes it short.</summary>
public interface IDiskSpace
{
    /// <summary>Bytes free on the volume that holds <paramref name="folder"/>.</summary>
    long FreeBytes(string folder);
}

/// <summary>The real one (GetDiskFreeSpaceEx).</summary>
public sealed class SystemDiskSpace : IDiskSpace
{
    public static SystemDiskSpace Instance { get; } = new();

    public long FreeBytes(string folder) => TxNative.FreeBytes(folder);
}

/// <summary>What the launcher is asked to do.</summary>
public sealed record LaunchRequest(string InstallFolder, string Tx, int Attempt, int MaxAttempts, string Version, string BuildId, TimeSpan UiCheck, TimeSpan Total);

/// <summary>The answer of a launch.</summary>
public enum LaunchVerdict
{
    /// <summary>The new version started, drew its window and its UI thread answered for the whole check.</summary>
    Ok,

    /// <summary>It did not (did not start, died, hung, or the time ran out).</summary>
    Failed,
}

/// <summary>
/// "Start the new version and see that it lives" (DESIGN-B 5, VerifyPending). Step 4 only defines the seam; the tests use a fake, and step 5 wires the real one (the verify-only GUI start, 15 s of UI
/// check, 120 s per attempt). An exception from it counts as <see cref="LaunchVerdict.Failed"/>. The transaction calls it with the journal already saying that this attempt was made.
/// </summary>
public interface ILaunchVerifier
{
    LaunchVerdict LaunchAndVerify(LaunchRequest request);
}

/// <summary>Why the recovery runner could not be placed.</summary>
public sealed class RunnerUnavailableException(string message) : IOException(message);

/// <summary>What the runner placement is given.</summary>
internal sealed record PlacementContext(TxPaths Paths, InstalledRecord Current, InstalledRecord? Next);

/// <summary>
/// Makes sure the recovery runner is in place before the first change (DESIGN-B 13 item 3): <c>I\update\recover.exe</c> is a verified copy of the current <c>I\PCMieru.Swap.exe</c> (its hash is the one of the
/// installed record), and <c>I\update\RECOVER.txt</c> says what to do if everything else fails. Throws <see cref="RunnerUnavailableException"/> (or an I/O exception) when it cannot; the transaction then stops before
/// the journal is committed. A test replaces it to make the placement fail.
/// </summary>
internal interface IRunnerPlacement
{
    void Place(TxIo io, PlacementContext context);
}

/// <summary>Everything the transaction takes from outside. The defaults are the real ones.</summary>
public sealed class TxSeams
{
    /// <summary>Stops the process at named steps (tests only).</summary>
    public IFaultInjector Fault { get; init; } = NoFaults.Instance;

    public IDiskSpace Disk { get; init; } = SystemDiskSpace.Instance;

    /// <summary>Null: no launch is possible (a local recovery). A swap needs one.</summary>
    public ILaunchVerifier? Launcher { get; init; }

    internal IRunnerPlacement Placement { get; init; } = RealRunnerPlacement.Instance;

    /// <summary>The clock for last-result.json and blocked.json.</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>The real seams, with the given launcher (null for a recovery that must not launch).</summary>
    public static TxSeams Production(ILaunchVerifier? launcher = null) => new() { Launcher = launcher };
}
