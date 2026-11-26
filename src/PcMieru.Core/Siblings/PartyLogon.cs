using System.Security;
using System.Text.Json.Serialization;
using PcMieru.Wire;

namespace PcMieru.Core.Siblings;

/// <summary>What <see cref="PartyLogon.Read"/> found.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PartyLogonState>))]
public enum PartyLogonState
{
    /// <summary>There is no registration of ours.</summary>
    Off,

    /// <summary>Ours (the value names this exact exe with the logon argument) and Windows runs it: no approval stamp, or one that says enabled.</summary>
    On,

    /// <summary>Ours, but the approval stamp says Windows (Task Manager or the Settings page) turned it off.</summary>
    DisabledInWindows,

    /// <summary>The value of our name points at another PCMieru.Party.exe (another folder or install). It is not changed unless the user asks to register this one.</summary>
    OtherPlacement,

    /// <summary>Could not be read or is not in a form we know: access denied, a failure, a value of another type, a command line that is not ours, a stamp of an unknown format. Never rewritten by a read.</summary>
    Unknown,
}

/// <summary>Why <see cref="PartyLogon.Enable"/> or <see cref="PartyLogon.Disable"/> did not finish.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PartyLogonFailure>))]
public enum PartyLogonFailure
{
    None,

    /// <summary>The exe path handed in is not an absolute path. Nothing was written.</summary>
    InvalidPath,

    /// <summary>The registry said access denied.</summary>
    Denied,

    /// <summary>Any other failure of a read, a write or a delete.</summary>
    Error,

    /// <summary>Every call worked, but reading the result back did not show the wanted state (see <see cref="PartyLogonResult.Detail"/>).</summary>
    NotVerified,

    /// <summary>Another program holds the lock of the sign-in entries (a switch from the old value, a recovery, or another change). Nothing was written; trying again later works.</summary>
    Busy,

    /// <summary>The Run command for this exe would be 260 characters or more (the limit of a Run value that Windows starts). Nothing was written.</summary>
    TooLong,
}

/// <summary>The outcome of a change. Never an exception: the settings page shows a line for a failure and goes on.</summary>
public sealed record PartyLogonResult(bool Ok, PartyLogonFailure Failure, string Detail)
{
    public static PartyLogonResult Success { get; } = new(true, PartyLogonFailure.None, string.Empty);
}

/// <summary>
/// The "start Parts Life at sign-in" setting (DESIGN-C 3 item 5): one value of HKCU Run named <see cref="ValueName"/>, whose data is the absolute path of this install's
/// PCMieru.Party.exe in quotes plus <see cref="Argument"/>, and the Windows approval stamp that goes with it. Everything goes through an <see cref="IRunStore"/>: this class never names
/// the registry, so a test or a smoke run (which hand in a <see cref="MemoryRunStore"/>) cannot reach it. Nothing here throws to its caller.
/// </summary>
public static class PartyLogon
{
    /// <summary>The Run value and the approval value of the setting.</summary>
    public const string ValueName = "PCMieru.Party";

    /// <summary>The argument of a start from the Run key (see PartyArgs of the Party exe).</summary>
    public const string Argument = "--logon";

    /// <summary>The data of the Run value for this exe: the path in quotes, a space, <see cref="Argument"/>.</summary>
    public static string ExpectedValue(string exePath) => "\"" + exePath + "\" " + Argument;

    /// <summary>A Run command of this many characters or more is refused (Windows starts a Run value only up to a 259-character command line).</summary>
    public const int MaxCommandLength = 260;

    /// <summary>True when the Run command for <paramref name="exePath"/> would be <see cref="MaxCommandLength"/> characters or more.</summary>
    public static bool IsTooLong(string exePath) => ExpectedValue(exePath).Length >= MaxCommandLength;

