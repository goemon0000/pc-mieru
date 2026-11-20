using System.Globalization;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The per-disk rows of the storage check (DESIGN-A §3.3, §14.4): what one inspected drive and its baseline outcome say, as
/// <see cref="CheckItem"/>s that pass <see cref="CheckResultValidator"/>. A pure function of its two inputs: no clock, no file, no
/// Windows. The boundaries (<c>&lt;</c> or <c>&lt;=</c>) are v0.7.4's, written as constants here and pinned by the fixtures.
/// <para>
/// Every row of a disk is either a value with its verdict or, when the drive did not give the value, no value with the reason and the
/// verdict <c>unavailable</c> (a judged row) or <c>info</c> (an observation). A row a protocol does not have is not written at all.
/// The ".delta" rows are <see cref="BaselineDeltaRows"/>'s and go last.
/// </para>
/// </summary>
internal static class StorageJudge
{
    /// <summary>NVMe: the composite temperature that is a check, and the one that is a caution (≥).</summary>
    public const double NvmeTemperatureCheck = 80;

    public const double NvmeTemperatureCaution = 70;

    /// <summary>ATA: the same for attribute 194, or 190 when there is no 194 (≥).</summary>
    public const long AtaTemperatureCheck = 70;

    public const long AtaTemperatureCaution = 60;

    /// <summary>A temperature attribute outside 1 to 127 is not a temperature (the upper bytes of the raw value hold other numbers).</summary>
    public const long AtaTemperatureMin = 1;

    public const long AtaTemperatureMax = 127;

    /// <summary>The spare is a caution below the threshold plus this margin, a check below the threshold itself.</summary>
    public const double SpareMargin = 10;

    /// <summary>Percentage used: ≥ 100 check, ≥ 90 caution.</summary>
    public const double WearCheck = 100;

    public const double WearCaution = 90;

