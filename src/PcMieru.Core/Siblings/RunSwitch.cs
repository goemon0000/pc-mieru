using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using PcMieru.Wire;

namespace PcMieru.Core.Siblings;

/// <summary>What <see cref="RunSwitch.Offer"/> / <see cref="RunSwitch.Switch"/> / <see cref="RunSwitch.Recover"/> ended with.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RunSwitchStatus>))]
public enum RunSwitchStatus
{
    /// <summary>Switch: the sign-in start now runs the new exe through the new value; the old value and its stamp are gone.</summary>
    Switched,

    /// <summary>Switch: the four values are not (any more) in a state that the offer needs. Nothing was written.</summary>
    NotOffered,

    /// <summary>Switch: it did not finish and the four values are exactly as they were (nothing written, or all put back). Not shown to the user: nothing is worse than before.</summary>
    RolledBack,

    /// <summary>Switch: it did not finish and the values could not all be put back. The journal is kept so the next start tries again.</summary>
    RestoreFailed,

    /// <summary>Recover / Switch: a journal is there, and the four values match none of its states (the user changed something in Windows meanwhile), or the journal itself cannot be read. Nothing written, the journal kept.</summary>
    NeedsUserCheck,

    /// <summary>Another program holds the lock (it is switching or changing the setting right now). Nothing done.</summary>
    Busy,

    /// <summary>Recover: no journal, or a committed one (removed). Nothing to put back.</summary>
    NothingToDo,

    /// <summary>Recover: the values of an unfinished switch were put back and the journal removed.</summary>
    Recovered,

    /// <summary>Recover: reading the store or putting the values back failed. The journal is kept for a retry.</summary>
    RecoveryFailed,
}

public sealed record RunSwitchResult(RunSwitchStatus Status, string Detail)
{
    /// <summary>True when the user should be told to check the sign-in setting (never for a clean rollback or a busy lock).</summary>
    public bool NeedsAttention => Status is RunSwitchStatus.RestoreFailed or RunSwitchStatus.NeedsUserCheck or RunSwitchStatus.RecoveryFailed;
}

/// <summary>Why <see cref="RunSwitch.Offer"/> did not ask.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RunSwitchReason>))]
public enum RunSwitchReason
{
    /// <summary>It asks.</summary>
    None,

    InvalidPath,

    /// <summary>A store call failed or was denied: not enough known to offer anything.</summary>
    Unreadable,

    /// <summary>There is no old Run value.</summary>
    OldNotThere,

    /// <summary>The old value does not point at an existing PartsLife.exe or PartsParty.exe.</summary>
    NotOldExe,

    /// <summary>The old entry is off in Windows (stamp of the disabled kind), or its stamp is of a format we do not know.</summary>
    NotEnabled,

    /// <summary>The new name already has a Run value: the user (or an earlier switch) already chose.</summary>
    NewExists,

    /// <summary>A stamp of the new name that is not binary could not be put back exactly.</summary>
    Unrestorable,

    /// <summary>The Run command for the new exe would be 260 characters or more (<see cref="PartyLogon.MaxCommandLength"/>). Nothing is written.</summary>
    TooLong,
}

public sealed record RunSwitchOffer(bool Ask, RunSwitchReason Reason);

/// <summary>
/// The one-time move of the sign-in start from the old Parts Life Run value (<c>PartsLife</c>) to the new one (<see cref="PartyLogon.ValueName"/>), DESIGN-C 4.3. It touches four values:
/// Run <c>PartsLife</c>, Run <c>PCMieru.Party</c>, StartupApproved <c>PartsLife</c>, StartupApproved <c>PCMieru.Party</c>. Everything goes through an <see cref="IRunStore"/> (a test and a smoke run hand in a
/// <see cref="MemoryRunStore"/>) and every change runs under <see cref="RunSwitchLock"/>. A journal (JSON, written as temp file + write-through rename) holds the four values as they were and the phase
/// (<c>prepared</c>, then <c>committed</c>); <see cref="Recover"/> reads it at the next start. Nothing here throws to its caller.
/// <para>
/// ORDER (so that a crash at any store call leaves at least one start that works; worst case both start once, and the next start's Recover settles it): journal prepared, new stamp disabled, new Run written,
/// new stamp enabled (or removed when it did not exist), THEN old Run deleted, old stamp deleted, read back, flush, journal committed, journal deleted.
/// The way back is the mirror: the new entry is made not to start first (new Run and new stamp to their "before" values), THEN the old Run and old stamp are put back, so a restore interrupted
/// halfway never leaves both entries enabled (it may leave neither when the crash had already deleted the old Run; the journal stays and the next start continues).
/// </para>
/// </summary>
public static class RunSwitch
{
    public const string OldName = "PartsLife";
    public const string NewName = PartyLogon.ValueName;
    public const string JournalFileName = "run-switch.json";

