using System.Globalization;
using System.Numerics;

namespace PcMieru.Checks;

/// <summary>A result could not be written or read because it breaks the pcm-check/1 rules. <see cref="Problems"/> lists every rule broken.</summary>
public sealed class CheckFormatException : Exception
{
    public CheckFormatException(string message)
        : this(message, [message])
    {
    }

    public CheckFormatException(string message, IReadOnlyList<string> problems)
        : base(message)
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Set when the file is well-formed JSON with a "format" string this build does not know (for example "pcm-check/2").
    /// The history store keeps such a file untouched and reports history_future_format; it must not quarantine it as broken.
    /// </summary>
    public string? UnknownFormat { get; init; }

    /// <summary>
    /// Set when the format is known but the "kind" is a well-formed name this build does not know (for example "gpu", which
    /// a later version that opens the GPU gate writes). A future version wrote it: keep the file untouched like an unknown
    /// format. A "kind" that is not even a lowercase name is broken, not future, and leaves this null.
    /// </summary>
    public string? UnknownKind { get; init; }

    /// <summary>True for a file a newer version wrote (an unknown format or an unknown kind): keep it, do not move it aside as broken.</summary>
    public bool IsFromFutureVersion => UnknownFormat is not null || UnknownKind is not null;
}

/// <summary>
/// The fixed note codes (DESIGN-A §3.2 CPU and memory notes, §14.3, §3.4 and §14.4 storage). A writer refuses a note that
/// is not in <see cref="Known"/>; a reader only checks the form (<see cref="IsWellFormed"/>), because a later version may
/// add a note (DESIGN-A §3.5-9). A new note is added here and in the design together.
/// </summary>
public static class CheckNotes
{
    /// <summary>Always on a CPU result: no driver is installed, so no temperature is read (DESIGN-A §3.2).</summary>
    public const string CpuTemperatureNotMeasured = "cpu.temperature_not_measured";

    /// <summary>Always on a memory result: only the borrowed range was checked (DESIGN-A §3.2).</summary>
    public const string MemoryBorrowedRegionOnly = "memory.borrowed_region_only";

    /// <summary>A memory run that stopped because free memory fell (DESIGN-A §14.3).</summary>
    public const string MemoryPressure = "memory.pressure";

    /// <summary>A storage result whose baseline could not be read (DESIGN-A §3).</summary>
    public const string StorageBaselineReadFailed = "storage.baseline_read_failed";

    /// <summary>Every note a writer may write.</summary>
    public static IReadOnlyList<string> Known { get; } =
        [CpuTemperatureNotMeasured, MemoryBorrowedRegionOnly, MemoryPressure, StorageBaselineReadFailed];

    public static bool IsKnown(string? note) => note is not null && Known.Contains(note);

    public static bool IsWellFormed(string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return false;
        }

        var dot = note.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && note.IndexOf('.', dot + 1) < 0 && IsSegment(note.AsSpan(0, dot)) && IsSegment(note.AsSpan(dot + 1));
    }

    private static bool IsSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Every rule a pcm-check/1 result must satisfy, in one place: the writer refuses to write a result that fails and the
/// reader refuses to return one. Rules come from DESIGN-A §3, §3.5 and §14 (§14 wins). It does not judge items: whether a
/// verdict is the right one is the producer's job (later steps); this checks that the shape and the claims agree.
/// </summary>
public static class CheckResultValidator
{
    /// <summary>All problems of the strict reading (the writer's and the tests'), or an empty list for a valid result.</summary>
    public static IReadOnlyList<string> Validate(CheckResult result) => Validate(result, CheckReadMode.Strict, null);

