// Provenance: what is compared and when (strong and weak keys, twins, the withheld record after a clock reversal, the 200-record trim,
// the compare of each counter) follows the owner's pl-sensors BaselineStore (src/PartsLife.Checks/Storage/Baseline.cs), MIT, same owner.

using System.Numerics;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Storage;

/// <summary>What a run was told at its start: its acquisition number (DESIGN-A §14.5), or that it could not get one.</summary>
/// <param name="Acq">The per-user sequence number this run took. Bigger means later; a record of a bigger one makes this run's sampling the older one.</param>
/// <param name="Failed">The lock or the sequence file was not available: nothing may be compared or recorded by this run.</param>
public readonly record struct BaselineRunTicket(long Acq, bool Failed);

/// <summary>Whether a disk's counters were compared with a previous record.</summary>
public enum BaselineDiskState
{
    /// <summary>The disk's health could not be read (or has no counters): there are no source rows, so no ".delta" rows either.</summary>
    NoCounters,

    /// <summary>The whole disk could not be compared; <see cref="BaselineDiskOutcome.Code"/> says why. Every ".delta" of it is null with that code.</summary>
    NotComparable,

    /// <summary>The disk was compared. A counter can still be not comparable (<see cref="BaselineCounterOutcome.Code"/> = baseline_missing_counter).</summary>
    Compared,
}

/// <summary>What the baseline says about one counter of one disk: everything the ".delta" row needs (DESIGN-A §3.3, §3.5-5 to 7, §14.5).</summary>
/// <param name="CounterId">The ID of the counter's row, "storage.nvme.media_errors" and so on. The ".delta" row is <see cref="DeltaId"/>.</param>
/// <param name="Code">Null when compared. Else the reason: the disk-wide code of a not comparable disk, or baseline_missing_counter for this row alone.</param>
/// <param name="SinceUtc">When the "before" was recorded (UTC, whole milliseconds). Compared only.</param>
/// <param name="NewBits">Critical warning only, compared only: the bits that are set now and were not before (<c>after &amp; ~before</c>). 0 when none.</param>
public sealed record BaselineCounterOutcome(string CounterId, CheckEvidenceCode? Code, ulong? Before, ulong? After, DateTimeOffset? SinceUtc, int? NewBits)
{
    public bool IsCompared => Code is null;

    public string DeltaId => CheckItemRegistry.DeltaId(CounterId);

    /// <summary>After minus before. Compared only (0 otherwise).</summary>
    public BigInteger Change => IsCompared ? new BigInteger(After!.Value) - new BigInteger(Before!.Value) : BigInteger.Zero;

    /// <summary>"Grew" as v0.7.4's <c>CounterDelta.Grew</c> and §14.5 define it: after &gt; before, and for the critical warning a newly set bit.</summary>
    public bool Grew => IsCompared && (NewBits is { } bits ? bits != 0 : After > Before);
}

/// <summary>What the baseline says about one drive of the inspection.</summary>
/// <param name="DriveIndex">The position in <see cref="StorageInspection.Drives"/>.</param>
/// <param name="DiskId">"disk-N": the stable number of a strong key (<c>disk-ordinals.json</c>), or the lowest number not used in this result.</param>
/// <param name="Code">The disk-wide reason of a <see cref="BaselineDiskState.NotComparable"/> disk. Null otherwise.</param>
/// <param name="Counters">One outcome per counter that has a ".delta" row and was read in this run, in the order of the item registry.</param>
public sealed record BaselineDiskOutcome(int DriveIndex, string DiskId, BaselineDiskState State, CheckEvidenceCode? Code, IReadOnlyList<BaselineCounterOutcome> Counters);

/// <summary>What <see cref="BaselineEngine.Complete"/> did for one inspection.</summary>
/// <param name="Disks">One entry per drive, in the order of <see cref="StorageInspection.Drives"/>.</param>
/// <param name="ReadFailed">
/// A baseline file could not be read or written in this run (a disk got baseline_read_failed), the set of twins could not be trusted or
/// stored, or the migration stopped the comparison: the writer of the storage result adds the note <see cref="CheckNotes.StorageBaselineReadFailed"/>.
/// </param>
public sealed record BaselineRunResult(IReadOnlyList<BaselineDiskOutcome> Disks, BaselineMigrationReport Migration, bool ReadFailed);