    private const string Prepared = "prepared";
    private const string Committed = "committed";

    private static readonly string[] OldExeNames = ["PartsLife.exe", "PartsParty.exe"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The journal of a switch: next to the save of the new app (the folder that holds state.json).</summary>
    public static string JournalPathFor(string saveFolder) => Path.Combine(saveFolder, JournalFileName);

    /// <summary>
    /// Asks only when the old Run value points at an existing old exe (PartsLife.exe or PartsParty.exe), is effectively enabled (the key was readable and there is no stamp, or a stamp of the enabled
    /// kind; a stamp of the disabled kind, an unknown format or a failed read is "not enabled"), and the new name has no Run value.
    /// </summary>
    public static RunSwitchOffer Offer(IRunStore store, string newExePath, Func<string, bool>? fileExists = null)
    {
        Snapshot snapshot;
        try
        {
            snapshot = Snapshot.Read(store);
        }
        catch (Exception)
        {
            return new RunSwitchOffer(false, RunSwitchReason.Unreadable);
        }

        return Evaluate(snapshot, newExePath, fileExists ?? File.Exists);
    }

    public static RunSwitchResult Switch(IRunStore store, string journalPath, string newExePath, Func<string, bool>? fileExists = null) =>
        Switch(store, new FileRunSwitchJournal(journalPath), newExePath, fileExists);

    /// <summary>Moves the sign-in start to <paramref name="newExePath"/> (see the class text for the steps). <paramref name="lockName"/> and <paramref name="wait"/> are for tests.</summary>
    public static RunSwitchResult Switch(IRunStore store, IRunSwitchJournal journal, string newExePath, Func<string, bool>? fileExists = null, string? lockName = null, TimeSpan? wait = null)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(newExePath) || !Path.IsPathFullyQualified(newExePath))
            {
                return new RunSwitchResult(RunSwitchStatus.NotOffered, nameof(RunSwitchReason.InvalidPath));
            }

            full = Path.GetFullPath(newExePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RunSwitchResult(RunSwitchStatus.NotOffered, nameof(RunSwitchReason.InvalidPath));
        }