    /// <summary>
    /// The state of the setting for <paramref name="expectedExePath"/>, the PCMieru.Party.exe of this install. The rules of the approval stamp are the ones of Task Manager's: a key that opened and
    /// holds no stamp = Windows runs it; a stamp of the enabled kind = enabled; a stamp of the disabled kind = off in Windows; denied or an unknown format = not enabled, shown as unknown.
    /// </summary>
    public static PartyLogonState Read(IRunStore store, string expectedExePath)
    {
        try
        {
            var run = store.ReadRun(ValueName);
            if (run is null)
            {
                return PartyLogonState.Off;
            }

            if (run.Text is null || !TryParse(run.Text, out var path, out var arguments))
            {
                return PartyLogonState.Unknown;
            }

            if (!SamePath(path, expectedExePath))
            {
                return PartyLogonState.OtherPlacement;
            }

            if (!string.Equals(arguments, Argument, StringComparison.OrdinalIgnoreCase))
            {
                return PartyLogonState.Unknown;
            }

            var approval = store.ReadApproval(ValueName);
            var stamp = approval is null ? ApprovalState.Enabled : StartupApprovedCodec.Decode(approval.Kind, approval.Bytes);
            return stamp switch
            {
                ApprovalState.Enabled => PartyLogonState.On,
                ApprovalState.Disabled => PartyLogonState.DisabledInWindows,
                _ => PartyLogonState.Unknown,
            };
        }
        catch (Exception)
        {
            return PartyLogonState.Unknown;
        }
    }

    /// <summary>
    /// Registers <paramref name="exePath"/>: writes the Run value, makes a stamp that is there (and not enabled) enabled, and reads the result back. A stamp that is not there is left absent (Windows
    /// runs the entry). When the stamp could not be made, the Run value is put back as it was. Replaces the value of another placement: the user asked for this one.
    /// </summary>
    /// <remarks>
    /// <paramref name="journalPath"/> is the journal of the switch from the old value (<see cref="RunSwitch.JournalPathFor"/>); null = the caller has none. With it, under the lock, a journal that is there is
    /// recovered first (<see cref="RunSwitch.RecoverHeld"/>); one that cannot be settled (the user changed an entry in Windows, or the recovery failed) is settled by this change instead: the owner's explicit
    /// choice wins, and when it was written the journal is deleted, so a later recovery cannot undo it. <paramref name="fileExists"/> is for tests.
    /// </remarks>
    public static PartyLogonResult Enable(IRunStore store, string exePath, string? lockName = null, TimeSpan? lockWait = null, string? journalPath = null, Func<string, bool>? fileExists = null) =>
        Locked(lockName, lockWait, () => Settling(store, journalPath, fileExists, () => EnableCore(store, exePath)));

