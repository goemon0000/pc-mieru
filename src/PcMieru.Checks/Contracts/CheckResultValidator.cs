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
}

/// <summary>The fixed note codes DESIGN-A names. A note is "area.snake_code"; the validator checks that form, not this list, because the design gives examples, not the whole list.</summary>
public static class CheckNotes
{
    public const string CpuTemperatureNotMeasured = "cpu.temperature_not_measured";
    public const string MemoryBorrowedRegionOnly = "memory.borrowed_region_only";
    public const string MemoryPressure = "memory.pressure";
    public const string StorageBaselineReadFailed = "storage.baseline_read_failed";

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
/// reader refuses to return one. Rules come from DESIGN-A §3 and §14 (§14 wins). It does not judge items: whether a
/// verdict is the right one is the producer's job (later steps); this checks that the shape and the claims agree.
/// </summary>
public static class CheckResultValidator
{
    /// <summary>All problems, or an empty list for a valid result.</summary>
    public static IReadOnlyList<string> Validate(CheckResult result)
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

        // DESIGN-A §3 / §4: the JSON carries UTC with milliseconds; the filename timestamp has 7 digits, so a producer
        // must cut its clock reading to milliseconds (CheckTime.TruncateToMilliseconds) before it builds the result.
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

        if (result.Counts is null || !result.Counts.IsNonNegative)
        {
            problems.Add("counts must have five non-negative values.");
        }
        else if (comparable)
        {
            var recomputed = CheckCounts.From(result.Items);
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

        // The overall verdict is a function of the items.
        if (comparable && Enum.IsDefined(result.Verdict) && CheckVerdicts.Overall(result.Items) != result.Verdict)
        {
            problems.Add($"verdict '{result.Verdict}' does not match the items ('{CheckVerdicts.Overall(result.Items)}').");
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
                else if (!notes.Add(note))
                {
                    problems.Add($"note '{note}' appears twice.");
                }
            }
        }

        return problems;
    }

    /// <summary>Throws <see cref="CheckFormatException"/> when <see cref="Validate"/> finds anything.</summary>
    public static void EnsureValid(CheckResult result)
    {
        var problems = Validate(result);
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

        ValidateVerdict(item, spec, where, problems);
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
            problems.Add($"{where}: subject id must look like '{SubjectKinds.Prefix(spec.Subject)}-N'.");
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
            case CheckValueType.Int when spec.Unit == "count" && value.AsInt < 0:
                problems.Add($"{where}: a count cannot be negative.");
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

    // DESIGN-A §14.0: a judged ID is info only for the two exceptions; a judged ID without a value is unavailable.
    private static void ValidateVerdict(CheckItem item, CheckItemSpec spec, string where, List<string> problems)
    {
        var hasValue = item.Value is not null;
        if (spec.Class == ItemClass.Info)
        {
            if (item.Verdict != ItemVerdict.Info)
            {
                problems.Add($"{where}: an observation-only item must have verdict info.");
            }

            return;
        }

        if (spec.IsDelta)
        {
            // Exception 1: a .delta that could not be compared has no value and is info. A compared one is judged.
            if (!hasValue && item.Verdict != ItemVerdict.Info)
            {
                problems.Add($"{where}: a delta without a value (not comparable) must have verdict info.");
            }
            else if (hasValue && item.Verdict == ItemVerdict.Info)
            {
                problems.Add($"{where}: a compared delta is judged; only a delta without a value is info.");
            }

            return;
        }

        if (item.Verdict == ItemVerdict.Info)
        {
            // Exception 2: the GPU driver rows are info when that GPU's device status is not 0. The producer decides; allowed here.
            if (!spec.InfoWhenGpuStatusNonZero)
            {
                problems.Add($"{where}: a judged item cannot have verdict info (only a delta without a value, and the GPU driver rows when the device status is not 0).");
            }

            return;
        }

        if (!hasValue && item.Verdict != ItemVerdict.Unavailable)
        {
            problems.Add($"{where}: a judged item without a value must be unavailable.");
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
                else if (CheckEvidenceCodes.IsBaseline(code) && spec.Kind != CheckKind.Storage)
                {
                    problems.Add($"{where}: baseline evidence codes belong to storage items.");
                }
            }

            if (evidence.NewBits is not null && spec.Id != "storage.nvme.critical_warning.delta")
            {
                problems.Add($"{where}: newBits is evidence of storage.nvme.critical_warning.delta only.");
            }
        }

        if (!spec.IsDelta)
        {
            return;
        }

        if (!hasValue)
        {
            // DESIGN-A §3.3: not comparable, so the reason is a baseline_* code.
            if (evidence?.Code is not { } why || !CheckEvidenceCodes.IsBaseline(why))
            {
                problems.Add($"{where}: a delta without a value needs evidence.code of the form baseline_*.");
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
            problems.Add($"{where}: evidence.diskId must look like 'disk-N'.");
        }

        if (e.NewBits < 0 || e.EfficiencyClass < 0 || e.RecentMediaErrors < 0 || e.RecentEntries < 0)
        {
            problems.Add($"{where}: evidence integers cannot be negative.");
        }

        if ((e.Cv is { } cv && !double.IsFinite(cv)) || (e.ClockSteadiness is { } cs && !double.IsFinite(cs)))
        {
            problems.Add($"{where}: evidence numbers must be finite.");
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
