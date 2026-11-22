using PcMieru.Update.Transaction;

namespace PcMieru.Update.Feed;

/// <summary>Whether an update could be put in place here, and if not, why (DESIGN-B 5). Only <see cref="CanSwap"/> means yes; the others mean "notify only".</summary>
public enum PlacementKind
{
    /// <summary>The folder can be written with this process's normal token, on a fixed drive, and the GUI is not elevated.</summary>
    CanSwap,

    /// <summary>The folder (or its update folder) cannot be written: read-only media, no rights, a locked-down share, Program Files.</summary>
    ReadOnly,

    /// <summary>The GUI runs elevated. An elevated process must never swap files that a normal user's process runs from (DESIGN-B 5).</summary>
    Elevated,

    /// <summary>The folder is on a removable drive, a network drive or a path that has no drive. Only a fixed drive can be swapped.</summary>
    NotFixedDrive,

    /// <summary>The drive is a fixed disk but its file system is not NTFS or ReFS (FAT32, exFAT, an unknown answer). The swap relies on the ordered metadata log of those two (see TxNative); a recovery does not need it.</summary>
    NotNtfs,
}

/// <summary>The answer of <see cref="InstallPlacement.Check"/>: the kind and a short technical text for a log.</summary>
public sealed record Placement(PlacementKind Kind, string Detail)
{
    public bool CanSwap => Kind == PlacementKind.CanSwap;
}

/// <summary>
/// The writability rule of DESIGN-B 5. The folder of the program (I) and its update folder (I\update, when there is one) are probed with the normal token: a file is
/// created, flushed to disk, renamed and deleted. Success is not remembered here: every call looks again (the GUI's controller remembers it for a page). An elevated GUI and a drive that is not a fixed disk mean "notify
/// only" whatever the probe says.
/// </summary>
public static class InstallPlacement
{
    /// <summary>The folder a swap would work in below the install folder.</summary>
    public const string UpdateFolderName = "update";

    public static Placement Check(string installFolder) => Check(installFolder, ProcessElevation.IsElevated, DriveTypeOf, SystemVolumeFormat.Instance.FormatOf);

    /// <summary>The same with the three questions given by the caller (a test's answers). <paramref name="formatOf"/> is the file system name of the folder's volume ("NTFS", "ReFS", ...).</summary>
    public static Placement Check(string installFolder, Func<bool> isElevated, Func<string, DriveType> driveTypeOf, Func<string, string?> formatOf)
    {
        if (isElevated())
        {
            return new Placement(PlacementKind.Elevated, "the GUI runs elevated");
        }

        if (driveTypeOf(installFolder) != DriveType.Fixed)
        {
            return new Placement(PlacementKind.NotFixedDrive, "the folder is not on a fixed drive");
        }

        // NTFS or ReFS only: the swap stands on the order of their metadata log (TxNative). A recovery does not ask this question.
        if (!SystemVolumeFormat.IsSupported(formatOf(installFolder)))
        {
            return new Placement(PlacementKind.NotNtfs, "the volume is not NTFS or ReFS");
        }

        // I\update is probed when it exists. When it does not, making it needs I to be writable, which the first probe shows.
        var update = Path.Combine(installFolder, UpdateFolderName);
        foreach (var folder in Directory.Exists(update) ? new[] { installFolder, update } : [installFolder])
        {
            if (Probe(folder) is { } failure)
            {
                return new Placement(PlacementKind.ReadOnly, $"{folder}: {failure}");
            }
        }

        return new Placement(PlacementKind.CanSwap, "writable");
    }

    /// <summary>A probe file that is older than this is a left-over of a crashed or killed probe, and is swept.</summary>
    internal static readonly TimeSpan StaleProbeAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes the <c>.pcm-probe-*</c> files an earlier probe left (a process killed between creating and deleting its file) that are older than <see cref="StaleProbeAge"/>.
    /// A younger one may belong to a probe running right now in another window. Best effort: a file that cannot be deleted stays.
    /// </summary>
    internal static void SweepStale(string folder, DateTimeOffset now)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, ".pcm-probe-*"))
            {
                try
                {
                    if (now - File.GetLastWriteTimeUtc(path) > StaleProbeAge)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    /// <summary>Creates a file in the folder, flushes it, renames it and deletes it. Null on success, otherwise what went wrong. Sweeps the stale files of earlier probes first.</summary>
    internal static string? Probe(string folder)
    {
        SweepStale(folder, DateTimeOffset.UtcNow);
        var first = Path.Combine(folder, ".pcm-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        var second = first + ".moved";
        try
        {
            using (var stream = new FileStream(first, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x50);
                stream.Flush(flushToDisk: true);
            }

            File.Move(first, second);
            File.Delete(second);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            foreach (var left in new[] { first, second })
            {
                try
                {
                    File.Delete(left);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }

            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static DriveType DriveTypeOf(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? DriveType.Unknown : new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DriveType.Unknown;
        }
    }
}
