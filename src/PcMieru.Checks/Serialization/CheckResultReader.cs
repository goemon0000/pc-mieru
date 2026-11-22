using System.Text.Json;
using static PcMieru.Checks.CheckJsonReading;

namespace PcMieru.Checks;

/// <summary>
/// Reads one pcm-check/1 result from parsed JSON, strictly or tolerantly (<see cref="CheckReadMode"/>, DESIGN-A §3 and
/// §3.5). Both modes refuse a wrong type, a missing required field (nothing is defaulted), an unknown verdict, reason or
/// outcome, a non-canonical number, and end in <see cref="CheckResultValidator"/>. The tolerant mode alone ignores
/// unknown keys and skips the rows a newer version added.
/// </summary>
internal static class CheckResultReader
{
    public static CheckReading Read(JsonElement root, CheckReadMode mode)
    {
        var tolerant = mode == CheckReadMode.Tolerant;
        var obj = Obj.Of(root, "result", tolerant);

        // The format is checked before anything else: a newer version may have other keys, and the reader must say
        // "unknown format" (keep the file) rather than "broken" (move it aside).
        if (!obj.TryGet("format", out var formatElement) || formatElement.ValueKind != JsonValueKind.String)
        {
            throw new CheckFormatException("format is missing or not a string.");
        }

        var format = formatElement.GetString()!;
        if (format != CheckResult.FormatId)
        {
            throw new CheckFormatException($"Unknown format '{format}'.") { UnknownFormat = format };
        }

        obj.RequireOnly("format", "app", "runId", "kind", "startedAtUtc", "endedAtUtc", "outcome", "verdict", "counts", "items", "notes");

        var runId = RunId(obj.Required("runId"), "runId");
        var kind = ReadKind(obj.Required("kind"));
        var items = ReadItems(obj.Required("items"), tolerant, out var verdicts, out var skipped);
        var allNotes = ReadStrings(obj.Required("notes"), "notes");

        // A tolerant reader takes a note it does not know as a note a newer version wrote: it is not shown (and it must
        // still be a well-formed code, which the validator checks on the notes that are kept; check the dropped ones here).
        var notes = allNotes;
        if (tolerant)
        {
            foreach (var note in allNotes)
            {
                if (!CheckNotes.IsWellFormed(note))
                {
                    throw new CheckFormatException($"note '{note}' is not of the form area.snake_code.");
                }
            }

            notes = allNotes.Where(CheckNotes.IsKnown).ToList();
        }

        var result = new CheckResult(
            format,
            Str(obj.Required("app"), "app"),
            runId,
            kind,
            Time(obj.Required("startedAtUtc"), "startedAtUtc"),
            Time(obj.Required("endedAtUtc"), "endedAtUtc"),
            ReadEnum<CheckOutcome>(obj.Required("outcome"), "outcome"),
            ReadEnum<CheckVerdict>(obj.Required("verdict"), "verdict"),
            ReadCounts(obj.Required("counts"), tolerant),
            items,
            notes);

        // The counts and the overall verdict are checked against every row the file had, the skipped ones included.
        CheckResultValidator.EnsureValid(result, mode, verdicts);
        return new CheckReading(result, skipped, allNotes.Count - notes.Count);
    }

    // A "kind" this version does not know is a later version's result (the file is kept), whichever the mode; one that is
    // not even a lowercase name is a broken file.
    private static CheckKind ReadKind(JsonElement element)
    {
        var text = Str(element, "kind");
        if (SnakeCaseEnum<CheckKind>.TryParse(text, out var kind))
        {
            return kind;
        }

        throw IsToken(text)
            ? new CheckFormatException($"kind: unknown kind '{text}'.") { UnknownKind = text }
            : new CheckFormatException($"kind '{text}' is not a lowercase name.");
    }

