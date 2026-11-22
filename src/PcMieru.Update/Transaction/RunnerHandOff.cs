using System.Diagnostics;
using PcMieru.Update.Bundle;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

/// <summary>What <see cref="RunnerHandOff"/> found or did.</summary>
public enum HandOffStatus
{
    /// <summary><c>I\update\recover.exe</c> is a file that may be run (it is the recorded Swap.exe).</summary>
    Ready,

    /// <summary>I\update is a link (a reparse point) or cannot be used.</summary>
    UpdateFolderUnsafe,

    /// <summary>There is no installed.json, no journal and no readable PCMieru.bundle.json to take the hash of the Swap.exe from.</summary>
    NoRecord,

    /// <summary>The record has no PCMieru.Swap.exe line, or the installed PCMieru.Swap.exe is not the recorded file (so it is not copied).</summary>
    SwapExeNotRecorded,

    /// <summary>recover.exe is missing, or is not a file that the installed record or the journal vouches for.</summary>
    RunnerNotTrusted,

    /// <summary>A file operation failed while placing it.</summary>
    IoFailure,
}

/// <summary>The answer of <see cref="RunnerHandOff"/>: a status and one English line for a log.</summary>
public sealed record HandOffResult(HandOffStatus Status, string Detail)
{
    public bool IsReady => Status == HandOffStatus.Ready;
}

/// <summary>
/// Who may run <c>I\update\recover.exe</c> and how it gets there (DESIGN-B 5 "the runner runs from its copy" and 13 item 3). Used by the Launcher role of <c>PCMieru.Swap.exe</c> (run as <c>I\PCMieru.Swap.exe</c>,
/// which must not do the swap itself: a running exe cannot be overwritten), by the start gate window's "Recover" button (it only starts recover.exe when <see cref="Check"/> says it is the file the records
/// vouch for) and by the update screen of step 5.
///
/// <b>Which hash is trusted.</b> recover.exe is a copy of the Swap.exe of the generation that was installed when it was made. After a swap that changed Swap.exe, installed.json names the NEW Swap.exe while
/// recover.exe is still the OLD copy, and the journal's <c>from</c> side names exactly that old one. So a recover.exe is trusted when its length and SHA-256 are those of the Swap.exe line of
/// installed.json or of the journal's <c>from</c> record. The journal's <c>to</c> side is not: a recover.exe is never made from a file that has not been verified as installed.
/// </summary>
public static class RunnerHandOff
{
    /// <summary>
    /// Read-only: is <c>I\update\recover.exe</c> there and one of the trusted files? Nothing is written, nothing is started; the update folder being a link is the only other thing looked at.
    /// </summary>
    public static HandOffResult Check(string installFolder)
    {
        var paths = new TxPaths(installFolder);
        if (TxPaths.IsLink(paths.Update))
        {
            return new HandOffResult(HandOffStatus.UpdateFolderUnsafe, "the update folder is a link");
        }

        var presence = ManagedSet.Probe(paths.Recover, out var length, out var sha);
        if (presence != Presence.File)
        {
            return new HandOffResult(HandOffStatus.RunnerNotTrusted, presence == Presence.Absent ? "recover.exe is not there" : "recover.exe is not a plain file");
        }

        var lines = TrustedLines(paths);
        if (lines.Count == 0)
        {
            return new HandOffResult(HandOffStatus.NoRecord, "neither installed.json nor the journal names a Swap.exe");
        }

        foreach (var line in lines)
        {
            if (length == line.Bytes && string.Equals(sha, line.Sha256, StringComparison.Ordinal))
            {
                return new HandOffResult(HandOffStatus.Ready, "recover.exe is the recorded Swap.exe");
            }
        }

        return new HandOffResult(HandOffStatus.RunnerNotTrusted, "recover.exe is not the Swap.exe that installed.json or the journal records");
    }