    private static PartyLogonResult EnableCore(IRunStore store, string exePath)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath))
            {
                return new PartyLogonResult(false, PartyLogonFailure.InvalidPath, "not an absolute path");
            }

            full = Path.GetFullPath(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PartyLogonResult(false, PartyLogonFailure.InvalidPath, ex.GetType().Name);
        }

        if (IsTooLong(full))
        {
            return new PartyLogonResult(false, PartyLogonFailure.TooLong, ExpectedValue(full).Length + " characters");
        }

        RunValue? before;
        try
        {
            before = store.ReadRun(ValueName);
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }

        try
        {
            store.WriteRun(ValueName, ExpectedValue(full));
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }

        try
        {
            var stamp = store.ReadApproval(ValueName);
            if (stamp is not null && StartupApprovedCodec.Decode(stamp.Kind, stamp.Bytes) != ApprovalState.Enabled)
            {
                store.WriteApproval(ValueName, StartupApprovedCodec.EncodeEnabled());
            }
        }
        catch (Exception ex)
        {
            PutBack(store, before);
            return Failed(ex);
        }

        var state = Read(store, full);
        return state == PartyLogonState.On
            ? PartyLogonResult.Success
            : new PartyLogonResult(false, PartyLogonFailure.NotVerified, "read back: " + state);
    }

    /// <summary>Removes the Run value of our name and its approval stamp, and checks that both are gone. Stops at the first failure (the stamp is not touched when the Run value stays).</summary>
    /// <remarks>About <paramref name="journalPath"/> and <paramref name="fileExists"/> see <see cref="Enable"/>: a stuck journal is settled by this change.</remarks>
    public static PartyLogonResult Disable(IRunStore store, string? lockName = null, TimeSpan? lockWait = null, string? journalPath = null, Func<string, bool>? fileExists = null) =>
        Locked(lockName, lockWait, () => Settling(store, journalPath, fileExists, () => DisableCore(store)));

    /// <summary>
    /// Runs <paramref name="change"/> (the lock is held) after the journal of an earlier switch was dealt with: recovered when its values are known, otherwise left for the owner's change to settle, which
    /// deletes it once the change was written. No journal path or no journal: just the change.
    /// </summary>
    private static PartyLogonResult Settling(IRunStore store, string? journalPath, Func<string, bool>? fileExists, Func<PartyLogonResult> change)
    {
        if (journalPath is null)
        {
            return change();
        }

        var journal = new FileRunSwitchJournal(journalPath);
        var stuck = RunSwitch.RecoverHeld(store, journal, fileExists).NeedsAttention;
        var result = change();
        if (!stuck || !result.Ok)
        {
            return result;
        }

        try
        {
            journal.Delete();
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }

        return result;
    }

    private static PartyLogonResult DisableCore(IRunStore store)
    {
        try
        {
            store.DeleteRun(ValueName);
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }

        try
        {
            store.DeleteApproval(ValueName);
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }

        try
        {
            return store.ReadRun(ValueName) is null && store.ReadApproval(ValueName) is null
                ? PartyLogonResult.Success
                : new PartyLogonResult(false, PartyLogonFailure.NotVerified, "a value is still there");
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }
    }

    /// <summary>Runs a change under <see cref="RunSwitchLock"/> (the same lock as the switch from the old value). Taken and given back on this thread, inside this call.</summary>
    private static PartyLogonResult Locked(string? lockName, TimeSpan? lockWait, Func<PartyLogonResult> change)
    {
        RunSwitchLock? held;
        try
        {
            held = RunSwitchLock.TryAcquire(lockName ?? RunSwitchLock.CurrentName(), lockWait ?? RunSwitchLock.DefaultWait);
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }

        if (held is null)
        {
            return new PartyLogonResult(false, PartyLogonFailure.Busy, "another change of the sign-in entries is running");
        }

        using (held)
        {
            return change();
        }
    }

    private static PartyLogonResult Failed(Exception ex) => new(
        false,
        ex is UnauthorizedAccessException or SecurityException ? PartyLogonFailure.Denied : PartyLogonFailure.Error,
        ex.GetType().Name);

    /// <summary>Best effort: the Run value as it was before a change that did not finish. A failure here is not reported on top of the one that caused it.</summary>
    private static void PutBack(IRunStore store, RunValue? before)
    {
        try
        {
            if (before is null)
            {
                store.DeleteRun(ValueName);
            }
            else if (before.Text is not null)
            {
                store.WriteRun(ValueName, before.Text, before.Kind);
            }
        }
        catch (Exception)
        {
            // the caller already has the failure that made this necessary
        }
    }

    /// <summary>Splits <c>"C:\path\x.exe" args</c> into the path and the rest. False for anything that does not start with a quoted path.</summary>
    internal static bool TryParse(string command, out string path, out string arguments)
    {
        path = string.Empty;
        arguments = string.Empty;
        var text = command.Trim();
        if (text.Length < 3 || text[0] != '"')
        {
            return false;
        }

        var close = text.IndexOf('"', 1);
        if (close < 2)
        {
            return false;
        }

        path = text[1..close];
        arguments = text[(close + 1)..].Trim();
        return true;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
