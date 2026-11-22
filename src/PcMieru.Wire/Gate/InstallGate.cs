using System.Security;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Wire.Gate;

/// <summary>What the start gate said (docs/unify/DESIGN-B.md 4). Everything but <see cref="Open"/> means "do not do the normal work".</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<GateOutcome>))] // the contract rule for every public enum of the Wire (a later diagnostics record may carry it); the gate itself never serializes
public enum GateOutcome
{
    /// <summary>The gate file is open (shared, or exclusive for the swapper) and no update was left half done. The caller keeps the <see cref="GateEntry"/> until its work ends.</summary>
    Open,

    /// <summary>Another handle holds the gate in a way that is not compatible (a sharing violation): an update is being installed, or - for an exclusive request - some program of this install is running.</summary>
    Busy,

    /// <summary>The gate was free but <c>I\update\journal.json</c> or <c>journal.json.tmp</c> is there, or one of them cannot be looked at: an update was left open and has to be recovered before anything runs.</summary>
    RecoveryPending,

    /// <summary>As <see cref="RecoveryPending"/>, and the sentinel file <c>I\update\recovery-failed</c> (<see cref="GateFile.RecoveryFailedName"/>) is there: an earlier recovery was tried and failed. Written by the recovery of the swapper when it could not put the earlier version back.</summary>
    RecoveryFailed,

    /// <summary>There is no <c>PCMieru.gate.txt</c> beside the exe (a file copied out of its folder, a gate that was deleted).</summary>
    MissingGate,

    /// <summary>The file system refused the gate: access denied. (Every other trouble with reading it is <see cref="Unreadable"/>.)</summary>
    Denied,

    /// <summary>
    /// A link is where a file of the install should be: the gate file, <c>I\update</c>, a journal file or the sentinel is a reparse point (a symbolic link or a junction), or the gate file has more than one
    /// name (a hard link: a gate shared by two installs would give both the same install id). A link to the install FOLDER is fine: its final path is the real folder.
    /// </summary>
    ReparseRefused,

    /// <summary>The gate file could not be looked at or opened for a reason that is neither "not found", a sharing violation nor access denied: an I/O error, a path that is too long or not valid.</summary>
    Unreadable,
}

/// <summary>
/// The answer of <see cref="InstallGate"/>: an outcome and, for <see cref="GateOutcome.Open"/>, the open gate file. Dispose it when the process's work ends (the file is then free for the
/// swapper). Every other outcome holds nothing: the handle was already closed, so a caller that goes on to recover (the swapper) is not in its own way.
/// </summary>
public sealed class GateEntry : IDisposable
{
    private SafeFileHandle? _handle;

    internal GateEntry(GateOutcome outcome, string gatePath, string detail, string? recoveryNotePath, SafeFileHandle? handle)
    {
        Outcome = outcome;
        GatePath = gatePath;
        Detail = detail;
        RecoveryNotePath = recoveryNotePath;
        _handle = handle;
    }

    /// <summary>What the gate said.</summary>
    public GateOutcome Outcome { get; }

    /// <summary>True for <see cref="GateOutcome.Open"/>.</summary>
    public bool IsOpen => Outcome == GateOutcome.Open;

    /// <summary>
    /// The path the gate was looked for at: <c>&lt;install folder&gt;\PCMieru.gate.txt</c>, the install folder being the folder of the exe's FINAL path (<see cref="InstallGate.InstallFolderOf"/>: links of the folders
    /// followed, 8.3 names long, stored case). The path as the caller spelled it only when the final path could not be had.
    /// </summary>
    public string GatePath { get; }

    /// <summary>One fixed English sentence for the outcome, no path in it: what a console program writes to stderr (<see cref="InstallGate.ReasonOf"/>).</summary>
    public string Reason => InstallGate.ReasonOf(Outcome);

    /// <summary>What exactly was seen (which file, which error), for a log line. English, may name a file.</summary>
    public string Detail { get; }

    /// <summary><c>I\update\RECOVER.txt</c> for <see cref="GateOutcome.RecoveryPending"/> and <see cref="GateOutcome.RecoveryFailed"/>, else null. The swapper writes the note before its first change; the path is given whether or not the file is there.</summary>
    public string? RecoveryNotePath { get; }