/// <summary>
/// The baseline of the storage check (DESIGN-A §5, §14.5): remembers the counters of every strong-keyed disk between runs and says,
/// for each drive of one inspection, whether the counters can be compared with the last record and which counters grew. The engine only
/// decides and records; judging an absolute row and building the result is the caller's.
///
/// Usage: <see cref="BeginRun"/> at the start of a run (before sampling), <see cref="Complete"/> when the inspection is done.
/// Both are synchronous, take a short cross-process lock and never hold it while sampling. Nothing here throws for a missing, damaged
/// or locked file: it comes back as a reason (<c>baseline_read_failed</c> and the others).
/// </summary>
public sealed class BaselineEngine
{
    /// <summary>How long a run waits for the lock, in all, before it gives up (<c>baseline_read_failed</c>). The lock is held for a few file reads and writes.</summary>
    internal static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(15);

    private readonly BaselineStore _store;
    private readonly IBaselineLock _lock;
    private readonly ICheckHistoryFileReader _legacyReader;
    private readonly string? _legacyDirectory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lockTimeout;

    /// <param name="root">The folder that holds <c>baseline\</c>. The caller decides it; nothing here looks at the user's profile.</param>
    /// <param name="legacyDirectory">The v0.7.4 folder (<c>%APPDATA%\PartsLife\checks</c>), opened read-only, or null for no import.</param>
    public BaselineEngine(string root, string? legacyDirectory = null)
        : this(root, legacyDirectory, PhysicalBaselineFileSystem.Instance, PhysicalHistoryFileReader.Instance,
            new FileBaselineLock(Path.Combine(root, "baseline")), TimeProvider.System, null)
    {
    }