        return Locked(lockName, wait, () => SwitchLocked(store, journal, full, fileExists ?? File.Exists));
    }

    public static RunSwitchResult Recover(IRunStore store, string journalPath, Func<string, bool>? fileExists = null) =>
        Recover(store, new FileRunSwitchJournal(journalPath), fileExists: fileExists);

    /// <summary>
    /// Run at a real start. No journal (the read says file or folder not found, and nothing else does: any other error is <see cref="RunSwitchStatus.RecoveryFailed"/> with nothing written and the journal kept):
    /// nothing. A committed journal: removed. Otherwise the four values are compared with the journal: when each is the value it had before or a value the switch writes or deletes, the values are put
    /// back as they were (new entry first, see the class text) and the journal removed; when one is something else (the user changed it in Windows) nothing is touched and the journal stays
    /// (<see cref="RunSwitchStatus.NeedsUserCheck"/>). The rule is per value, so a crash in the middle of an earlier recovery is recovered too.
    /// One exception to "put back": when the old Run pointed at a file that is gone now (the old app was uninstalled) and the new exe is still there, nothing is put back; the switch is finished
    /// instead (new Run and enabled stamp in place, old Run and stamp gone) and the journal removed. If the new exe is gone as well, the values are put back as they were.
    /// </summary>
    public static RunSwitchResult Recover(IRunStore store, IRunSwitchJournal journal, string? lockName = null, TimeSpan? wait = null, Func<string, bool>? fileExists = null) =>
        Locked(lockName, wait, () => RecoverLocked(store, journal, fileExists ?? File.Exists));

    private static RunSwitchResult Locked(string? lockName, TimeSpan? wait, Func<RunSwitchResult> work)
    {
        RunSwitchLock? held;
        try
        {
            held = RunSwitchLock.TryAcquire(lockName ?? RunSwitchLock.CurrentName(), wait ?? RunSwitchLock.DefaultWait);
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.Busy, "lock: " + ex.GetType().Name);
        }

        if (held is null)
        {
            return new RunSwitchResult(RunSwitchStatus.Busy, "another change is running");
        }

        using (held)
        {
            return work();
        }
    }

    private static RunSwitchResult SwitchLocked(IRunStore store, IRunSwitchJournal journal, string full, Func<string, bool> exists)
    {
        // "no journal" is only what a failed READ says (not found); a journal that exists but cannot be opened must not look absent
        var journalThere = false;
        try
        {
            journal.Read();
            journalThere = true;
        }
        catch (Exception ex) when (IsAbsent(ex))
        {
            // no journal: the normal case
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RolledBack, "journal: " + ex.GetType().Name);
        }

        if (journalThere)
        {
            return new RunSwitchResult(RunSwitchStatus.NeedsUserCheck, "a journal of an earlier switch is still there");
        }

        Snapshot before;
        try
        {
            before = Snapshot.Read(store);
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RolledBack, "read: " + ex.GetType().Name);
        }

        var offer = Evaluate(before, full, exists);
        if (!offer.Ask)
        {
            return new RunSwitchResult(RunSwitchStatus.NotOffered, offer.Reason.ToString());
        }

        var command = PartyLogon.ExpectedValue(full);
        var disabled = StartupApprovedCodec.EncodeDisabled(DateTime.UtcNow.ToFileTimeUtc());
        var dto = new JournalDto
        {
            Phase = Prepared,
            NewExe = full,
            NewCommand = command,
            DisabledStamp = Convert.ToHexString(disabled),
            OldRun = ValueDto.Of(before.OldRun),
            NewRun = ValueDto.Of(before.NewRun),
            OldApproval = ValueDto.Of(before.OldApproval),
            NewApproval = ValueDto.Of(before.NewApproval),
        };

        try
        {
            journal.Write(JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RolledBack, "journal write: " + ex.GetType().Name);
        }

        try
        {
            store.WriteApproval(NewName, disabled);
            store.WriteRun(NewName, command);
            EnableNew(store, existedBefore: before.NewApproval is not null);

            // the new entry is fully enabled: only now does the old one go (a crash before this line leaves the old start working, after it the new one)
            store.DeleteRun(OldName);
            store.DeleteApproval(OldName);

            Verify(Snapshot.Read(store), command);
            store.Flush();
            dto.Phase = Committed;
            journal.Write(JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            var error = RestoreAll(store, dto);
            if (error is not null)
            {
                return new RunSwitchResult(RunSwitchStatus.RestoreFailed, error + " (after " + ex.GetType().Name + ")");
            }

            try
            {
                journal.Delete();
            }
            catch (Exception)
            {
                // the values are back and match "before": the next Recover removes the journal
                return new RunSwitchResult(RunSwitchStatus.RolledBack, ex.GetType().Name + "; journal left");
            }

            return new RunSwitchResult(RunSwitchStatus.RolledBack, ex.GetType().Name);
        }

        try
        {
            journal.Delete();
        }
        catch (Exception)
        {
            return new RunSwitchResult(RunSwitchStatus.Switched, "journal left (committed; the next start removes it)");
        }

        return new RunSwitchResult(RunSwitchStatus.Switched, string.Empty);
    }

    private static RunSwitchResult RecoverLocked(IRunStore store, IRunSwitchJournal journal, Func<string, bool> exists)
    {
        string text;
        try
        {
            text = journal.Read();
        }
        catch (Exception ex) when (IsAbsent(ex))
        {
            return new RunSwitchResult(RunSwitchStatus.NothingToDo, "no journal");
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RecoveryFailed, "journal: " + ex.GetType().Name);
        }

        JournalDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<JournalDto>(text, JsonOptions);
        }
        catch (JsonException)
        {
            dto = null;
        }

        if (dto is null || dto.Version != 1 || dto.OldRun is null || dto.NewRun is null || dto.OldApproval is null || dto.NewApproval is null || dto.DisabledStamp is null || dto.NewCommand is null)
        {
            return new RunSwitchResult(RunSwitchStatus.NeedsUserCheck, "the journal cannot be read");
        }

        if (dto.Phase == Committed)
        {
            try
            {
                journal.Delete();
            }
            catch (Exception ex)
            {
                return new RunSwitchResult(RunSwitchStatus.RecoveryFailed, "journal delete: " + ex.GetType().Name);
            }

            return new RunSwitchResult(RunSwitchStatus.NothingToDo, "committed journal removed");
        }

        Snapshot now;
        try
        {
            now = Snapshot.Read(store);
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RecoveryFailed, "read: " + ex.GetType().Name);
        }

        if (!IsKnownState(now, dto))
        {
            return new RunSwitchResult(RunSwitchStatus.NeedsUserCheck, "a value was changed meanwhile");
        }

        // the old app is uninstalled (its exe is gone) and the new exe is there: do not bring back a start that points at nothing; finish the switch instead
        var finish = OldExeIsGone(dto.OldRun!, exists) && exists(dto.NewExe);
        var error = finish ? FinishForward(store, dto) : RestoreAll(store, dto);
        if (error is not null)
        {
            return new RunSwitchResult(RunSwitchStatus.RecoveryFailed, error);
        }

        try
        {
            journal.Delete();
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RecoveryFailed, "journal delete: " + ex.GetType().Name);
        }

        return new RunSwitchResult(RunSwitchStatus.Recovered, finish ? "the old exe is gone: the switch was finished, not put back" : string.Empty);
    }

    /// <summary>Only "not found" means there is no journal; access denied, a sharing violation or a disk error mean "could not tell".</summary>
    private static bool IsAbsent(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException;

    /// <summary>The new stamp as the end of a switch has it: enabled, or not there at all when it was not there before.</summary>
    private static void EnableNew(IRunStore store, bool existedBefore)
    {
        if (existedBefore)
        {
            store.WriteApproval(NewName, StartupApprovedCodec.EncodeEnabled());
        }
        else
        {
            store.DeleteApproval(NewName);
        }
    }

    /// <summary>
    /// The way forward for a recovery whose old exe is gone: new Run, then the new stamp enabled, then the old Run and its stamp removed (never a moment with no entry), flush, read back. Null when done.
    /// </summary>
    private static string? FinishForward(IRunStore store, JournalDto dto)
    {
        try
        {
            store.WriteRun(NewName, dto.NewCommand!);
            EnableNew(store, existedBefore: dto.NewApproval!.Exists);
            store.DeleteRun(OldName);
            store.DeleteApproval(OldName);
            store.Flush();
            Verify(Snapshot.Read(store), dto.NewCommand!);
            return null;
        }
        catch (Exception ex)
        {
            return "finish: " + ex.GetType().Name;
        }
    }

    /// <summary>True when the journal's old Run named a file (we can tell its path) that does not exist now.</summary>
    private static bool OldExeIsGone(ValueDto oldRun, Func<string, bool> exists)
    {
        if (!oldRun.Exists || oldRun.Text is null || (RegistryValueKind)oldRun.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            return false;
        }

        var path = ExePathOf(new RunValue((RegistryValueKind)oldRun.Kind, oldRun.Text));
        return path.Length > 0 && !exists(path);
    }

    private static RunSwitchOffer Evaluate(Snapshot s, string newExePath, Func<string, bool> exists)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(newExePath) || !Path.IsPathFullyQualified(newExePath))
            {
                return No(RunSwitchReason.InvalidPath);
            }

            full = Path.GetFullPath(newExePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return No(RunSwitchReason.InvalidPath);
        }

        if (PartyLogon.IsTooLong(full))
        {
            return No(RunSwitchReason.TooLong);
        }

        if (s.OldRun is null)
        {
            return No(RunSwitchReason.OldNotThere);
        }

        if (!PointsAtOldExe(s.OldRun, exists))
        {
            return No(RunSwitchReason.NotOldExe);
        }

        if (s.NewRun is not null)
        {
            return No(RunSwitchReason.NewExists);
        }

        if (s.NewApproval is { Kind: not RegistryValueKind.Binary })
        {
            return No(RunSwitchReason.Unrestorable);
        }

        var enabled = s.OldApproval is null || StartupApprovedCodec.Decode(s.OldApproval.Kind, s.OldApproval.Bytes) == ApprovalState.Enabled;
        return enabled ? new RunSwitchOffer(true, RunSwitchReason.None) : No(RunSwitchReason.NotEnabled);
    }

    private static RunSwitchOffer No(RunSwitchReason reason) => new(false, reason);

    private static bool PointsAtOldExe(RunValue run, Func<string, bool> exists)
    {
        var path = ExePathOf(run);
        if (path.Length == 0)
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return OldExeNames.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)) && exists(path);
    }

    /// <summary>The path a Run value starts (quoted, or the whole text when it is not quoted; environment variables expanded for REG_EXPAND_SZ). Empty when it is not a string or names no path.</summary>
    private static string ExePathOf(RunValue run)
    {
        if (run.Text is null || run.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            return string.Empty;
        }

        var text = run.Kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(run.Text) : run.Text;
        var trimmed = text.Trim();
        return trimmed.StartsWith('"') ? (PartyLogon.TryParse(trimmed, out var quoted, out _) ? quoted : string.Empty) : trimmed;
    }

    private static void Verify(Snapshot s, string command)
    {
        var ok = s.OldRun is null
            && s.OldApproval is null
            && s.NewRun is { Kind: RegistryValueKind.String } run && run.Text == command
            && (s.NewApproval is null || StartupApprovedCodec.Decode(s.NewApproval.Kind, s.NewApproval.Bytes) == ApprovalState.Enabled);
        if (!ok)
        {
            throw new InvalidOperationException("read back does not show the switched state");
        }
    }

    /// <summary>Each of the four values may be what it was, or what the switch writes or deletes. Anything else was changed by somebody.</summary>
    private static bool IsKnownState(Snapshot now, JournalDto dto)
    {
        var disabled = Convert.FromHexString(dto.DisabledStamp!);
        var enabled = StartupApprovedCodec.EncodeEnabled();
        return (now.OldRun is null || dto.OldRun!.Matches(now.OldRun))
            && (now.NewRun is null || dto.NewRun!.Matches(now.NewRun) || (now.NewRun.Kind == RegistryValueKind.String && now.NewRun.Text == dto.NewCommand))
            && (now.OldApproval is null || dto.OldApproval!.Matches(now.OldApproval))
            && (now.NewApproval is null
                || dto.NewApproval!.Matches(now.NewApproval)
                || (now.NewApproval.Kind == RegistryValueKind.Binary && (now.NewApproval.Bytes.AsSpan().SequenceEqual(disabled) || now.NewApproval.Bytes.AsSpan().SequenceEqual(enabled))));
    }

    /// <summary>
    /// Puts the four values back as the journal has them, the NEW entry first (its Run, then its stamp, so it cannot start any more) and only then the old one (its Run, then its stamp): a restore that
    /// stops halfway never leaves both entries enabled. When the new Run could not be taken away, the old entry is not touched at all (two starts are worse than one). A value failing does not stop the
    /// others of its entry. Flushes, reads the four back and compares. Null when all four are as before, else what is wrong.
    /// </summary>
    private static string? RestoreAll(IRunStore store, JournalDto dto)
    {
        var errors = new List<string>();
        Try(errors, "new run", () => PutRun(store, NewName, dto.NewRun!));
        var newRunBack = errors.Count == 0;
        Try(errors, "new stamp", () => PutApproval(store, NewName, dto.NewApproval!));
        if (newRunBack)
        {
            Try(errors, "old run", () => PutRun(store, OldName, dto.OldRun!));
            Try(errors, "old stamp", () => PutApproval(store, OldName, dto.OldApproval!));
        }

        Try(errors, "flush", store.Flush);

        try
        {
            var s = Snapshot.Read(store);
            if (!dto.OldRun!.Matches(s.OldRun) || !dto.NewRun!.Matches(s.NewRun) || !dto.OldApproval!.Matches(s.OldApproval) || !dto.NewApproval!.Matches(s.NewApproval))
            {
                errors.Add("read back differs from the journal");
            }
        }
        catch (Exception ex)
        {
            errors.Add("read back: " + ex.GetType().Name);
        }

        return errors.Count == 0 ? null : "restore: " + string.Join(", ", errors);
    }

    private static void Try(List<string> errors, string what, Action put)
    {
        try
        {
            put();
        }
        catch (Exception ex)
        {
            errors.Add(what + " " + ex.GetType().Name);
        }
    }

    private static void PutRun(IRunStore store, string name, ValueDto v)
    {
        if (v.Exists)
        {
            store.WriteRun(name, v.Text ?? throw new InvalidOperationException("a Run value of another type"), (RegistryValueKind)v.Kind);
        }
        else
        {
            store.DeleteRun(name);
        }
    }

    private static void PutApproval(IRunStore store, string name, ValueDto v)
    {
        if (v.Exists)
        {
            store.WriteApproval(name, Convert.FromHexString(v.Hex ?? string.Empty), (RegistryValueKind)v.Kind);
        }
        else
        {
            store.DeleteApproval(name);
        }
    }

    /// <summary>The four values as read in one go (the order is also the order of the calls: old run, new run, old stamp, new stamp).</summary>
    private sealed record Snapshot(RunValue? OldRun, RunValue? NewRun, ApprovalValue? OldApproval, ApprovalValue? NewApproval)
    {
        public static Snapshot Read(IRunStore store)
        {
            var oldRun = store.ReadRun(OldName);
            var newRun = store.ReadRun(NewName);
            var oldApproval = store.ReadApproval(OldName);
            var newApproval = store.ReadApproval(NewName);
            return new Snapshot(oldRun, newRun, oldApproval, newApproval);
        }
    }

    /// <summary>The journal file, one JSON object. Each value: whether it exists, its registry type (number) and its raw content (the text of a Run value, the hex of a stamp).</summary>
    internal sealed class JournalDto
    {
        public int Version { get; set; } = 1;

        public string Phase { get; set; } = Prepared;

        public string NewExe { get; set; } = string.Empty;

        /// <summary>The text the switch writes as the new Run value.</summary>
        public string? NewCommand { get; set; }

        /// <summary>The hex of the disabled stamp that the switch writes first (so a recovery knows it).</summary>
        public string? DisabledStamp { get; set; }

        public ValueDto? OldRun { get; set; }

        public ValueDto? NewRun { get; set; }

        public ValueDto? OldApproval { get; set; }

        public ValueDto? NewApproval { get; set; }
    }

    internal sealed class ValueDto
    {
        public bool Exists { get; set; }

        public int Kind { get; set; }

        public string? Text { get; set; }

        public string? Hex { get; set; }

        public static ValueDto Of(RunValue? v) => v is null ? new ValueDto() : new ValueDto { Exists = true, Kind = (int)v.Kind, Text = v.Text };

        public static ValueDto Of(ApprovalValue? v) => v is null ? new ValueDto() : new ValueDto { Exists = true, Kind = (int)v.Kind, Hex = Convert.ToHexString(v.Bytes) };

        public bool Matches(RunValue? v) => v is null
            ? !Exists
            : Exists && Kind == (int)v.Kind && string.Equals(Text, v.Text, StringComparison.Ordinal);

        public bool Matches(ApprovalValue? v) => v is null
            ? !Exists
            : Exists && Kind == (int)v.Kind && string.Equals(Hex ?? string.Empty, Convert.ToHexString(v.Bytes), StringComparison.OrdinalIgnoreCase);
    }
}