    /// <summary>
    /// For the Launcher: makes sure a trusted <c>I\update\recover.exe</c> is in place. While an update is open (a journal, its temporary file or the failed-recovery sentinel is there) the file is NEVER replaced: the
    /// recovery may need exactly the copy that was placed before the first change, so it is only checked. Otherwise the recorded Swap.exe (installed.json, or for a folder that was unzipped by hand the
    /// PCMieru.bundle.json of the folder) is copied there through "recover.exe.new" and read back, as the transaction does it. Nothing else of the install is touched.
    /// </summary>
    public static HandOffResult Prepare(string installFolder)
    {
        var paths = new TxPaths(installFolder);
        if (TxPaths.IsLink(paths.Update))
        {
            return new HandOffResult(HandOffStatus.UpdateFolderUnsafe, "the update folder is a link");
        }

        if (ManagedSet.Exists(paths.Journal) || ManagedSet.Exists(paths.JournalTemp) || ManagedSet.Exists(paths.Sentinel))
        {
            return Check(installFolder);
        }

        var record = CurrentRecord(paths);
        if (record is null)
        {
            return new HandOffResult(HandOffStatus.NoRecord, $"there is no installed.json and no readable {BundleFormat.InnerManifestName} to take the hash of the Swap.exe from");
        }

        if (!record.TryGet(RealRunnerPlacement.SwapName, out var line))
        {
            return new HandOffResult(HandOffStatus.SwapExeNotRecorded, "the record has no " + RealRunnerPlacement.SwapName);
        }

        try
        {
            RealRunnerPlacement.EnsureRecoverExe(new TxIo(NoFaults.Instance), paths, line);
        }
        catch (RunnerUnavailableException ex)
        {
            return new HandOffResult(HandOffStatus.SwapExeNotRecorded, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HandOffResult(HandOffStatus.IoFailure, ex.GetType().Name + ": " + ex.Message);
        }

        return new HandOffResult(HandOffStatus.Ready, "recover.exe was made the recorded Swap.exe");
    }

    /// <summary>The Swap.exe lines a recover.exe may be: installed.json's and the journal's <c>from</c> side (see the class comment). Unreadable or refused files are skipped.</summary>
    internal static List<InstalledFile> TrustedLines(TxPaths paths)
    {
        var lines = new List<InstalledFile>();
        if (TxEngine.ReadSmall(paths.Installed, TxJson.MaxBytes) is { } installed
            && InstalledRecord.TryParse(installed, out var record, out _)
            && record.TryGet(RealRunnerPlacement.SwapName, out var installedLine))
        {
            lines.Add(installedLine);
        }

        if (TxEngine.ReadSmall(paths.Journal, TxJson.MaxBytes) is { } journalBytes
            && TxJournal.TryParse(journalBytes, out var journal, out _)
            && journal.From.TryGet(RealRunnerPlacement.SwapName, out var fromLine))
        {
            lines.Add(fromLine);
        }

        return lines;
    }

    /// <summary>installed.json, or the install's own manifest (a folder unzipped by hand); null when neither can be read.</summary>
    private static InstalledRecord? CurrentRecord(TxPaths paths)
    {
        if (ManagedSet.Exists(paths.Installed))
        {
            return TxEngine.ReadSmall(paths.Installed, TxJson.MaxBytes) is { } bytes && InstalledRecord.TryParse(bytes, out var record, out _) ? record : null;
        }

        return TxEngine.ReadSmall(paths.InInstall(BundleFormat.InnerManifestName), BundleManifest.MaxBytes) is { } manifest
            && BundleManifest.TryParse(manifest, out var bundle, out _)
                ? InstalledRecord.FromBundle(bundle, manifest)
                : null;
    }
}

/// <summary>Starts <c>recover.exe</c>. A test replaces it.</summary>
public interface IRunnerStarter
{
    /// <summary>Starts the process and does NOT wait for it. Returns false (and says why) when it could not be started.</summary>
    bool TryStart(string exePath, string workingFolder, IReadOnlyList<string> arguments, out string detail);
}

/// <summary>
/// The real starter: a plain <c>Process.Start</c> (no shell, no redirection: the runner writes to the console it inherits, which is the one a person double-clicking recover.exe sees). The environment is inherited.
/// </summary>
public sealed class ProcessRunnerStarter : IRunnerStarter
{
    public static ProcessRunnerStarter Instance { get; } = new();

    public bool TryStart(string exePath, string workingFolder, IReadOnlyList<string> arguments, out string detail)
    {
        try
        {
            var info = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = workingFolder };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            detail = process is null ? "the process did not start" : "started";
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            detail = ex.GetType().Name;
            return false;
        }
    }
}
