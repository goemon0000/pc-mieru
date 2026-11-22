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

    /// <summary>As <see cref="RecoveryPending"/>, and <c>I\update\last-result.json</c> says an earlier recovery failed (<see cref="GateFile.RecoveryFailedToken"/>). Written by step 4; before it nothing produces this.</summary>
    RecoveryFailed,

    /// <summary>There is no <c>PCMieru.gate.txt</c> beside the exe (a file copied out of its folder, a gate that was deleted).</summary>
    MissingGate,

    /// <summary>The file system refused the gate (access denied, or any I/O error that is neither "not found" nor a sharing violation).</summary>
    Denied,

    /// <summary>The gate file itself, <c>I\update</c> or a journal file is a reparse point (a symbolic link or a junction). A link to the install FOLDER is fine: it resolves to the same real file.</summary>
    ReparseRefused,
}

/// <summary>
/// The answer of <see cref="InstallGate"/>: an outcome and, for <see cref="GateOutcome.Open"/>, the open gate file. Dispose it when the process's work ends (the file is then free for the
/// swapper). Every other outcome holds nothing: the handle was already closed, so a caller that goes on to recover (step 4) is not in its own way.
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

    /// <summary>The path the gate was looked for at (<c>&lt;folder of the exe&gt;\PCMieru.gate.txt</c>, as spelled by the caller). Not a normalised or resolved path.</summary>
    public string GatePath { get; }

    /// <summary>One fixed English sentence for the outcome, no path in it: what a console program writes to stderr (<see cref="InstallGate.ReasonOf"/>).</summary>
    public string Reason => InstallGate.ReasonOf(Outcome);

    /// <summary>What exactly was seen (which file, which error), for a log line. English, may name a file.</summary>
    public string Detail { get; }

    /// <summary><c>I\update\RECOVER.txt</c> for <see cref="GateOutcome.RecoveryPending"/> and <see cref="GateOutcome.RecoveryFailed"/>, else null. The note itself comes in step 4: until then the file does not exist.</summary>
    public string? RecoveryNotePath { get; }

    /// <summary>Lets go of the gate file. Safe to call twice.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }
}

