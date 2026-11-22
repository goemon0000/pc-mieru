using PcMieru.Update.Feed;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Transaction;

/// <summary>
/// Where everything of the transaction lives (DESIGN-B 4). <c>I</c> is the install folder; everything the transaction adds is under <c>I\update</c> and on the SAME volume as <c>I</c>, so every rename into place is
/// a rename and never a copy.
///
/// <code>
/// I\update\installed.json                          the record of what is installed (the only basis for deleting a file)
/// I\update\journal.json (+ .tmp)                   the open transaction
/// I\update\prev-&lt;old version&gt;\&lt;tx&gt;\files\...      a copy of the old set; ...\installed.json the old record
/// I\update\work\&lt;tx&gt;\next\...                      the new files, ready to be renamed into place
/// I\update\work\&lt;tx&gt;\displaced\...                 old files the new version does not have (moved, not deleted)
/// I\update\work\&lt;tx&gt;\restore\...                   an old file being copied back from the backup (renamed into place when whole)
/// I\update\recover.exe, RECOVER.txt                the runner's copy and the note for a person
/// I\update\last-result.json, blocked.json, recovery-failed
/// </code>
/// </summary>
public sealed class TxPaths
{
    public TxPaths(string installFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(installFolder);
        Install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder));
        Update = Path.Combine(Install, GateFile.UpdateFolderName);
    }

    /// <summary><c>I</c>.</summary>
    public string Install { get; }

    /// <summary><c>I\update</c>.</summary>
    public string Update { get; }

    public string Gate => Path.Combine(Install, GateFile.FileName);

    public string Installed => Path.Combine(Update, "installed.json");

    public string Journal => Path.Combine(Update, GateFile.JournalName);

    public string JournalTemp => Path.Combine(Update, GateFile.JournalTempName);

    /// <summary><c>I\update\recover.exe</c>: the runner of a swap and of a recovery.</summary>
    public string Recover => Path.Combine(Update, "recover.exe");

    /// <summary><c>I\update\RECOVER.txt</c> (<see cref="GateFile.RecoverNoteName"/>).</summary>
    public string Note => Path.Combine(Update, GateFile.RecoverNoteName);

    public string LastResult => Path.Combine(Update, GateFile.LastResultName);

    /// <summary>The sentinel of a failed recovery (<see cref="GateFile.RecoveryFailedName"/>).</summary>
    public string Sentinel => Path.Combine(Update, GateFile.RecoveryFailedName);

    public string Blocked => Path.Combine(Update, "blocked.json");

    /// <summary><c>I\update\prev-&lt;old&gt;\&lt;tx&gt;</c>.</summary>
    public string Prev(string oldVersion, string tx) => Path.Combine(Update, "prev-" + oldVersion, tx);

    public string PrevFiles(string oldVersion, string tx) => Path.Combine(Prev(oldVersion, tx), "files");

    public string PrevInstalled(string oldVersion, string tx) => Path.Combine(Prev(oldVersion, tx), "installed.json");

    public string Work(string tx) => Path.Combine(Update, "work", tx);

    public string Next(string tx) => Path.Combine(Work(tx), "next");

    public string Displaced(string tx) => Path.Combine(Work(tx), "displaced");

    public string Restore(string tx) => Path.Combine(Work(tx), "restore");

    /// <summary>Where a managed file that a running program has mapped is renamed aside to (a mapped image cannot be overwritten or deleted, but its name can be changed): work/&lt;tx&gt;/inuse.</summary>
    public string InUse(string tx) => Path.Combine(Work(tx), "inuse");

    /// <summary>The path of a bundle-relative name (with "/") below <paramref name="root"/>. The name must already have passed <c>BundleNames.Check</c> (the readers of a record and a journal do that).</summary>
    public static string Under(string root, string relativeName) => Path.Combine(root, relativeName.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The path of a bundle-relative name in the install folder.</summary>
    public string InInstall(string relativeName) => Under(Install, relativeName);

    /// <summary>A transaction id: 12 lower-case hex digits (short, because the folders made from it sit deep in the install folder).</summary>
    public static string NewTx() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().AsSpan(0, 6));

    /// <summary>The two folders that hold transaction folders: <c>work</c> and <c>prev-&lt;version&gt;</c> (the only parents a cleaning may remove when they are left empty).</summary>
    public static bool IsContainerName(string name) =>
        string.Equals(name, "work", StringComparison.OrdinalIgnoreCase) || name.StartsWith("prev-", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidTx(string? tx) => tx is { Length: 12 } && tx.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>The folders a preparation leaves that a cleaning may delete (see <see cref="ScanStale"/>). Nothing is returned when update/ does not exist.</summary>
    public IReadOnlyList<string> TransactionFolders() => ScanStale().ToDelete;

    /// <summary>
    /// What a cleaning of update/ may delete and what it must leave alone. <see cref="ToDelete"/>: <c>work/&lt;tx&gt;</c> and <c>prev-&lt;version&gt;/&lt;tx&gt;</c> (a real folder with a transaction id for a name, <see cref="IsValidTx"/>), and a LINK at
    /// <c>work</c>, at <c>prev-*</c> or at one of those (the link itself is what is deleted: <see cref="TxIo.DeleteTree"/> unlinks it). <see cref="LeftAlone"/>: anything else (a file, a folder with another name, a
    /// <c>prev-</c> entry whose suffix is not a version), as paths relative to update/ for a text. A link is NEVER entered: not to list it, not to delete in it (a junction made to point at a folder of the user's would
    /// otherwise lose it).
    /// </summary>
    public StaleScan ScanStale()
    {
        var delete = new List<string>();
        var alone = new List<string>();
        if (ManagedSet.Look(Update) != Presence.Other)
        {
            return new StaleScan(delete, alone);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(Update))
        {
            var name = Path.GetFileName(entry);
            var isWork = string.Equals(name, "work", StringComparison.OrdinalIgnoreCase);
            var isPrev = name.StartsWith("prev-", StringComparison.OrdinalIgnoreCase);
            if (!isWork && !isPrev)
            {
                continue; // installed.json, the journal, recover.exe, ...: not a transaction folder
            }

            if (isPrev && !(AppVersion.TryParse(name["prev-".Length..], out var version) && version.ToString() == name["prev-".Length..]))
            {
                alone.Add(name);
                continue;
            }

            switch (ManagedSet.Look(entry))
            {
                case Presence.Link:
                    delete.Add(entry); // unlinked, never entered
                    break;
                case Presence.Other:
                    foreach (var child in Directory.EnumerateFileSystemEntries(entry))
                    {
                        var childName = Path.GetFileName(child);
                        if (IsValidTx(childName) && ManagedSet.Look(child) is Presence.Other or Presence.Link)
                        {
                            delete.Add(child);
                        }
                        else
                        {
                            alone.Add(name + "/" + childName);
                        }
                    }

                    break;
                case Presence.File:
                    alone.Add(name);
                    break;
            }
        }

        return new StaleScan(delete, alone);
    }

    /// <summary>Whether <paramref name="path"/> exists and is a link (a reparse point). A path that is not there is not a link.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null
                : Directory.Exists(path) && (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) || new DirectoryInfo(path).LinkTarget is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // cannot be looked at: not a place to write to
        }
    }
}

/// <summary>What <see cref="TxPaths.ScanStale"/> found: the folders (and links) a cleaning deletes, and the entries of update/ it leaves alone (relative to update/, with "/").</summary>
public sealed record StaleScan(IReadOnlyList<string> ToDelete, IReadOnlyList<string> LeftAlone);