    internal BaselineEngine(
        string root,
        string? legacyDirectory,
        IBaselineFileSystem files,
        ICheckHistoryFileReader legacyReader,
        IBaselineLock baselineLock,
        TimeProvider time,
        Action<TimeSpan>? sleep,
        TimeSpan? lockTimeout = null)
    {
        _time = time;
        _store = new BaselineStore(Path.Combine(root, "baseline"), files, sleep, time);
        _legacyDirectory = legacyDirectory;
        _legacyReader = legacyReader;
        _lock = baselineLock;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>
    /// Takes the next acquisition number (DESIGN-A §14.5): the lock is taken, the per-user sequence is advanced and stored, the lock is
    /// released. Call this before sampling starts. When it cannot be done the ticket is <see cref="BaselineRunTicket.Failed"/> and
    /// <see cref="Complete"/> compares and records nothing. Like <see cref="Complete"/> it may wait for the lock: not for the UI thread.
    /// </summary>
    public BaselineRunTicket BeginRun()
    {
        using var held = _lock.TryAcquire(_lockTimeout);
        if (held is null)
        {
            return new BaselineRunTicket(0, Failed: true);
        }

        return _store.TryNextSequence(out var seq) ? new BaselineRunTicket(seq, Failed: false) : new BaselineRunTicket(0, Failed: true);
    }

    /// <summary>
    /// Compares and records the drives of one finished inspection. The lock is taken for this call as a whole: the files are read
    /// again under it, so a run that started later and finished first is noticed (<c>baseline_concurrent</c>).
    /// </summary>
    /// <remarks>
    /// Synchronous, with file reads and writes, and it may <b>block for up to the lock deadline</b> (15 seconds) while another run holds
    /// the lock: it must not be called on the UI thread. When the lock cannot be taken in that time nothing is compared or recorded and
    /// every drive comes back as <c>baseline_read_failed</c>.
    /// </remarks>
    public BaselineRunResult Complete(BaselineRunTicket ticket, StorageInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        IDisposable? held = ticket.Failed ? null : _lock.TryAcquire(_lockTimeout);
        try
        {
            return new Run(this, ticket, held is not null, inspection).Execute();
        }
        finally
        {
            held?.Dispose();
        }
    }

    private sealed class Work(int index, StorageInspectionDrive drive)
    {
        public int Index { get; } = index;

        public StorageInspectionDrive Drive { get; } = drive;

        /// <summary>The key without the location. Null for a slot with no descriptor (nothing to key).</summary>
        public DiskKey? Plain { get; init; }

        public CounterSet Counters { get; init; } = new();

        public int Ordinal { get; set; }
    }

    private sealed class Run
    {
        private readonly BaselineEngine _engine;
        private readonly BaselineStore _store;
        private readonly BaselineRunTicket _ticket;
        private readonly bool _canWrite;
        private readonly StorageInspection _inspection;
        private readonly DateTimeOffset _now;
        private readonly Work[] _work;
        private readonly Dictionary<string, List<int>> _strong = new(StringComparer.Ordinal);
        private readonly HashSet<string> _twins = new(StringComparer.Ordinal);
        private BaselineMigrationReport _migration = BaselineMigrationReport.None(BaselineMigrationStatus.NotNeeded);
        private bool _stateBlocked;
        private bool _ignoreImported;
        private bool _twinsWriteFailed;
        private bool _readFailed;

        public Run(BaselineEngine engine, BaselineRunTicket ticket, bool canWrite, StorageInspection inspection)
        {
            _engine = engine;
            _store = engine._store;
            _ticket = ticket;
            _canWrite = canWrite;
            _inspection = inspection;
            _now = engine._time.GetUtcNow();
            _work = inspection.Drives.Select(static (drive, index) => Describe(index, drive)).ToArray();
        }

        /// <summary>Recording is allowed in this run: the lock is held, the sets of twins are trusted and the new twins are on the disk.</summary>
        private bool RecordingAllowed => _canWrite && !_stateBlocked && !_twinsWriteFailed;

        private static Work Describe(int index, StorageInspectionDrive drive)
        {
            if (drive.Recognition != StorageRecognition.Recognized)
            {
                return new Work(index, drive); // no descriptor: no key, no counters, no part in the count of twins
            }

            return new Work(index, drive)
            {
                Plain = DiskKey.Of(drive.SerialNumber, drive.UniqueId, drive.Model, drive.SizeBytes, drive.BusType, drive.Location),
                Counters = BaselineCounters.From(drive),
            };
        }

        public BaselineRunResult Execute()
        {
            if (_canWrite && _engine._legacyDirectory is not null)
            {
                _migration = BaselineMigration.Run(_store, _engine._legacyReader, _engine._legacyDirectory, _now);
                _stateBlocked = _migration.StopsComparison;
            }

            if (_canWrite)
            {
                // An abandoned import (DESIGN-A §5.3-2) means the old twins could not be trusted: whatever an interrupted earlier run
                // had already copied from the old side is not used as a base. A marker that cannot be read is treated the same way
                // (when an old folder is set, the migration has stopped the comparison already).
                if (_store.ReadMarker() is BaselineMarker.Abandoned or BaselineMarker.Unreadable)
                {
                    _ignoreImported = true;
                }
            }

            LoadTwins();
            FindNewTwins();
            AssignOrdinals();

            var outcomes = new BaselineDiskOutcome[_work.Length];
            foreach (var work in _work)
            {
                outcomes[work.Index] = Decide(work);
            }

            return new BaselineRunResult(outcomes, _migration, _readFailed || _stateBlocked || _twinsWriteFailed);
        }

        // ---- twins ----

        private void LoadTwins()
        {
            foreach (var name in new[] { BaselineStore.TwinsFile, BaselineStore.ImportedTwinsFile })
            {
                var set = _store.ReadKeySet(name);
                switch (set.Status)
                {
                    case BaselineReadStatus.Ok:
                        _twins.UnionWith(set.Value!);
                        break;
                    case BaselineReadStatus.Missing:
                        break;
                    default:
                        // A set that cannot be trusted: a drive that is a past twin could be compared with its twin's record.
                        _stateBlocked = true;
                        break;
                }
            }
        }

        /// <summary>Strong keys seen on more than one drive now are twins from now on: stored before anything else is compared or recorded.</summary>
        private void FindNewTwins()
        {
            foreach (var work in _work)
            {
                if (work.Plain is { Strong: true } plain)
                {
                    if (!_strong.TryGetValue(plain.Key, out var list))
                    {
                        _strong.Add(plain.Key, list = []);
                    }

                    list.Add(work.Index);
                }
            }

            var found = _strong.Where(static pair => pair.Value.Count > 1).Select(static pair => pair.Key).Where(key => !_twins.Contains(key)).ToList();
            if (found.Count == 0)
            {
                return;
            }

            var own = _store.ReadKeySet(BaselineStore.TwinsFile);
            if (!_canWrite || own.Status is BaselineReadStatus.Corrupt or BaselineReadStatus.Unreadable)
            {
                _twinsWriteFailed = true; // the damaged file is not overwritten: it holds the twins this run cannot see
            }
            else
            {
                var all = new HashSet<string>(own.Value ?? [], StringComparer.Ordinal);
                all.UnionWith(found);
                try
                {
                    _store.WriteReplace(BaselineStore.TwinsFile, BaselineJson.WriteKeyList(all));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _twinsWriteFailed = true;
                }
            }

            _twins.UnionWith(found);
        }

        // ---- the numbers of the disks ----

        private void AssignOrdinals()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var persistable = _canWrite;
            var stored = _store.ReadOrdinals();
            switch (stored.Status)
            {
                case BaselineReadStatus.Ok:
                    map = stored.Value!;
                    break;
                case BaselineReadStatus.Missing:
                    break;
                case BaselineReadStatus.Corrupt:
                    // Kept for a look; the numbering starts again. When it cannot be moved aside it is not written over either:
                    // the numbers of this run are temporary, as for a write that failed.
                    if (_canWrite && !_store.Quarantine(BaselineStore.OrdinalsFile, _now))
                    {
                        persistable = false;
                    }

                    break;
                default:
                    persistable = false;
                    break;
            }

            // A number that outlives the run belongs to a key that is one drive in this result and was never a twin.
            var eligible = _work
                .Where(w => w.Plain is { Strong: true } plain && _strong[plain.Key].Count == 1 && !_twins.Contains(plain.Key))
                .ToList();
            var used = new HashSet<int>();
            foreach (var work in eligible)
            {
                if (map.TryGetValue(BaselineNames.KeyHash(work.Plain!.Key), out var number) && used.Add(number))
                {
                    work.Ordinal = number;
                }
            }

            var fresh = eligible.Where(static w => w.Ordinal == 0).ToList();
            if (fresh.Count > 0 && persistable)
            {
                var next = map.Count == 0 ? 1 : map.Values.Max() + 1;
                var grown = new Dictionary<string, int>(map, StringComparer.Ordinal);
                var given = new List<(Work Work, int Number)>();
                foreach (var work in fresh)
                {
                    if (next > 99_999_999)
                    {
                        break;
                    }

                    grown[BaselineNames.KeyHash(work.Plain!.Key)] = next;
                    given.Add((work, next));
                    next++;
                }

                if (given.Count > 0)
                {
                    try
                    {
                        _store.WriteReplace(BaselineStore.OrdinalsFile, BaselineJson.WriteOrdinals(grown));
                        foreach (var (work, number) in given)
                        {
                            work.Ordinal = number;
                            used.Add(number);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Not stored: these drives get temporary numbers below, like a weak key.
                    }
                }
            }

            // Everything else: the lowest number nobody in this result has. Not stored, never joined to another run.
            var cursor = 1;
            foreach (var work in _work.Where(static w => w.Ordinal == 0))
            {
                while (used.Contains(cursor))
                {
                    cursor++;
                }

                work.Ordinal = cursor;
                used.Add(cursor);
            }
        }

        // ---- one drive ----

        private BaselineDiskOutcome Decide(Work work)
        {
            if (work.Counters.IsEmpty)
            {
                return Outcome(work, BaselineDiskState.NoCounters, null);
            }

            var drive = work.Drive;
            if (drive.Confirmation == StorageIdentityConfirmation.Mismatch)
            {
                return NotComparable(work, CheckEvidenceCode.BaselineDeviceChanged);
            }

            if (drive.Confirmation != StorageIdentityConfirmation.Confirmed)
            {
                return NotComparable(work, CheckEvidenceCode.BaselineNoIdentity);
            }

            var plain = work.Plain!;
            if (!plain.Strong)
            {
                // A weak key cannot be told from a drive that could not be read: with an unreadable slot the doubt is open (§5.2).
                return NotComparable(work, _inspection.InventoryComplete ? CheckEvidenceCode.BaselineNoIdentity : CheckEvidenceCode.BaselineInventoryIncomplete);
            }

            if (_twins.Contains(plain.Key))
            {
                return Twin(work, plain);
            }

            return Single(work, plain);
        }

        /// <summary>A twin is never compared. If every drive of the group has its own location the value is still recorded, under the key with the location.</summary>
        private BaselineDiskOutcome Twin(Work work, DiskKey plain)
        {
            var group = _strong[plain.Key];
            var locations = group.Select(i => _work[i].Drive.Location).ToList();
            var separable = locations.All(static l => l.Length > 0) && locations.Distinct(StringComparer.Ordinal).Count() == locations.Count;
            if (separable && RecordingAllowed)
            {
                RecordLocated(work);
            }

            return NotComparable(work, separable && !_inspection.InventoryComplete ? CheckEvidenceCode.BaselineInventoryIncomplete : CheckEvidenceCode.BaselineDuplicate);
        }

        private void RecordLocated(Work work)
        {
            var drive = work.Drive;
            var key = DiskKey.Of(drive.SerialNumber, drive.UniqueId, drive.Model, drive.SizeBytes, drive.BusType, drive.Location, withLocation: true);
            var file = BaselineNames.FileName(key);
            if (_migration.PendingHashes.Contains(BaselineNames.KeyHash(key.Key)))
            {
                return;
            }

            if (!TryLoad(file, key, drive, out var history) || history.Entries.Any(e => e.Acq > _ticket.Acq))
            {
                return;
            }

            history.Entries.Add(NewEntry(work, withheld: false));
            Trim(history, keep: null);
            TrySave(file, history);
        }

        /// <summary>One strong key on one drive that has never been a twin: the case the baseline exists for.</summary>
        private BaselineDiskOutcome Single(Work work, DiskKey plain)
        {
            if (!RecordingAllowed)
            {
                return Failed(work);
            }

            var drive = work.Drive;
            var file = BaselineNames.FileName(plain);
            if (_migration.PendingHashes.Contains(BaselineNames.KeyHash(plain.Key)) || !TryLoad(file, plain, drive, out var history))
            {
                return Failed(work);
            }

            // Re-read under the lock: a run that began later has already recorded this disk, so this sample is the older one.
            if (history.Entries.Any(e => e.Acq > _ticket.Acq))
            {
                return NotComparable(work, CheckEvidenceCode.BaselineConcurrent);
            }

            var at = CheckTime.TruncateToMilliseconds(drive.SampledAt);

            // The last record that may be a base. A record copied from the old folder is left out when that import was abandoned.
            var previous = history.Entries.LastOrDefault(e => !e.Withheld && !(_ignoreImported && e.Acq == 0));

            // A late sample (§14.5): acq is the order in which the runs began, not the order in which the drives were read, so the base can
            // have the smaller acq and still have been sampled later than this run's reading. Counters that went back against a base that is
            // later in time say that this sample is the older one, not that the disk was reset. A time that went back with no counter going
            // back is not this: it is the clock, below. A record from the old folder (acq 0) is not a run that was going on at the same
            // time, so it is left out: it cannot make a sample "late", and a clock that jumped forward must not stop the drive for good.
            if (previous is { Acq: > 0 } && previous.At > at && BaselineCounters.Check(previous, work.Counters, at) == BaselineCounters.Continuity.WentBackwards)
            {
                return NotComparable(work, CheckEvidenceCode.BaselineConcurrent);
            }

            BaselineDiskOutcome outcome;
            var withheld = false;
            if (previous is null)
            {
                outcome = NotComparable(work, IsLegacyRefused(work) ? CheckEvidenceCode.BaselineLegacyRefused : CheckEvidenceCode.BaselineFirst);
            }
            else
            {
                switch (BaselineCounters.Check(previous, work.Counters, at))
                {
                    case BaselineCounters.Continuity.WentBackwards:
                        outcome = NotComparable(work, CheckEvidenceCode.BaselineCounterReset);
                        break;
                    case BaselineCounters.Continuity.AheadOfClock:
                        outcome = NotComparable(work, CheckEvidenceCode.BaselineClock);
                        // The clock went back: how long ago the record was is unknown, so this record must not become the base (v0.7.4).
                        withheld = at < previous.At;
                        break;
                    default:
                        outcome = Compare(work, previous);
                        break;
                }
            }

            history.Model = drive.Model;
            history.Entries.Add(NewEntry(work, withheld));
            Trim(history, keep: withheld ? previous : null);
            return TrySave(file, history) ? outcome : Failed(work);
        }

        private BaselineEntry NewEntry(Work work, bool withheld) => new()
        {
            At = CheckTime.TruncateToMilliseconds(work.Drive.SampledAt),
            Counters = new SortedDictionary<string, ulong>(work.Counters.Values, StringComparer.Ordinal),
            Acq = _ticket.Acq,
            Withheld = withheld,
        };

        /// <summary>Oldest first, but never the record the next run compares with (this run's previous, when this run is withheld).</summary>
        private static void Trim(BaselineHistory history, BaselineEntry? keep)
        {
            while (history.Entries.Count > BaselineNames.MaxEntries)
            {
                history.Entries.RemoveAt(ReferenceEquals(history.Entries[0], keep) ? 1 : 0);
            }
        }

        /// <summary>The history of a key. A file that is not a history of that key is kept aside (renamed) and the history starts again; one that cannot be read now fails the load.</summary>
        private bool TryLoad(string file, DiskKey key, StorageInspectionDrive drive, out BaselineHistory history)
        {
            history = new BaselineHistory { Key = key.Key, Model = drive.Model };
            var read = _store.ReadHistory(file, key.Key);
            switch (read.Status)
            {
                case BaselineReadStatus.Ok:
                    history = read.Value!;
                    return true;
                case BaselineReadStatus.Missing:
                    return true;
                case BaselineReadStatus.Corrupt:
                    // Only when it has been moved aside is there nothing left to lose by writing a new history in its place. When it
                    // cannot be moved, it is a file that cannot be handled now: no compare and no write, this run (baseline_read_failed).
                    return _store.Quarantine(file, _now);
                default:
                    return false;
            }
        }

        private bool TrySave(string file, BaselineHistory history)
        {
            try
            {
                _store.WriteReplace(file, BaselineJson.WriteHistory(history));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// v1 (the file named after the serial alone) is never carried over (§5.3-3). A first run of a disk whose serial has such a file,
        /// whose key and model are the file's, is reported as refused rather than as new, under the conditions v0.7.4 used to pick the file.
        /// The old file is only read.
        /// </summary>
        private bool IsLegacyRefused(Work work)
        {
            if (_engine._legacyDirectory is null)
            {
                return false;
            }

            var serial = work.Drive.SerialNumber;
            if (!DiskKey.UsableSerial(serial))
            {
                return false;
            }

            var safe = BaselineNames.SafeName(serial);
            if (_work.Count(o => BaselineNames.SafeName(o.Drive.SerialNumber) == safe) != 1)
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = _engine._legacyReader.ReadAllBytes(Path.Combine(_engine._legacyDirectory, BaselineNames.LegacyV1FileName(serial)), BaselineStore.MaxFileBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }

            return BaselineJson.TryParseHistory(bytes, out var old, zeroAcq: true)
                && old!.Key == serial
                && old.Model == work.Drive.Model
                && old.Entries.Count > 0;
        }

        // ---- outcomes ----

        private BaselineDiskOutcome Failed(Work work)
        {
            _readFailed = true;
            return NotComparable(work, CheckEvidenceCode.BaselineReadFailed);
        }

        private BaselineDiskOutcome NotComparable(Work work, CheckEvidenceCode code) => Outcome(work, BaselineDiskState.NotComparable, code);

        private BaselineDiskOutcome Outcome(Work work, BaselineDiskState state, CheckEvidenceCode? code)
        {
            var counters = state == BaselineDiskState.NoCounters
                ? []
                : BaselineCounters.DeltaCounters
                    .Where(work.Counters.Values.ContainsKey)
                    .Select(name => new BaselineCounterOutcome(BaselineCounters.ItemId(name), code, null, null, null, null))
                    .ToList();
            return new BaselineDiskOutcome(work.Index, "disk-" + work.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), state, code, counters);
        }

        private BaselineDiskOutcome Compare(Work work, BaselineEntry previous)
        {
            var since = CheckTime.TruncateToMilliseconds(previous.At);
            var counters = new List<BaselineCounterOutcome>();
            foreach (var name in BaselineCounters.DeltaCounters.Where(work.Counters.Values.ContainsKey))
            {
                var id = BaselineCounters.ItemId(name);
                if (!BaselineCounters.TryBoth(previous, work.Counters, name, out var before, out var after))
                {
                    // The earlier record had no such counter, or a clipped one; or this value does not fit in 64 bits: no delta for this row only.
                    // A current value of ulong.MaxValue or more is "inexact" (a raw number that was clipped, or one that is wider than the
                    // field): whether it grew cannot be told from a clipped number, and a delta made of it could be a false alarm or hide a
                    // real one. So it is "this counter is missing" for that row, never a number.
                    counters.Add(new BaselineCounterOutcome(id, CheckEvidenceCode.BaselineMissingCounter, null, null, null, null));
                    continue;
                }

                int? newBits = name == BaselineCounters.CriticalWarning ? (int)(after & ~before & 0xFF) : null;
                counters.Add(new BaselineCounterOutcome(id, null, before, after, since, newBits));
            }

            return new BaselineDiskOutcome(work.Index, "disk-" + work.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), BaselineDiskState.Compared, null, counters);
        }
    }
}