    private static List<CheckItem> ReadItems(JsonElement element, bool tolerant, out List<ItemVerdict> verdicts, out int skipped)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new CheckFormatException("items must be an array.");
        }

        var items = new List<CheckItem>(element.GetArrayLength());
        verdicts = new List<ItemVerdict>(element.GetArrayLength());
        skipped = 0;
        var index = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var item = ReadItem(entry, index++, tolerant, out var verdict);
            verdicts.Add(verdict);
            if (item is null)
            {
                skipped++;
            }
            else
            {
                items.Add(item);
            }
        }

        return items;
    }

    // Returns null for a row a tolerant reader does not show (an ID, a source or an evidence code of a newer version).
    // The verdict is always read, so the counts and the overall verdict can be checked against every row.
    private static CheckItem? ReadItem(JsonElement element, int index, bool tolerant, out ItemVerdict verdict)
    {
        var where = $"items[{index}]";
        var obj = Obj.Of(element, where, tolerant);
        obj.RequireOnly("id", "subject", "value", "unit", "verdict", "source", "reason", "evidence");
        obj.RequireKeys("id", "value", "unit", "verdict", "source", "reason");

        var id = Str(obj.Required("id"), $"{where}.id");
        var known = CheckItemRegistry.TryGet(id, out var spec);
        if (!known && !(tolerant && IsFutureItemId(id)))
        {
            throw new CheckFormatException($"{where}: unknown item ID '{id}'.");
        }

        where = $"{where} {id}";

        // A row this version does not know still has a verdict and a reason of the known values (a new value of either is pcm-check/2).
        verdict = ReadEnum<ItemVerdict>(obj.Required("verdict"), $"{where}.verdict");
        var reasonElement = obj.Required("reason");
        CheckReason? reason = reasonElement.ValueKind == JsonValueKind.Null ? null : ReadEnum<CheckReason>(reasonElement, $"{where}.reason");
        if (!known)
        {
            return null;
        }

        var sourceElement = obj.Required("source");
        if (!TryReadSource(sourceElement, where, tolerant, out var source))
        {
            return null;
        }

        CheckSubject? subject = null;
        if (obj.TryGet("subject", out var subjectElement))
        {
            var s = Obj.Of(subjectElement, $"{where}.subject", tolerant);
            s.RequireOnly("id", "model");
            s.RequireKeys("id");
            subject = new CheckSubject(
                Str(s.Required("id"), $"{where}.subject.id"),
                s.TryGet("model", out var model) ? Str(model, $"{where}.subject.model") : null);
        }

        var unitElement = obj.Required("unit");
        var unit = unitElement.ValueKind == JsonValueKind.Null ? null : Str(unitElement, $"{where}.unit");

        CheckEvidence? evidence = null;
        if (obj.TryGet("evidence", out var evidenceElement))
        {
            evidence = ReadEvidence(evidenceElement, where, tolerant);
            if (evidence is null)
            {
                return null;
            }
        }

        return new CheckItem(id, subject, ReadValue(obj.Required("value"), spec, where), unit, verdict, source, reason, evidence);
    }

    // A source that is a lowercase name this version does not know is "unknown": a tolerant reader skips the row.
    private static bool TryReadSource(JsonElement element, string where, bool tolerant, out CheckSource source)
    {
        var at = $"{where}.source";
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new CheckFormatException($"{at} must be a string.");
        }

        var text = element.GetString();
        if (SnakeCaseEnum<CheckSource>.TryParse(text, out source))
        {
            return true;
        }

        if (tolerant && IsToken(text))
        {
            return false;
        }

        throw new CheckFormatException($"{at}: unknown source '{text}'.");
    }

    private static CheckValue? ReadValue(JsonElement element, CheckItemSpec spec, string where)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var at = $"{where}.value";
        switch (spec.ValueType)
        {
            case CheckValueType.Int:
                if (element.ValueKind == JsonValueKind.Number)
                {
                    return CheckValue.Int(ReadLong(element, at));
                }

                break;
            case CheckValueType.Number:
                if (element.ValueKind == JsonValueKind.Number)
                {
                    return CheckValue.Number(FiniteNumber(element, at));
                }

                break;
            case CheckValueType.Dec:
                if (element.ValueKind == JsonValueKind.String && CheckValue.IsCanonicalDec(element.GetString()))
                {
                    return CheckValue.Dec(element.GetString()!);
                }

                break;
            case CheckValueType.SignedDec:
                if (element.ValueKind == JsonValueKind.String && CheckValue.IsCanonicalSignedDec(element.GetString()))
                {
                    return CheckValue.SignedDec(element.GetString()!);
                }

                break;
            case CheckValueType.Text:
                if (element.ValueKind == JsonValueKind.String)
                {
                    return CheckValue.Text(element.GetString()!);
                }

                break;
            case CheckValueType.Bool:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return CheckValue.Bool(element.GetBoolean());
                }

                break;
            case CheckValueType.OsVersion:
                return CheckValue.OsVersion(ReadOsVersion(element, at));
        }

        throw new CheckFormatException($"{at} must be a {Describe(spec.ValueType)}.");
    }

    private static OsVersionValue ReadOsVersion(JsonElement element, string at)
    {
        var obj = Obj.Of(element, at);
        obj.RequireOnly("name", "displayVersion", "build", "ubr");
        obj.RequireKeys("name", "displayVersion", "build", "ubr");
        var display = obj.Required("displayVersion");
        var ubr = obj.Required("ubr");
        return new OsVersionValue(
            Str(obj.Required("name"), $"{at}.name"),
            display.ValueKind == JsonValueKind.Null ? null : Str(display, $"{at}.displayVersion"),
            ReadInt(obj.Required("build"), $"{at}.build"),
            ubr.ValueKind == JsonValueKind.Null ? null : ReadInt(ubr, $"{at}.ubr"));
    }

    // Returns null when a tolerant reader meets an evidence code of a newer version: the whole row is not shown.
    private static CheckEvidence? ReadEvidence(JsonElement element, string where, bool tolerant)
    {
        var at = $"{where}.evidence";
        var obj = Obj.Of(element, at, tolerant);
        obj.RequireOnly(
            "code", "before", "after", "sinceUtc", "newBits", "totalBytes", "diskId", "efficiencyClass", "cv", "clockSteadiness",
            "warningTempMinutes", "criticalTempMinutes", "recentMediaErrors", "recentEntries", "capacities");

        CheckEvidenceCode? code = null;
        if (obj.TryGet("code", out var codeElement))
        {
            var text = Str(codeElement, $"{at}.code");
            if (SnakeCaseEnum<CheckEvidenceCode>.TryParse(text, out var known))
            {
                code = known;
            }
            else if (tolerant && IsToken(text))
            {
                return null;
            }
            else
            {
                throw new CheckFormatException($"{at}.code: unknown evidence code '{text}'.");
            }
        }

        return new CheckEvidence
        {
            Code = code,
            Before = OptionalStr(obj, "before", at),
            After = OptionalStr(obj, "after", at),
            SinceUtc = obj.TryGet("sinceUtc", out var since) ? Time(since, $"{at}.sinceUtc") : null,
            NewBits = OptionalInt(obj, "newBits", at),
            TotalBytes = OptionalStr(obj, "totalBytes", at),
            DiskId = OptionalStr(obj, "diskId", at),
            EfficiencyClass = OptionalInt(obj, "efficiencyClass", at),
            Cv = OptionalNumber(obj, "cv", at),
            ClockSteadiness = OptionalNumber(obj, "clockSteadiness", at),
            WarningTempMinutes = OptionalStr(obj, "warningTempMinutes", at),
            CriticalTempMinutes = OptionalStr(obj, "criticalTempMinutes", at),
            RecentMediaErrors = OptionalInt(obj, "recentMediaErrors", at),
            RecentEntries = OptionalInt(obj, "recentEntries", at),
            Capacities = obj.TryGet("capacities", out var capacities) ? ReadStrings(capacities, $"{at}.capacities") : null,
        };
    }

    private static string? OptionalStr(Obj obj, string key, string at) => obj.TryGet(key, out var e) ? Str(e, $"{at}.{key}") : null;

    private static int? OptionalInt(Obj obj, string key, string at) => obj.TryGet(key, out var e) ? ReadInt(e, $"{at}.{key}") : null;

    private static double? OptionalNumber(Obj obj, string key, string at) => obj.TryGet(key, out var e) ? FiniteNumber(e, $"{at}.{key}") : null;

    private static string Describe(CheckValueType type) => type switch
    {
        CheckValueType.Int => "JSON integer",
        CheckValueType.Number => "finite JSON number",
        CheckValueType.Dec => "canonical unsigned decimal string",
        CheckValueType.SignedDec => "canonical signed decimal string",
        CheckValueType.Text => "string",
        CheckValueType.Bool => "boolean",
        _ => "value",
    };
}