    /// <summary>
    /// The bytes of the gate file, read through the handle that is held (null when nothing is held: any outcome but <see cref="GateOutcome.Open"/>, or after <see cref="Dispose"/>; also null when the file is
    /// longer than <paramref name="maxBytes"/> - the gate is a few hundred fixed bytes). The swapper holds the gate EXCLUSIVELY (FileShare.None), so no second open of the same file by path can work: this is
    /// how it checks that the gate is intact while it holds it. The handle was opened for reading, so nothing here can change the file, and the position is not used (a positioned read).
    /// </summary>
    public byte[]? ReadHeld(int maxBytes = 4096)
    {
        var handle = _handle;
        if (handle is null || handle.IsClosed || handle.IsInvalid)
        {
            return null;
        }

        var length = RandomAccess.GetLength(handle);
        if (length > maxBytes)
        {
            return null;
        }

        var bytes = new byte[length];
        var done = 0;
        while (done < bytes.Length)
        {
            var read = RandomAccess.Read(handle, bytes.AsSpan(done), done);
            if (read == 0)
            {
                break;
            }

            done += read;
        }

        return done == bytes.Length ? bytes : null;
    }

    /// <summary>Lets go of the gate file. Safe to call twice.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }
}

/// <summary>
/// The start gate every exe of the app passes before it does its work (DESIGN-B 4). It opens <c>I\PCMieru.gate.txt</c> - I is the folder of the exe's FINAL path (<see cref="InstallFolderOf"/>), never the
/// current directory and never <c>%LOCALAPPDATA%</c> - with <c>FileMode.Open</c>, <c>FileAccess.Read</c> and <c>FileShare.Read</c> (every ordinary program: they all coexist) or <c>FileShare.None</c> (the swapper: it
/// waits for all of them). Delete sharing is never given, and the handle is held until <see cref="GateEntry.Dispose"/>, so the file cannot be removed or replaced while any program of the install runs.
///
/// Who is "the same install" is the file system's answer, not a path's. An exe reached through a symbolic link, a junction, a case spelling or an 8.3 name finds the real install's gate, because I comes from
/// the final path. A hard-linked exe has no other name to give: its final path is the name that was opened, so its gate is the one beside THAT name. What is refused is a link where the files are: the gate
/// itself, the <c>update</c> folder, the journal files and the sentinel being reparse points (a link could point the transaction anywhere), and a gate file with more than one name (the install id is the gate
/// file's id, so a gate hard-linked between two installs would give both the same state folder). The gate file's own attributes and link count are read from the open HANDLE, not from a path.
///
/// After the shared open, and only then, it looks at <c>I\update</c> (an exclusive request does not: the swapper reads its own records): the sentinel <c>recovery-failed</c>, then <c>journal.json</c> and
/// <c>journal.json.tmp</c>. A sentinel is <see cref="GateOutcome.RecoveryFailed"/>; a journal, a lone temporary, or a file that cannot be looked at, is <see cref="GateOutcome.RecoveryPending"/>. The look
/// uses attribute reads that say "access denied" and "not found" apart (File.Exists would say "no" to both). Nothing is parsed and nothing has a size limit: presence is the message.
///
/// The BCL for everything but two questions about a handle (<see cref="GateNative"/>: final path and link count): no JSON reader, and the library loads of the NativeAOT exes stay pinned to System32.
/// </summary>
public static class InstallGate
{
    /// <summary>
    /// The gate of this process: the folder of the final path of <see cref="Environment.ProcessPath"/>. When the host is <c>dotnet.exe</c> (a managed dll started by hand or by <c>dotnet run</c>) there is no exe
    /// of the app, so the folder of the app's own assembly is used (<see cref="AppContext.BaseDirectory"/>, still not the current directory), also by its final path. Shared.
    /// </summary>
    public static GateEntry EnterStartup()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return new GateEntry(GateOutcome.MissingGate, GateFile.FileName, "the path of this process is not known", null, null);
        }

        return IsDotnetHost(path)
            ? Enter(GateNative.FinalPathOf(AppContext.BaseDirectory) ?? AppContext.BaseDirectory, FileShare.Read)
            : EnterStartup(path);
    }

    /// <summary>The gate of the install that <paramref name="executablePath"/> (a fully qualified path of an exe of the app) is in. Shared: what every program but the swapper uses.</summary>
    public static GateEntry EnterStartup(string executablePath) => Enter(InstallFolderOf(executablePath), FileShare.Read);

    /// <summary>
    /// For the swapper (PCMieru.Swap.exe, which also runs as recover.exe): the same gate taken exclusively (<c>FileShare.None</c>). <see cref="GateOutcome.Busy"/> while any program of the install holds it. It does not look at the journal.
    /// </summary>
    public static GateEntry EnterExclusive(string executablePath) => Enter(InstallFolderOf(executablePath), FileShare.None);

    /// <summary>
    /// The install folder I of an exe: the folder of the exe file's FINAL path (GetFinalPathNameByHandle on the file: symbolic links and junctions on the way followed, 8.3 names long, the case that is stored;
    /// <c>\\?\</c> and <c>\\?\UNC\</c> taken off). An exe started through a symbolic link finds the real install; a hard-linked exe has only the name it was opened by, so that is its folder. When the file cannot
    /// be opened for the question (it is not there, access denied) the folder of the path as given is returned. A fully qualified path is required; the file is only asked about (access 0, all sharing).
    /// </summary>
    public static string InstallFolderOf(string executablePath)
    {
        var spelled = FolderOf(executablePath);
        return GateNative.FinalPathOf(executablePath) is { } final && Path.GetDirectoryName(final) is { Length: > 0 } folder ? folder : spelled;
    }

    /// <summary>The path of the gate file of the install that <paramref name="executablePath"/> is in (a fully qualified path): <see cref="InstallFolderOf"/> and the file name. The gate file is not opened.</summary>
    public static string GatePathOf(string executablePath) => Path.Combine(InstallFolderOf(executablePath), GateFile.FileName);

    /// <summary>The one fixed English sentence of an outcome (what a console program writes to stderr; no path, no culture).</summary>
    public static string ReasonOf(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Open => "the start gate is open",
        GateOutcome.Busy => "PC Mieru's files are in use (an update being installed, for example); try again in a moment",
        GateOutcome.RecoveryPending => "an interrupted update has to be recovered before PC Mieru can start",
        GateOutcome.RecoveryFailed => "recovering an interrupted update failed, PC Mieru cannot start until it is repaired",
        GateOutcome.MissingGate => "PCMieru.gate.txt is missing from the install folder, unzip the downloaded ZIP file again",
        GateOutcome.Denied => "the start gate file cannot be opened (access denied)",
        GateOutcome.ReparseRefused => "a link (symbolic link, junction or hard link) is where the start gate or the update records should be, which is not accepted",
        GateOutcome.Unreadable => "the start gate file cannot be read (an I/O error, or a path that is too long)",
        _ => "the start gate did not open",
    };

    private static string FolderOf(string executablePath)
    {
        // A relative path would be completed with the current directory by GetFullPath: refused instead, the gate is never looked for there.
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("the path of the exe must be fully qualified", nameof(executablePath));
        }

        return Path.GetDirectoryName(executablePath) ?? throw new ArgumentException("the path of the exe has no folder", nameof(executablePath));
    }

    private static bool IsDotnetHost(string path) =>
        string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    private enum Seen
    {
        Absent,
        Present,
        Link,
        AccessDenied,
        Unreadable,
    }

    private static GateEntry Enter(string folder, FileShare share)
    {
        var gate = Path.Combine(folder, GateFile.FileName);

        // 1. The gate itself, before it is opened: a link would be followed by the open, and then the "gate" would be some other file.
        switch (Look(gate, out var attributes, out var problem))
        {
            case Seen.Absent:
                return Refuse(GateOutcome.MissingGate, gate, "the gate file is not there");
            case Seen.AccessDenied:
                return Refuse(GateOutcome.Denied, gate, "the gate file cannot be looked at: access denied");
            case Seen.Unreadable:
                return Refuse(GateOutcome.Unreadable, gate, "the gate file cannot be looked at: " + problem);
            case Seen.Link:
                return Refuse(GateOutcome.ReparseRefused, gate, "the gate file is a link");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return Refuse(GateOutcome.MissingGate, gate, "the gate is a folder, not a file");
        }

        // 2. The open: Read, never Delete or Write sharing. Not inheritable (File.OpenHandle makes a plain handle), so no child program holds it by accident.
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(gate, FileMode.Open, FileAccess.Read, share, FileOptions.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refuse(GateOutcome.MissingGate, gate, "the gate file is not there");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Refuse(GateOutcome.Denied, gate, "the gate file cannot be opened: access denied");
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return Refuse(GateOutcome.Busy, gate, "the gate file is held by another handle (sharing violation)");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return Refuse(GateOutcome.Unreadable, gate, "the gate file cannot be opened: " + ex.GetType().Name);
        }

        // The first look above was by PATH, before the open; the open is a separate step, so what the path pointed to may have changed in between. What is checked now is the HANDLE: the file that is
        // really held. (The handle was given without Delete sharing, so this file cannot be removed or replaced under it from here on.) A reparse point that the open did not follow, or a second name,
        // is refused: the install id is the gate file's id, and a gate that two installs hard-link would give both the same state folder.
        if (!GateNative.TryInformationOf(handle, out var held))
        {
            handle.Dispose();
            return Refuse(GateOutcome.Unreadable, gate, "the gate file cannot be asked about itself (GetFileInformationByHandle failed)");
        }

        if ((held.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            return Refuse(GateOutcome.ReparseRefused, gate, "the gate file is a reparse point");
        }

        if (held.NumberOfLinks > 1)
        {
            handle.Dispose();
            return Refuse(GateOutcome.ReparseRefused, gate, "the gate file has " + held.NumberOfLinks.ToString(System.Globalization.CultureInfo.InvariantCulture) + " names (a hard link)");
        }

        if (share == FileShare.None)
        {
            return new GateEntry(GateOutcome.Open, gate, "open, exclusive", null, handle);
        }

        // 3. The update records, with the gate already held shared (a swapper cannot start a transaction between this look and the work that follows).
        var update = Path.Combine(folder, GateFile.UpdateFolderName);
        switch (Look(update, out var updateAttributes, out problem))
        {
            case Seen.Absent:
                return new GateEntry(GateOutcome.Open, gate, "open, no update folder", null, handle);
            case Seen.Link:
                handle.Dispose();
                return Refuse(GateOutcome.ReparseRefused, gate, "the update folder is a link");
            case Seen.AccessDenied:
                return Recovery(handle, gate, update, "the update folder cannot be looked at: access denied", failed: false);
            case Seen.Unreadable:
                return Recovery(handle, gate, update, "the update folder cannot be looked at: " + problem, failed: false);
        }

        if ((updateAttributes & FileAttributes.Directory) == 0)
        {
            return Recovery(handle, gate, update, "update is a file, not a folder", failed: false);
        }

        // The sentinel of a failed recovery comes first, and is only looked at (nothing in it is read). Present: failed. A link: refused. Cannot be looked at: undecidable, so "pending" (the start stops either
        // way; whoever recovers sees the sentinel itself). Not there: go on to the journal.
        string? pendingReason = null;
        switch (Look(Path.Combine(update, GateFile.RecoveryFailedName), out _, out problem))
        {
            case Seen.Absent:
                break;
            case Seen.Link:
                handle.Dispose();
                return Refuse(GateOutcome.ReparseRefused, gate, GateFile.RecoveryFailedName + " is a link");
            case Seen.Present:
                return Recovery(handle, gate, update, GateFile.RecoveryFailedName + " is present", failed: true);
            case Seen.AccessDenied:
                pendingReason = GateFile.RecoveryFailedName + " cannot be looked at: access denied";
                break;
            default:
                pendingReason = GateFile.RecoveryFailedName + " cannot be looked at: " + problem;
                break;
        }

        foreach (var name in new[] { GateFile.JournalName, GateFile.JournalTempName })
        {
            switch (Look(Path.Combine(update, name), out _, out problem))
            {
                case Seen.Absent:
                    break;
                case Seen.Link:
                    handle.Dispose();
                    return Refuse(GateOutcome.ReparseRefused, gate, name + " is a link");
                case Seen.Present:
                    pendingReason ??= name == GateFile.JournalName ? "journal.json is present" : "journal.json.tmp is present";
                    break;
                case Seen.AccessDenied:
                    pendingReason ??= name + " cannot be looked at: access denied";
                    break;
                default:
                    pendingReason ??= name + " cannot be looked at: " + problem;
                    break;
            }
        }

        return pendingReason is null
            ? new GateEntry(GateOutcome.Open, gate, "open", null, handle)
            : Recovery(handle, gate, update, pendingReason, failed: false);
    }

    private static GateEntry Refuse(GateOutcome outcome, string gate, string detail) => new(outcome, gate, detail, null, null);

    /// <summary>Recovery pending or failed: the shared handle is closed first, so whoever recovers (the swapper) can take the gate itself. Failed only when the sentinel was seen.</summary>
    private static GateEntry Recovery(SafeFileHandle handle, string gate, string update, string detail, bool failed)
    {
        handle.Dispose();
        return new GateEntry(failed ? GateOutcome.RecoveryFailed : GateOutcome.RecoveryPending, gate, detail, Path.Combine(update, GateFile.RecoverNoteName), null);
    }

    /// <summary>
    /// One attribute read that tells "not there" (FileNotFound, DirectoryNotFound), "refused" (UnauthorizedAccess) and "something else" apart. File.Exists says false to all three, which
    /// would let a journal that cannot be read look like no journal.
    /// </summary>
    private static Seen Look(string path, out FileAttributes attributes, out string problem)
    {
        problem = string.Empty;
        attributes = 0;
        try
        {
            attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0 ? Seen.Link : Seen.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Seen.Absent;
        }
        catch (UnauthorizedAccessException)
        {
            return Seen.AccessDenied;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or SecurityException)
        {
            problem = ex.GetType().Name;
            return Seen.Unreadable;
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33): the low word of the HRESULT .NET puts on the IOException.</summary>
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
