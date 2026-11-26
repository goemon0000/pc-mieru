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
}

public sealed record RunSwitchOffer(bool Ask, RunSwitchReason Reason);

/// <summary>
/// The one-time move of the sign-in start from the old Parts Life Run value (<c>PartsLife</c>) to the new one (<see cref="PartyLogon.ValueName"/>), DESIGN-C 4.3. It touches four values:
/// Run <c>PartsLife</c>, Run <c>PCMieru.Party</c>, StartupApproved <c>PartsLife</c>, StartupApproved <c>PCMieru.Party</c>. Everything goes through an <see cref="IRunStore"/> (a test and a smoke run hand in a
/// <see cref="MemoryRunStore"/>) and every change runs under <see cref="RunSwitchLock"/>. A journal (JSON, written as temp file + rename) holds the four values as they were and the phase
/// (<c>prepared</c>, then <c>committed</c>); <see cref="Recover"/> reads it at the next start. Nothing here throws to its caller.
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

    public static RunSwitchResult Recover(IRunStore store, string journalPath) => Recover(store, new FileRunSwitchJournal(journalPath));

    /// <summary>
    /// Run at a real start. No journal: nothing. A committed journal: removed. Otherwise the four values are compared with the journal: when each is the value it had before or a value the switch writes
    /// or deletes, the values are put back as they were and the journal removed; when one is something else (the user changed it in Windows) nothing is touched and the journal stays
    /// (<see cref="RunSwitchStatus.NeedsUserCheck"/>). The rule is per value, so a crash in the middle of an earlier recovery is recovered too.
    /// </summary>
    public static RunSwitchResult Recover(IRunStore store, IRunSwitchJournal journal, string? lockName = null, TimeSpan? wait = null) =>
        Locked(lockName, wait, () => RecoverLocked(store, journal));

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
        try
        {
            if (journal.Exists())
            {
                return new RunSwitchResult(RunSwitchStatus.NeedsUserCheck, "a journal of an earlier switch is still there");
            }
        }
        catch (Exception ex)
        {
            return new RunSwitchResult(RunSwitchStatus.RolledBack, "journal: " + ex.GetType().Name);
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
            store.DeleteRun(OldName);
            store.DeleteApproval(OldName);
            if (before.NewApproval is null)
            {
                store.DeleteApproval(NewName);
            }
            else
            {
                store.WriteApproval(NewName, StartupApprovedCodec.EncodeEnabled());
            }

            Verify(Snapshot.Read(store), command);
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

    private static RunSwitchResult RecoverLocked(IRunStore store, IRunSwitchJournal journal)
    {
        string text;
        try
        {
            if (!journal.Exists())
            {
                return new RunSwitchResult(RunSwitchStatus.NothingToDo, "no journal");
            }

            text = journal.Read();
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

        var error = RestoreAll(store, dto);
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

        return new RunSwitchResult(RunSwitchStatus.Recovered, string.Empty);
    }

    private static RunSwitchOffer Evaluate(Snapshot s, string newExePath, Func<string, bool> exists)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newExePath) || !Path.IsPathFullyQualified(newExePath))
            {
                return No(RunSwitchReason.InvalidPath);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return No(RunSwitchReason.InvalidPath);
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
        if (run.Text is null || run.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            return false;
        }

        var text = run.Kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(run.Text) : run.Text;
        var trimmed = text.Trim();
        var path = trimmed.StartsWith('"') ? (PartyLogon.TryParse(trimmed, out var quoted, out _) ? quoted : string.Empty) : trimmed;
        if (path.Length == 0)
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return OldExeNames.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)) && exists(path);
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
    /// Puts the four values back as the journal has them (each on its own: one failing does not stop the others), reads them back and compares. Null when all four are as before, else what is wrong.
    /// </summary>
    private static string? RestoreAll(IRunStore store, JournalDto dto)
    {
        var errors = new List<string>();
        Try(errors, "old run", () => PutRun(store, OldName, dto.OldRun!));
        Try(errors, "old stamp", () => PutApproval(store, OldName, dto.OldApproval!));
        Try(errors, "new run", () => PutRun(store, NewName, dto.NewRun!));
        Try(errors, "new stamp", () => PutApproval(store, NewName, dto.NewApproval!));

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