    /// <summary>
    /// <paramref name="allVerdicts"/> is the verdict of every row the file had, including the rows a tolerant reader
    /// skipped (null: the rows of <paramref name="result"/> are all the rows). Counts and the overall verdict are checked against it.
    /// </summary>
    internal static IReadOnlyList<string> Validate(CheckResult result, CheckReadMode mode, IReadOnlyList<ItemVerdict>? allVerdicts)
    {
        ArgumentNullException.ThrowIfNull(result);
        var problems = new List<string>();

        if (result.Format != CheckResult.FormatId)
        {
            problems.Add($"format must be '{CheckResult.FormatId}'.");
        }

        if (string.IsNullOrWhiteSpace(result.App))
        {
            problems.Add("app must not be empty.");
        }

        if (result.RunId == Guid.Empty)
        {
            problems.Add("runId must not be the empty UUID.");
        }

        if (!Enum.IsDefined(result.Kind))
        {
            problems.Add("kind is not a known kind.");
        }

        if (!Enum.IsDefined(result.Outcome))
        {
            problems.Add("outcome is not a known outcome.");
        }

        if (!Enum.IsDefined(result.Verdict))
        {
            problems.Add("verdict is not a known verdict.");
        }

        // DESIGN-A §3 / §4: the JSON carries UTC with milliseconds, and the history file name carries the same
        // millisecond (CheckTime.FileStamp, from the same truncated value). A producer must therefore cut its clock
        // reading to milliseconds (CheckTime.TruncateToMilliseconds) before it builds the result; nothing is cut silently.
        if (!CheckTime.IsCanonical(result.StartedAtUtc))
        {
            problems.Add("startedAtUtc must be UTC with millisecond precision.");
        }

        if (!CheckTime.IsCanonical(result.EndedAtUtc))
        {
            problems.Add("endedAtUtc must be UTC with millisecond precision.");
        }

        if (result.EndedAtUtc < result.StartedAtUtc)
        {
            problems.Add("endedAtUtc is before startedAtUtc.");
        }

        if (result.Items is null)
        {
            problems.Add("items must not be null.");
            return problems;
        }

        // Counts and the overall verdict are recomputed below; that is only possible when every item is there and has a
        // known verdict (the item loop reports the ones that are not), so Validate reports instead of throwing.
        var comparable = result.Items.All(static item => item is not null && Enum.IsDefined(item.Verdict));
        IReadOnlyList<ItemVerdict>? verdicts = allVerdicts ?? (comparable ? result.Items.Select(static item => item.Verdict).ToArray() : null);

        if (result.Counts is null || !result.Counts.IsNonNegative)
        {
            problems.Add("counts must have five non-negative values.");
        }
        else if (verdicts is not null)
        {
            var recomputed = CheckCounts.FromVerdicts(verdicts);
            if (recomputed != result.Counts)
            {
                problems.Add($"counts {Describe(result.Counts)} do not match the items {Describe(recomputed)}.");
            }
        }

        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < result.Items.Count; i++)
        {
            var item = result.Items[i];
            if (item is null)
            {
                problems.Add($"items[{i}] is null.");
                continue;
            }

            ValidateItem(item, result.Kind, i, problems);
            if (!seen.Add((item.Id, item.Subject?.Id ?? string.Empty)))
            {
                problems.Add($"items[{i}] {item.Id}: (id, subject.id) is not unique in the result.");
            }
        }

        // A tolerant reader that skipped rows cannot judge the rows that need a neighbour (the GPU device status may be one of them).
        ValidateAcrossItems(result.Items, rowsMissing: allVerdicts is not null && allVerdicts.Count != result.Items.Count, problems);

        // The overall verdict is a function of the items.
        if (verdicts is not null && Enum.IsDefined(result.Verdict) && CheckVerdicts.Overall(verdicts) != result.Verdict)
        {
            problems.Add($"verdict '{result.Verdict}' does not match the items ('{CheckVerdicts.Overall(verdicts)}').");
        }

