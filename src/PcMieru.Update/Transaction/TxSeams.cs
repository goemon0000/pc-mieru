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

/// <summary>The file system of a volume. A test answers for it.</summary>
public interface IVolumeFormat
{
    /// <summary>The file system name of the volume that holds <paramref name="folder"/> ("NTFS", "ReFS", "FAT32", "exFAT", ...), or null when it cannot be told (a network path, a path with no drive).</summary>
    string? FormatOf(string folder);
}

/// <summary>
/// The real one: the format of the volume the folder REALLY lives on, asked of an open handle to the folder (GetVolumeInformationByHandle). The drive letter of the path is not asked: a volume mounted in a folder, or a
/// folder reached through a junction to another disk, is on the file system of the disk it is on, and not on the one of the root of its path (review of step 4, S5 gap). A network share answers null.
/// </summary>
public sealed class SystemVolumeFormat : IVolumeFormat
{
    private readonly Func<string, VolumeFacts?> _read;

    private SystemVolumeFormat() => _read = TxNative.ReadVolume;

    /// <summary>A test answers for the volume (what the file system would say about the folder).</summary>
    internal SystemVolumeFormat(Func<string, VolumeFacts?> read) => _read = read;

    public static SystemVolumeFormat Instance { get; } = new();

    public string? FormatOf(string folder)
    {
        try
        {
            var facts = _read(Path.GetFullPath(folder));
            return facts is null || facts.Value.IsNetwork || string.IsNullOrEmpty(facts.Value.Format) ? null : facts.Value.Format;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The swap runs only on NTFS or ReFS: the order in which the file system logs its metadata changes (a rename after the data was flushed) is what the journal and the "the new name never points to a
    /// half-written file" rule stand on. FAT, exFAT, a network share or an unknown answer do not promise it. A recovery does not ask: it must run on any file system, because the files are already there.
    /// </summary>
    public static bool IsSupported(string? format) =>
        format is not null && (format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || format.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What the launcher is asked to do. The verify run is part of the SWAP, not a start after it: the swapper still holds the start gate EXCLUSIVELY while the new version is verified (DESIGN-B 5, "the conditions of
/// the run", first point). The launcher therefore gives the new program that exclusive handle by limited inheritance (only that handle is passed down), together with an anonymous pipe for the answer and
/// <see cref="Tx"/> as the transaction id; the program enters verify mode only with them, and nothing else (a normal start) gets past the gate while the swap is open.
/// </summary>
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
/// <para>
/// The caller still HOLDS the exclusive start gate during this call, and it stays held for the whole verification. The real launcher gives the child that very handle (limited handle inheritance: the one
/// handle, an anonymous pipe, the transaction id), so the verify GUI is the only program that is inside the gate; a start by a person meanwhile is held back by the gate, not let in. The gate is released only
/// after the commit (or the rollback) is done.
/// </para>
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

    /// <summary>The file system of the install's volume. A swap is prepared only on NTFS or ReFS (<see cref="SystemVolumeFormat.IsSupported"/>); a recovery does not ask.</summary>
    public IVolumeFormat Volume { get; init; } = SystemVolumeFormat.Instance;

    /// <summary>Null: no launch is possible (a local recovery). A swap needs one.</summary>
    public ILaunchVerifier? Launcher { get; init; }

    /// <summary>
    /// True (and it is true in every production seam): data is flushed to the disk before the rename that names it and renames are written through. A TEST may set it false: the fault matrix and the kill tests stop the
    /// transaction at named steps (an exception, a killed process), which loses nothing the operating system already holds, so the flush adds no proof and, on a disk with a slow flush, takes minutes (a CI runner hit its
    /// limit). A source scan test fails when anything under src/ sets it to false.
    /// </summary>
    public bool Durable { get; init; } = true;

    internal IRunnerPlacement Placement { get; init; } = RealRunnerPlacement.Instance;

    /// <summary>The clock for last-result.json and blocked.json.</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>The real seams, with the given launcher (null for a recovery that must not launch).</summary>
    public static TxSeams Production(ILaunchVerifier? launcher = null) => new() { Launcher = launcher };

    /// <summary>A copy with the fault injector and/or the launcher replaced (the test build of the swapper uses it; the runner placement stays the real one).</summary>
    public TxSeams With(IFaultInjector? fault = null, ILaunchVerifier? launcher = null) => new()
    {
        Fault = fault ?? Fault,
        Disk = Disk,
        Launcher = launcher ?? Launcher,
        Durable = Durable,
        Placement = Placement,
        Volume = Volume,
        UtcNow = UtcNow,
    };
}