/// <summary>
/// The start gate every exe of the app passes before it does its work (DESIGN-B 4). It opens <c>I\PCMieru.gate.txt</c> - I is the folder of the real exe, never the current directory
/// and never <c>%LOCALAPPDATA%</c> - with <c>FileMode.Open</c>, <c>FileAccess.Read</c> and <c>FileShare.Read</c> (every ordinary program: they all coexist) or <c>FileShare.None</c> (the swapper: it
/// waits for all of them). Delete sharing is never given, and the handle is held until <see cref="GateEntry.Dispose"/>, so the file cannot be removed or replaced while any program of the install runs.
///
/// Who is "the same install" is the file system's answer, not a path's: a junction, a case spelling or an 8.3 name of the install folder reaches the same file, so the same sharing rules.
/// What is refused is a link where the files are: the gate itself, the <c>update</c> folder and the journal files being reparse points (a link could point the transaction anywhere).
///
/// After the shared open, and only then, it looks at <c>I\update\journal.json</c> and <c>journal.json.tmp</c> (an exclusive request does not: the swapper reads its own journal). A journal, a lone
/// temporary, or a file that cannot be looked at, is <see cref="GateOutcome.RecoveryPending"/>. The look uses attribute reads that say "access denied" and "not found" apart (File.Exists would say "no" to both).
///
/// BCL only (no P/Invoke, no JSON reader): the Helper and the network meter are NativeAOT and pin their library loads to System32.
/// </summary>
public static class InstallGate
{
    /// <summary>
    /// The gate of this process: the folder of <see cref="Environment.ProcessPath"/>. When the host is <c>dotnet.exe</c> (a managed dll started by hand or by <c>dotnet run</c>) there is no exe of the
    /// app, so the folder of the app's own assembly is used (<see cref="AppContext.BaseDirectory"/>, still not the current directory). Shared.
    /// </summary>
    public static GateEntry EnterStartup()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return new GateEntry(GateOutcome.MissingGate, GateFile.FileName, "the path of this process is not known", null, null);
        }

        return IsDotnetHost(path)
            ? Enter(AppContext.BaseDirectory, FileShare.Read)
            : EnterStartup(path);
    }

    /// <summary>The gate of the install that <paramref name="executablePath"/> (a fully qualified path of an exe of the app) is in. Shared: what every program but the swapper uses.</summary>
    public static GateEntry EnterStartup(string executablePath) => Enter(FolderOf(executablePath), FileShare.Read);

    /// <summary>
    /// For the swapper of step 4: the same gate taken exclusively (<c>FileShare.None</c>). <see cref="GateOutcome.Busy"/> while any program of the install holds it. It does not look at the journal.
    /// </summary>
    public static GateEntry EnterExclusive(string executablePath) => Enter(FolderOf(executablePath), FileShare.None);

    /// <summary>The path of the gate file of the install that <paramref name="executablePath"/> is in (a fully qualified path), by name only: nothing is opened.</summary>
    public static string GatePathOf(string executablePath) => Path.Combine(FolderOf(executablePath), GateFile.FileName);

    /// <summary>The one fixed English sentence of an outcome (what a console program writes to stderr; no path, no culture).</summary>
    public static string ReasonOf(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Open => "the start gate is open",
        GateOutcome.Busy => "an update is being installed (or the start gate is held exclusively), try again in a moment",
        GateOutcome.RecoveryPending => "an interrupted update has to be recovered before PC Mieru can start",
        GateOutcome.RecoveryFailed => "recovering an interrupted update failed, PC Mieru cannot start until it is repaired",
        GateOutcome.MissingGate => "PCMieru.gate.txt is missing from the install folder, unzip the downloaded ZIP file again",
        GateOutcome.Denied => "the start gate file cannot be read (access denied or an I/O error)",
        GateOutcome.ReparseRefused => "the start gate or the update records are behind a link, which is not accepted",
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
                return Refuse(GateOutcome.Denied, gate, "the gate file cannot be looked at: " + problem);
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
        catch (UnauthorizedAccessException)
        {
            return Refuse(GateOutcome.Denied, gate, "the gate file cannot be opened: access denied");
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return Refuse(GateOutcome.Busy, gate, "the gate file is held by another handle (sharing violation)");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or SecurityException)
        {
            return Refuse(GateOutcome.Denied, gate, "the gate file cannot be opened: " + ex.GetType().Name);
        }

        // The handle was given without Delete sharing, so from here on the file cannot be replaced under it: this second look is not a race with the first.
        if (Look(gate, out _, out _) == Seen.Link)
        {
            handle.Dispose();
            return Refuse(GateOutcome.ReparseRefused, gate, "the gate file is a link");
        }

        if (share == FileShare.None)
        {
            return new GateEntry(GateOutcome.Open, gate, "open, exclusive", null, handle);
        }

        // 3. The journal, with the gate already held shared (a swapper cannot start a transaction between this look and the work that follows).
        var update = Path.Combine(folder, GateFile.UpdateFolderName);
        switch (Look(update, out var updateAttributes, out problem))
        {
            case Seen.Absent:
                return new GateEntry(GateOutcome.Open, gate, "open, no update folder", null, handle);
            case Seen.Link:
                handle.Dispose();
                return Refuse(GateOutcome.ReparseRefused, gate, "the update folder is a link");
            case Seen.AccessDenied:
                return Recovery(handle, gate, update, "the update folder cannot be looked at: access denied");
            case Seen.Unreadable:
                return Recovery(handle, gate, update, "the update folder cannot be looked at: " + problem);
        }

        if ((updateAttributes & FileAttributes.Directory) == 0)
        {
            return Recovery(handle, gate, update, "update is a file, not a folder");
        }

        string? pendingReason = null;
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
            : Recovery(handle, gate, update, pendingReason);
    }

    private static GateEntry Refuse(GateOutcome outcome, string gate, string detail) => new(outcome, gate, detail, null, null);

    /// <summary>Recovery pending or failed: the shared handle is closed first, so whoever recovers (step 4) can take the gate itself.</summary>
    private static GateEntry Recovery(SafeFileHandle handle, string gate, string update, string detail)
    {
        handle.Dispose();
        var failed = ReadsRecoveryFailed(Path.Combine(update, GateFile.LastResultName));
        return new GateEntry(
            failed ? GateOutcome.RecoveryFailed : GateOutcome.RecoveryPending,
            gate,
            failed ? detail + "; last-result.json says the recovery failed" : detail,
            Path.Combine(update, GateFile.RecoverNoteName),
            null);
    }

    /// <summary>True when the first bytes of last-result.json carry <see cref="GateFile.RecoveryFailedToken"/>. Any trouble reading it is "no": the journal alone already stops the start.</summary>
    private static bool ReadsRecoveryFailed(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var buffer = new byte[GateFile.MarkerReadBytes];
            var count = 0;
            int read;
            while (count < buffer.Length && (read = file.Read(buffer, count, buffer.Length - count)) > 0)
            {
                count += read;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, 0, count).Contains(GateFile.RecoveryFailedToken, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
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