        if (result.Notes is null)
        {
            problems.Add("notes must not be null.");
        }
        else
        {
            var notes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var note in result.Notes)
            {
                if (!CheckNotes.IsWellFormed(note))
                {
                    problems.Add($"note '{note}' is not of the form area.snake_code.");
                }
                else if (mode == CheckReadMode.Strict && !CheckNotes.IsKnown(note))
                {
                    problems.Add($"note '{note}' is not a known note code.");
                }
                else if (!notes.Add(note))
                {
                    problems.Add($"note '{note}' appears twice.");
                }
            }
        }

        return problems;
    }

    /// <summary>Throws <see cref="CheckFormatException"/> when <see cref="Validate(CheckResult)"/> finds anything.</summary>
    public static void EnsureValid(CheckResult result) => EnsureValid(result, CheckReadMode.Strict, null);

    internal static void EnsureValid(CheckResult result, CheckReadMode mode, IReadOnlyList<ItemVerdict>? allVerdicts)
    {
        var problems = Validate(result, mode, allVerdicts);
        if (problems.Count > 0)
        {
            throw new CheckFormatException($"The check result is not valid pcm-check/1: {problems[0]}" + (problems.Count > 1 ? $" (and {problems.Count - 1} more)" : string.Empty), problems);
        }
    }

    private static string Describe(CheckCounts c) => $"ok={c.Ok} caution={c.Caution} check={c.Check} unavailable={c.Unavailable} info={c.Info}";

    private static void ValidateItem(CheckItem item, CheckKind resultKind, int index, List<string> problems)
    {
        var where = $"items[{index}] {item.Id}";
        if (!CheckItemRegistry.TryGet(item.Id, out var spec))
        {
            problems.Add($"{where}: unknown item ID.");
            return;
        }

        if (spec.Kind != resultKind)
        {
            problems.Add($"{where}: belongs to a {spec.Kind} result, not {resultKind}.");
        }

        ValidateSubject(item, spec, where, problems);

        if (!string.Equals(item.Unit, spec.Unit, StringComparison.Ordinal))
        {
            problems.Add($"{where}: unit must be {(spec.Unit is null ? "null" : $"'{spec.Unit}'")}.");
        }

        if (!Enum.IsDefined(item.Verdict))
        {
            problems.Add($"{where}: unknown verdict.");
        }

        if (!Enum.IsDefined(item.Source))
        {
            problems.Add($"{where}: unknown source.");
        }

        if (item.Reason is { } reason && !Enum.IsDefined(reason))
        {
            problems.Add($"{where}: unknown reason.");
        }

        if (item.Value is { } value)
        {
            ValidateValue(value, spec, where, problems);

            // DESIGN-A §3: a value with a reason is allowed for one reason only, partial.
            if (item.Reason is { } r && r != CheckReason.Partial)
            {
                problems.Add($"{where}: a value is present, so the reason must be null or partial (not {r}).");
            }
        }
        else if (item.Reason is null)
        {
            problems.Add($"{where}: no value, so a reason is required.");
        }

        if (Enum.IsDefined(item.Verdict))
        {
            ValidateVerdict(item, spec, where, problems);
        }

        ValidateEvidence(item, spec, where, problems);
    }

    private static void ValidateSubject(CheckItem item, CheckItemSpec spec, string where, List<string> problems)
    {
        if (spec.Subject == SubjectKind.None)
        {
            if (item.Subject is not null)
            {
                problems.Add($"{where}: this ID is not repeated and must not have a subject.");
            }

            return;
        }

        if (item.Subject is null)
        {
            problems.Add($"{where}: a subject '{SubjectKinds.Prefix(spec.Subject)}-N' is required.");
            return;
        }

        if (!SubjectKinds.IsValidId(spec.Subject, item.Subject.Id))
        {
            problems.Add($"{where}: subject id must look like '{SubjectKinds.Prefix(spec.Subject)}-N' with N from 1.");
        }

        if (item.Subject.Model is { } model && string.IsNullOrWhiteSpace(model))
        {
            problems.Add($"{where}: subject.model must be omitted rather than empty.");
        }
    }

    private static void ValidateValue(CheckValue value, CheckItemSpec spec, string where, List<string> problems)
    {
        if (value.Type != spec.ValueType)
        {
            problems.Add($"{where}: value must be of type {spec.ValueType}, not {value.Type}.");
            return;
        }

        switch (value.Type)
        {
            // DESIGN-A §3.5-3: these units count things; they cannot be negative.
            case CheckValueType.Int when value.AsInt < 0 && spec.Unit is "count" or "bits" or "code" or "d":
                problems.Add($"{where}: a value in '{spec.Unit}' cannot be negative.");
                break;
            case CheckValueType.Number when !double.IsFinite(value.AsNumber):
                problems.Add($"{where}: a number must be finite.");
                break;
            case CheckValueType.Text:
                if (string.IsNullOrWhiteSpace(value.AsText))
                {
                    problems.Add($"{where}: a text value must not be empty (use null and a reason).");
                }
                else if (spec.AllowedText is { } allowed && !allowed.Contains(value.AsText))
                {
                    problems.Add($"{where}: value must be one of {string.Join('|', allowed)}.");
                }

                break;
            case CheckValueType.OsVersion:
                var os = value.AsOsVersion;
                if (string.IsNullOrWhiteSpace(os.Name) || os.Build < 0 || os.Ubr < 0 || (os.DisplayVersion is { } display && string.IsNullOrWhiteSpace(display)))
                {
                    problems.Add($"{where}: os version needs a name, a build >= 0, and a ubr >= 0 or null; displayVersion is null or non-empty.");
                }

                break;
        }
    }

    // DESIGN-A §3.5-2 and §14.0. A judged item allows exactly:
    //   value, reason null     -> ok, caution, check
    //   value, reason partial  -> caution, check, unavailable
    //   no value, a reason     -> unavailable
    // and is info only for the two §14.0 exceptions. An observation-only item (info) allows value+null, value+partial and
    // no value+reason, which the value/reason rule above already enforces.
    private static void ValidateVerdict(CheckItem item, CheckItemSpec spec, string where, List<string> problems)
    {
        var hasValue = item.Value is not null;

        // Exception 1: a .delta that could not be compared has no value and is info (whatever its class), reason partial.
        if (spec.IsDelta && !hasValue)
        {
            if (item.Verdict != ItemVerdict.Info)
            {
                problems.Add($"{where}: a delta without a value (not comparable) must have verdict info.");
            }

            if (item.Reason != CheckReason.Partial)
            {
                problems.Add($"{where}: a delta without a value (not comparable) must have reason partial.");
            }

            return;
        }

        if (spec.Class == ItemClass.Info)
        {
            if (item.Verdict != ItemVerdict.Info)
            {
                problems.Add($"{where}: an observation-only item must have verdict info.");
            }

            return;
        }

        if (item.Verdict == ItemVerdict.Info)
        {
            if (spec.IsDelta)
            {
                problems.Add($"{where}: a compared delta is judged; only a delta without a value is info.");
            }
            else if (!spec.InfoWhenGpuStatusNonZero)
            {
                problems.Add($"{where}: a judged item cannot have verdict info (only a delta without a value, and the GPU driver rows when the device status is not 0).");
            }

            // The GPU driver rows: whether this one is info is decided against the device status of the same GPU (ValidateAcrossItems).
            return;
        }

        if (!hasValue)
        {
            if (item.Verdict != ItemVerdict.Unavailable)
            {
                problems.Add($"{where}: a judged item without a value must be unavailable.");
            }

            return;
        }

        switch (item.Reason)
        {
            case null when item.Verdict is not (ItemVerdict.Ok or ItemVerdict.Caution or ItemVerdict.Check):
                problems.Add($"{where}: a judged item with a value and no reason must be ok, caution or check, not {item.Verdict.ToString().ToLowerInvariant()}.");
                break;
            case CheckReason.Partial when item.Verdict == ItemVerdict.Ok:
                problems.Add($"{where}: ok cannot carry a reason: a value that did not finish is caution, check or unavailable.");
                break;
        }
    }

    // The rules that look at more than one row of a result.
    private static void ValidateAcrossItems(IReadOnlyList<CheckItem> items, bool rowsMissing, List<string> problems)
    {
        if (!rowsMissing)
        {
            ValidateGpuDriverRows(items, problems);
        }

        ValidatePerDiskBaseline(items, problems);
    }

    // DESIGN-A §14.0 exception 2 / §3.5-10: a GPU's driver rows are info exactly when that GPU's device status is not 0;
    // otherwise they are judged (an absent or missing device status is not "non-zero").
    private static void ValidateGpuDriverRows(IReadOnlyList<CheckItem> items, List<string> problems)
    {
        var failing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is { Id: CheckItemRegistry.GpuDeviceStatusId, Subject: { } subject, Value: { Type: CheckValueType.Int } value } && value.AsInt != 0)
            {
                failing.Add(subject.Id);
            }
        }

        foreach (var item in items)
        {
            if (item is null
                || item.Subject is not { } subject
                || !CheckItemRegistry.TryGet(item.Id, out var spec)
                || !spec.InfoWhenGpuStatusNonZero
                || !Enum.IsDefined(item.Verdict))
            {
                continue;
            }

            var statusIsNonZero = failing.Contains(subject.Id);
            if (statusIsNonZero && item.Verdict != ItemVerdict.Info)
            {
                problems.Add($"{item.Id} {subject.Id}: the device status of this GPU is not 0, so this row must be info.");
            }
            else if (!statusIsNonZero && item.Verdict == ItemVerdict.Info)
            {
                problems.Add($"{item.Id} {subject.Id}: this row is info only when the device status of the same GPU is not 0.");
            }
        }
    }

    // DESIGN-A §3.5-7: the baseline codes that are about a disk as a whole. Once one is on a disk, every .delta of that
    // disk is null with the same code. baseline_missing_counter is the one about a single row.
    private static void ValidatePerDiskBaseline(IReadOnlyList<CheckItem> items, List<string> problems)
    {
        var byDisk = new Dictionary<string, List<CheckItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item?.Subject is { } subject && CheckItemRegistry.TryGet(item.Id, out var spec) && spec.IsDelta)
            {
                if (!byDisk.TryGetValue(subject.Id, out var rows))
                {
                    byDisk.Add(subject.Id, rows = []);
                }

                rows.Add(item);
            }
        }

        foreach (var (disk, rows) in byDisk)
        {
            var diskWide = rows
                .Select(static row => row.Evidence?.Code)
                .OfType<CheckEvidenceCode>()
                .Where(static code => Enum.IsDefined(code) && CheckEvidenceCodes.IsPerDisk(code))
                .Distinct()
                .ToArray();
            if (diskWide.Length == 0)
            {
                continue;
            }

            if (diskWide.Length > 1)
            {
                problems.Add($"{disk}: the .delta rows carry different disk-wide baseline codes ({string.Join(", ", diskWide.Select(static c => SnakeCaseEnum<CheckEvidenceCode>.Name(c)))}); one code applies to the whole disk.");
                continue;
            }

            var code = diskWide[0];
            var name = SnakeCaseEnum<CheckEvidenceCode>.Name(code);
            foreach (var row in rows)
            {
                if (row.Value is not null)
                {
                    problems.Add($"{row.Id} {disk}: the disk-wide baseline code '{name}' is on this disk, so every .delta of the disk must be null; this one has a value.");
                }
                else if (row.Evidence?.Code != code)
                {
                    problems.Add($"{row.Id} {disk}: every .delta of this disk must carry the same baseline code '{name}'.");
                }
            }
        }
    }

    private static void ValidateEvidence(CheckItem item, CheckItemSpec spec, string where, List<string> problems)
    {
        var evidence = item.Evidence;
        var hasValue = item.Value is not null;

        if (evidence is not null && evidence.IsEmpty)
        {
            problems.Add($"{where}: evidence must be omitted rather than empty.");
            return;
        }

        if (evidence is not null)
        {
            ValidateEvidenceTypes(evidence, where, problems);

            if (evidence.Code is { } code)
            {
                if (!Enum.IsDefined(code))
                {
                    problems.Add($"{where}: unknown evidence code.");
                }
                else if (!CheckItemRegistry.AllowsEvidenceCode(spec.Id, code))
                {
                    problems.Add($"{where}: evidence code '{SnakeCaseEnum<CheckEvidenceCode>.Name(code)}' cannot be carried by this item; it belongs to {DescribePlacement(code)}.");
                }
            }

            if (evidence.NewBits is not null && spec.Id is not ("storage.nvme.critical_warning" or "storage.nvme.critical_warning.delta"))
            {
                problems.Add($"{where}: newBits is evidence of storage.nvme.critical_warning and its .delta only.");
            }
        }

        if (!spec.IsDelta)
        {
            return;
        }

        if (!hasValue)
        {
            // DESIGN-A §3.3 / §3.5-6: not comparable, so the reason is a baseline_* code, and it is the only evidence.
            if (evidence?.Code is not { } why || !CheckEvidenceCodes.IsBaseline(why))
            {
                problems.Add($"{where}: a delta without a value needs evidence.code of the form baseline_*.");
            }
            else if (!(evidence with { Code = null }).IsEmpty)
            {
                problems.Add($"{where}: a delta without a value carries only evidence.code (no before, after, sinceUtc or other key).");
            }

            return;
        }

        if (evidence?.Before is null || evidence.After is null || evidence.SinceUtc is null)
        {
            problems.Add($"{where}: a compared delta needs evidence before, after and sinceUtc.");
            return;
        }

        if (evidence.Code is { } c && CheckEvidenceCodes.IsBaseline(c))
        {
            problems.Add($"{where}: a compared delta cannot carry a baseline_* code.");
        }

        if (CheckValue.IsCanonicalDec(evidence.Before) && CheckValue.IsCanonicalDec(evidence.After))
        {
            var expected = BigInteger.Parse(evidence.After, CultureInfo.InvariantCulture) - BigInteger.Parse(evidence.Before, CultureInfo.InvariantCulture);
            if (item.Value is { Type: CheckValueType.SignedDec } delta && delta.AsText != expected.ToString(CultureInfo.InvariantCulture))
            {
                problems.Add($"{where}: the delta must be after minus before ({expected.ToString(CultureInfo.InvariantCulture)}).");
            }
        }
    }

    private static string DescribePlacement(CheckEvidenceCode code)
    {
        if (CheckEvidenceCodes.IsBaseline(code))
        {
            return "the .delta rows only";
        }

        if (code is CheckEvidenceCode.CounterIncreased or CheckEvidenceCode.CounterDecreased)
        {
            return "the .delta rows and the rows they are computed from";
        }

        return string.Join(", ", CheckItemRegistry.RowsFor(code));
    }

    private static void ValidateEvidenceTypes(CheckEvidence e, string where, List<string> problems)
    {
        CheckDec(e.Before, "before");
        CheckDec(e.After, "after");
        CheckDec(e.TotalBytes, "totalBytes");
        CheckDec(e.WarningTempMinutes, "warningTempMinutes");
        CheckDec(e.CriticalTempMinutes, "criticalTempMinutes");

        if (e.SinceUtc is { } since && !CheckTime.IsCanonical(since))
        {
            problems.Add($"{where}: evidence.sinceUtc must be UTC with millisecond precision.");
        }

        if (e.DiskId is not null && !SubjectKinds.IsValidId(SubjectKind.Disk, e.DiskId))
        {
            problems.Add($"{where}: evidence.diskId must look like 'disk-N' with N from 1.");
        }

        if (e.NewBits < 0 || e.EfficiencyClass < 0 || e.RecentMediaErrors < 0 || e.RecentEntries < 0)
        {
            problems.Add($"{where}: evidence integers cannot be negative.");
        }

        if ((e.Cv is { } cv && (!double.IsFinite(cv) || double.IsNegative(cv))) || (e.ClockSteadiness is { } cs && (!double.IsFinite(cs) || double.IsNegative(cs))))
        {
            problems.Add($"{where}: evidence numbers must be finite and not negative (a variation and a clock percentage cannot be below 0; -0 is not a canonical number).");
        }

        if (e.Capacities is { } capacities)
        {
            if (capacities.Count == 0)
            {
                problems.Add($"{where}: evidence.capacities must not be empty.");
            }

            foreach (var capacity in capacities)
            {
                if (!CheckValue.IsCanonicalDec(capacity))
                {
                    problems.Add($"{where}: evidence.capacities must be unsigned decimal strings.");
                    break;
                }
            }
        }

        void CheckDec(string? text, string name)
        {
            if (text is not null && !CheckValue.IsCanonicalDec(text))
            {
                problems.Add($"{where}: evidence.{name} must be an unsigned decimal string.");
            }
        }
    }
}