    /// <summary>The rows of one disk, in the order of §3.3: health, capacity, temperature, the protocol's rows, the ".delta" rows.</summary>
    public static IReadOnlyList<CheckItem> Rows(StorageInspectionDrive drive, BaselineDiskOutcome baseline, CheckSubject subject)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(subject);
        return new DiskRows(drive, baseline, subject).Build();
    }

    /// <summary>The subject of a disk: its "disk-N" from the baseline, and its model as read (never a serial). The model is left out rather than empty.</summary>
    public static CheckSubject SubjectOf(StorageInspectionDrive drive, string diskId) =>
        new(diskId, string.IsNullOrWhiteSpace(drive.Model) ? null : drive.Model.Trim());

    /// <summary>The reason a value is missing. A state that says nothing about the drive (not collected, gone) is the closest of the fixed list.</summary>
    public static CheckReason ReasonOf(ValueStatus status) => status switch
    {
        ValueStatus.Unsupported or ValueStatus.NotCollected => CheckReason.Unsupported,
        ValueStatus.Denied => CheckReason.Denied,
        ValueStatus.Timeout => CheckReason.Timeout,
        _ => CheckReason.Error,
    };

    private enum Growth
    {
        /// <summary>Media errors, ATA 5, 187, 196: grown is a check, never compared is a caution.</summary>
        Standard,

        /// <summary>ATA 197, 198: the sectors are unreadable now, so any value is a check, grown or not.</summary>
        Persistent,

        /// <summary>ATA 199 (CRC errors): the cable or the plug, more often than the disk. Always a caution, with the code.</summary>
        Cable,
    }

    private sealed class DiskRows(StorageInspectionDrive drive, BaselineDiskOutcome baseline, CheckSubject subject)
    {
        private readonly List<CheckItem> _rows = [];

        public IReadOnlyList<CheckItem> Build()
        {
            if (drive.Recognition == StorageRecognition.Recognized && drive.Health.TryGet(out var health))
            {
                Capacity();
                if (health.Kind == StorageHealthKind.Nvme)
                {
                    NvmeTemperature(health);
                    NvmeRows(health);
                }
                else
                {
                    AtaTemperature(health);
                    AtaRows(health);
                }

                _rows.Insert(0, HealthRow());
            }
            else
            {
                var reason = HealthMissingReason();
                _rows.Add(Item("storage.health", null, ItemVerdict.Unavailable, CheckSource.StorageQuery, reason));
                if (drive.Recognition == StorageRecognition.Recognized)
                {
                    Capacity();
                    _rows.Add(Item("storage.temperature", null, ItemVerdict.Unavailable, CheckSource.StorageQuery, reason));
                }
            }

            _rows.AddRange(BaselineDeltaRows.Build(baseline, subject));
            return _rows;
        }

        // ---- the rows every disk has ----

        private CheckReason HealthMissingReason()
        {
            var status = drive.Health.Status;
            if (status is ValueStatus.NotCollected && drive.Recognition == StorageRecognition.Unreadable)
            {
                return CheckReason.Error; // the slot is there and could not be looked at
            }

            return status == ValueStatus.Ok ? CheckReason.Error : ReasonOf(status);
        }

        /// <summary>
        /// The disk's health summary. DESIGN-A §14.4 says "as system.storage.health" (Windows' own HealthStatus), which the storage check's
        /// input does not carry, so the summary is the worst of the disk's own judged rows: a check is critical, a caution is warning, and
        /// a disk with nothing but ok rows is healthy. A row that was not measured says nothing (it has its own unavailable row).
        /// </summary>
        private CheckItem HealthRow()
        {
            var judged = _rows.Where(static r => r.Verdict is ItemVerdict.Ok or ItemVerdict.Caution or ItemVerdict.Check).ToArray();
            if (judged.Any(static r => r.Verdict == ItemVerdict.Check))
            {
                return Item("storage.health", CheckValue.Text("critical"), ItemVerdict.Check, CheckSource.StorageQuery);
            }

            if (judged.Any(static r => r.Verdict == ItemVerdict.Caution))
            {
                return Item("storage.health", CheckValue.Text("warning"), ItemVerdict.Caution, CheckSource.StorageQuery);
            }

            if (judged.Length > 0)
            {
                return Item("storage.health", CheckValue.Text("healthy"), ItemVerdict.Ok, CheckSource.StorageQuery);
            }

            // Readable, but not one judged reading came out of it.
            return Item("storage.health", null, ItemVerdict.Unavailable, CheckSource.StorageQuery, CheckReason.Partial);
        }

        private void Capacity()
        {
            if (drive.SizeBytes > 0)
            {
                Observed("storage.capacity", CheckValue.Dec((ulong)drive.SizeBytes), CheckSource.StorageQuery);
            }
            else
            {
                Observed("storage.capacity", null, CheckSource.StorageQuery, CheckReason.Error); // the geometry could not be read
            }
        }

        // ---- NVMe ----

        private void NvmeTemperature(StorageHealth health)
        {
            const string id = "storage.temperature";
            if (!health.TemperatureC.TryGet(out var celsius) || !double.IsFinite(celsius))
            {
                // Unreadable is unavailable whatever the minutes above the limits say (v0.7.4).
                Unavailable(id, CheckSource.Nvme, Missing(health.TemperatureC));
                return;
            }

            var verdict = celsius >= NvmeTemperatureCheck ? ItemVerdict.Check : celsius >= NvmeTemperatureCaution ? ItemVerdict.Caution : ItemVerdict.Ok;
            CheckEvidence? evidence = null;
            if (health.Nvme is { } details && details.CriticalCompositeTempMinutes.TryGet(out var critical) && critical > 0)
            {
                // The drive was above its critical temperature for this long, before this run: at least a caution, whatever it is now.
                if (verdict == ItemVerdict.Ok)
                {
                    verdict = ItemVerdict.Caution;
                }

                evidence = new CheckEvidence
                {
                    Code = CheckEvidenceCode.TempOverCriticalBefore,
                    CriticalTempMinutes = critical.ToString(CultureInfo.InvariantCulture),
                    WarningTempMinutes = details.WarningCompositeTempMinutes.TryGet(out var warning) && warning >= 0 ? warning.ToString(CultureInfo.InvariantCulture) : null,
                };
            }

            Judged(id, CheckValue.Number(celsius), verdict, CheckSource.Nvme, evidence);
        }

        private void NvmeRows(StorageHealth health)
        {
            const CheckSource nvme = CheckSource.Nvme;
            CriticalWarning(health);
            AvailableSpare(health);
            Observed("storage.nvme.spare_threshold", health.AvailableSpareThresholdPercent.TryGet(out var threshold) && double.IsFinite(threshold) ? CheckValue.Number(threshold) : null, nvme, MissingOrNull(health.AvailableSpareThresholdPercent, threshold));
            PercentageUsed(health);

            if (health.MediaErrors.TryGet(out var media))
            {
                GrowthCounter("storage.nvme.media_errors", "nvme.media_errors", media.Value, nvme, Growth.Standard);
            }
            else
            {
                Unavailable("storage.nvme.media_errors", nvme, Missing(health.MediaErrors));
            }

            ErrorLogEntries(health);
            ObserveWide("storage.nvme.unsafe_shutdowns", health.UnsafeShutdowns, nvme);
            ObserveWide("storage.nvme.power_cycles", health.PowerCycles, nvme);
            ObserveWide("storage.nvme.power_on_hours", health.PowerOnHours, nvme);
            if (drive.Counters?.DataUnitsWritten is { } units)
            {
                Observed("storage.nvme.data_units_written", CheckValue.Dec(units.Value), nvme);
            }
            else
            {
                Observed("storage.nvme.data_units_written", null, nvme, health.DataWrittenBytes.Status == ValueStatus.Ok ? CheckReason.Unsupported : ReasonOf(health.DataWrittenBytes.Status));
            }
        }

        private void CriticalWarning(StorageHealth health)
        {
            const string id = "storage.nvme.critical_warning";
            if (!health.CriticalWarning.TryGet(out var bits) || bits < 0)
            {
                Unavailable(id, CheckSource.Nvme, Missing(health.CriticalWarning));
                return;
            }

            var counter = Counter("nvme.critical_warning");
            var grew = counter is { IsCompared: true, Grew: true };
            CheckEvidenceCode? code = grew ? CheckEvidenceCode.NewBits : Decreased(counter) ? CheckEvidenceCode.CounterDecreased : null;
            var extra = grew ? new CheckEvidence { NewBits = counter!.NewBits } : null;
            Judged(id, CheckValue.Int(bits), bits != 0 ? ItemVerdict.Check : ItemVerdict.Ok, CheckSource.Nvme, Evidence(counter, (UInt128)bits, code, extra));
        }

        private void AvailableSpare(StorageHealth health)
        {
            const string id = "storage.nvme.available_spare";
            if (!TryWhole(health.AvailableSparePercent, out var spare))
            {
                Unavailable(id, CheckSource.Nvme, Missing(health.AvailableSparePercent));
                return;
            }

            // The threshold is the drive's own limit. A drive that does not give one is judged against 0, so only the margin applies.
            var threshold = health.AvailableSpareThresholdPercent.TryGet(out var t) && double.IsFinite(t) && t > 0 ? t : 0;
            var verdict = threshold > 0 && spare < threshold ? ItemVerdict.Check : spare < threshold + SpareMargin ? ItemVerdict.Caution : ItemVerdict.Ok;
            Judged(id, CheckValue.Int(spare), verdict, CheckSource.Nvme);
        }

        private void PercentageUsed(StorageHealth health)
        {
            const string id = "storage.nvme.percentage_used";
            if (!TryWhole(health.PercentageUsed, out var used))
            {
                Unavailable(id, CheckSource.Nvme, Missing(health.PercentageUsed));
                return;
            }

            Judged(id, CheckValue.Int(used), used >= WearCheck ? ItemVerdict.Check : used >= WearCaution ? ItemVerdict.Caution : ItemVerdict.Ok, CheckSource.Nvme);
        }

        private void ErrorLogEntries(StorageHealth health)
        {
            const string id = "storage.nvme.error_log_entries";
            UInt128 total;
            if (drive.Counters?.ErrorLogEntries is { } fromCounters)
            {
                total = fromCounters.Value;
            }
            else if (health.Nvme is { } details && details.ErrorLogEntries.TryGet(out var fromDetails))
            {
                total = fromDetails.Value;
            }
            else
            {
                Unavailable(id, CheckSource.Nvme, health.Nvme is { } d ? Missing(d.ErrorLogEntries) : CheckReason.Unsupported);
                return;
            }

            var counter = Counter("nvme.error_log_entries");
            var verdict = ItemVerdict.Ok;
            CheckEvidenceCode? code = null;
            CheckEvidence? extra = null;
            if (drive.Counters?.RecentErrorLog is { MediaErrors: >= 1 } recent)
            {
                // A media error in the last entries of the drive's own log. When that log was not read, nothing is written in its place.
                verdict = ItemVerdict.Caution;
                code = CheckEvidenceCode.MediaErrorsInLog;
                extra = new CheckEvidence { RecentMediaErrors = recent.MediaErrors, RecentEntries = Math.Max(0, recent.EntriesRead) };
            }
            else if (counter is { IsCompared: true, Grew: true })
            {
                verdict = ItemVerdict.Caution;
                code = CheckEvidenceCode.CounterIncreased;
            }
            else if (Decreased(counter))
            {
                code = CheckEvidenceCode.CounterDecreased;
            }

            Judged(id, CheckValue.Dec(total), verdict, CheckSource.Nvme, Evidence(counter, total, code, extra));
        }

        // ---- ATA ----

        private void AtaTemperature(StorageHealth health)
        {
            const string id = "storage.temperature";
            var attribute = LastAttribute(health, 194) ?? LastAttribute(health, 190);
            if (attribute is null)
            {
                Unavailable(id, CheckSource.Ata, CheckReason.Unsupported);
                return;
            }

            var degrees = (long)(attribute.Raw.Value & 0xFF); // the low byte: many models pack the lowest and highest above it
            if (degrees is < AtaTemperatureMin or > AtaTemperatureMax)
            {
                Unavailable(id, CheckSource.Ata, CheckReason.Error);
                return;
            }

            var verdict = degrees >= AtaTemperatureCheck ? ItemVerdict.Check : degrees >= AtaTemperatureCaution ? ItemVerdict.Caution : ItemVerdict.Ok;
            Judged(id, CheckValue.Number(degrees), verdict, CheckSource.Ata);
        }

        private void AtaRows(StorageHealth health)
        {
            const CheckSource ata = CheckSource.Ata;
            if (drive.Counters?.AtaPredictFailure is { } predicted)
            {
                // The window is read only when SMART READ DATA fails; a drive that was not asked this has no row (it is not "unavailable").
                Judged("storage.ata.predict_failure", CheckValue.Bool(predicted), predicted ? ItemVerdict.Check : ItemVerdict.Ok, ata);
            }

            ThresholdStatus(health);
            foreach (var id in new[] { 5, 187, 196, 197, 198, 199 })
            {
                if (AttributeRaw(health, id) is { } raw)
                {
                    var policy = id is 197 or 198 ? Growth.Persistent : id == 199 ? Growth.Cable : Growth.Standard;
                    GrowthCounter($"storage.ata.{id.ToString(CultureInfo.InvariantCulture)}", $"ata.{id.ToString(CultureInfo.InvariantCulture)}", raw, ata, policy);
                }
            }

            if (AttributeRaw(health, 9) is { } hours)
            {
                Observed("storage.ata.9", CheckValue.Dec(hours), ata);
            }
        }

        private void ThresholdStatus(StorageHealth health)
        {
            const string id = "storage.ata.threshold_status";
            if (drive.Counters is not { AtaThresholdsRead: true })
            {
                Unavailable(id, CheckSource.Ata, CheckReason.Unsupported); // without the table a 0 is not a real threshold
                return;
            }

            var current = false;
            var past = false;
            foreach (var attribute in health.Attributes)
            {
                if (attribute.Threshold <= 0)
                {
                    continue; // an attribute without a threshold is not compared (v0.7.4)
                }

                if (attribute.Current <= attribute.Threshold)
                {
                    current = true;
                }
                else if (attribute.Worst <= attribute.Threshold)
                {
                    past = true;
                }
            }

            if (current)
            {
                Judged(id, CheckValue.Text("current_breach"), ItemVerdict.Check, CheckSource.Ata, CheckEvidence.OfCode(CheckEvidenceCode.CurrentThresholdBreach));
            }
            else if (past)
            {
                Judged(id, CheckValue.Text("past_breach"), ItemVerdict.Caution, CheckSource.Ata, CheckEvidence.OfCode(CheckEvidenceCode.PastThresholdBreach));
            }
            else
            {
                Judged(id, CheckValue.Text("within"), ItemVerdict.Ok, CheckSource.Ata);
            }
        }

        // ---- counters that should not grow (§14.4, the table) ----

        private void GrowthCounter(string id, string name, UInt128 value, CheckSource source, Growth policy)
        {
            var counter = Counter(name);
            var compared = counter is { IsCompared: true };
            var grew = compared && counter!.Grew;
            ItemVerdict verdict;
            CheckEvidenceCode? code = null;
            if (value == 0)
            {
                verdict = ItemVerdict.Ok;
                code = Decreased(counter) ? CheckEvidenceCode.CounterDecreased : null;
            }
            else if (grew)
            {
                verdict = policy == Growth.Cable ? ItemVerdict.Caution : ItemVerdict.Check;
                code = policy == Growth.Cable ? CheckEvidenceCode.LikelyCable : CheckEvidenceCode.CounterIncreased;
            }
            else if (compared)
            {
                // Compared and not grown: the old value is the last run's, so it is not new. 197 and 198 still are unreadable sectors now.
                verdict = policy == Growth.Persistent ? ItemVerdict.Check : ItemVerdict.Ok;
                code = Decreased(counter) ? CheckEvidenceCode.CounterDecreased : null;
            }
            else
            {
                // Never compared (the first run, or a disk the baseline cannot tell apart): a value above 0 is a finding, but not a growth.
                verdict = policy == Growth.Persistent ? ItemVerdict.Check : ItemVerdict.Caution;
                code = policy == Growth.Cable ? CheckEvidenceCode.LikelyCable : null;
            }

            Judged(id, CheckValue.Dec(value), verdict, source, Evidence(counter, value, code));
        }

        // ---- building blocks ----

        private BaselineCounterOutcome? Counter(string name) =>
            baseline.Counters.FirstOrDefault(c => string.Equals(c.CounterId, BaselineCounters.ItemId(name), StringComparison.Ordinal));

        private static bool Decreased(BaselineCounterOutcome? counter) => counter is { IsCompared: true } c && c.After < c.Before;

        /// <summary>
        /// What a row rests on: the code (when there is one) and, for a counter that was compared, the numbers it was compared by. A
        /// counter that is 0 and did not go down needs none of that: nothing is said by "0 before, 0 now" that the row does not say.
        /// </summary>
        private static CheckEvidence? Evidence(BaselineCounterOutcome? counter, UInt128 value, CheckEvidenceCode? code, CheckEvidence? extra = null)
        {
            var evidence = (extra ?? new CheckEvidence()) with { Code = code };
            if (counter is { IsCompared: true } compared && (code is not null || value != 0))
            {
                evidence = evidence with
                {
                    Before = compared.Before!.Value.ToString(CultureInfo.InvariantCulture),
                    After = compared.After!.Value.ToString(CultureInfo.InvariantCulture),
                    SinceUtc = compared.SinceUtc,
                };
            }

            return evidence.IsEmpty ? null : evidence;
        }

        private static SmartAttribute? LastAttribute(StorageHealth health, int id) =>
            health.Attributes.LastOrDefault(a => a.Id == id);

        /// <summary>The attribute's value as the baseline keeps it (the same masks as <see cref="BaselineCounters.From"/>), so the row and its ".delta" agree.</summary>
        private static UInt128? AttributeRaw(StorageHealth health, int id)
        {
            if (LastAttribute(health, id) is not { } attribute)
            {
                return null;
            }

            var raw = attribute.Raw.Value;
            return id == 9 ? raw & 0xFFFFFFFF : raw;
        }

        /// <summary>A percentage that is a whole number (the row is an integer; a fraction or a negative number is not written as a value).</summary>
        private static bool TryWhole(Value<double> value, out long whole)
        {
            whole = 0;
            if (!value.TryGet(out var number) || !double.IsFinite(number) || number < 0 || number >= 1e18 || Math.Floor(number) != number)
            {
                return false;
            }

            whole = (long)number;
            return true;
        }

        /// <summary>Why a value that could not be used is missing: the status when the drive did not give it, an error when it gave something that is not a value.</summary>
        private static CheckReason Missing<T>(Value<T> value) => value.Status == ValueStatus.Ok ? CheckReason.Error : ReasonOf(value.Status);

        private static CheckReason MissingOrNull(Value<double> value, double read) =>
            value.Status == ValueStatus.Ok && !double.IsFinite(read) ? CheckReason.Error : Missing(value);

        private void ObserveWide(string id, Value<U128> value, CheckSource source)
        {
            if (value.TryGet(out var wide))
            {
                Observed(id, CheckValue.Dec(wide.Value), source);
            }
            else
            {
                Observed(id, null, source, Missing(value));
            }
        }

        private CheckItem Item(string id, CheckValue? value, ItemVerdict verdict, CheckSource source, CheckReason? reason = null, CheckEvidence? evidence = null) =>
            CheckItem.Create(id, value, verdict, source, reason, subject, evidence);

        private void Judged(string id, CheckValue value, ItemVerdict verdict, CheckSource source, CheckEvidence? evidence = null) =>
            _rows.Add(Item(id, value, verdict, source, null, evidence));

        private void Unavailable(string id, CheckSource source, CheckReason reason) =>
            _rows.Add(Item(id, null, ItemVerdict.Unavailable, source, reason));

        /// <summary>An observation: the value, or no value and why. It never counts in the overall verdict.</summary>
        private void Observed(string id, CheckValue? value, CheckSource source, CheckReason? missing = null) =>
            _rows.Add(Item(id, value, ItemVerdict.Info, source, value is null ? missing ?? CheckReason.Error : null));
    }
}
