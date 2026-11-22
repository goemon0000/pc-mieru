using System.Text.Json;
using static PcMieru.Checks.CheckJsonReading;

namespace PcMieru.Checks;

/// <summary>
/// Reads a history page (<see cref="CheckHistoryPage"/>) from parsed JSON. It is the tolerant reader of DESIGN-A §3: an
/// unknown key is ignored, and an entry of a "kind" this version does not know is not shown but counted in one
/// <see cref="CheckHistoryWarningCodes.FutureFormat"/> warning, so one row written by a later version never fails the whole
/// page. It still refuses what a later version may not change without a new format: a missing required field (nothing is
/// defaulted), a wrong type, an unknown verdict or outcome, a negative count, a malformed UUID or time, and an unknown
/// warning code.
/// </summary>
internal static class CheckHistoryReader
{
    public static CheckHistoryPage Read(JsonElement root)
    {
        var obj = Obj.Of(root, "history page", lenient: true);
        var entriesElement = obj.Required("entries");
        var warningsElement = obj.Required("warnings");

        if (entriesElement.ValueKind != JsonValueKind.Array)
        {
            throw new CheckFormatException("entries must be an array.");
        }

        if (warningsElement.ValueKind != JsonValueKind.Array)
        {
            throw new CheckFormatException("warnings must be an array.");
        }

        var entries = new List<CheckHistoryEntry>(entriesElement.GetArrayLength());
        var future = 0;
        var index = 0;
        foreach (var element in entriesElement.EnumerateArray())
        {
            var entry = ReadEntry(element, $"entries[{index++}]");
            if (entry is null)
            {
                future++;
            }
            else
            {
                entries.Add(entry);
            }
        }

        var warnings = new List<CheckHistoryWarning>(warningsElement.GetArrayLength() + 1);
        index = 0;
        foreach (var element in warningsElement.EnumerateArray())
        {
            warnings.Add(ReadWarning(element, $"warnings[{index++}]"));
        }

        if (future > 0)
        {
            // One warning per code: the skipped rows are counted into the future-format line that is already there.
            var at = warnings.FindIndex(static w => w.Code == CheckHistoryWarningCodes.FutureFormat);
            if (at >= 0)
            {
                warnings[at] = warnings[at] with { Count = warnings[at].Count + future };
            }
            else
            {
                warnings.Add(new CheckHistoryWarning(CheckHistoryWarningCodes.FutureFormat, future));
            }
        }

        return new CheckHistoryPage(entries, warnings);
    }

    // Returns null for an entry of a kind a later version added (everything else of it is still checked).
    private static CheckHistoryEntry? ReadEntry(JsonElement element, string at)
    {
        var obj = Obj.Of(element, at, lenient: true);
        var runId = RunId(obj.Required("runId"), $"{at}.runId");
        if (runId == Guid.Empty)
        {
            throw new CheckFormatException($"{at}.runId must not be the empty UUID.");
        }

        var kindText = Str(obj.Required("kind"), $"{at}.kind");
        var knownKind = SnakeCaseEnum<CheckKind>.TryParse(kindText, out var kind);
        if (!knownKind && !IsToken(kindText))
        {
            throw new CheckFormatException($"{at}.kind '{kindText}' is not a lowercase name.");
        }

        var started = Time(obj.Required("startedAtUtc"), $"{at}.startedAtUtc");
        var ended = Time(obj.Required("endedAtUtc"), $"{at}.endedAtUtc");
        var outcome = ReadEnum<CheckOutcome>(obj.Required("outcome"), $"{at}.outcome");
        var verdict = ReadEnum<CheckVerdict>(obj.Required("verdict"), $"{at}.verdict");
        var counts = ReadCounts(obj.Required("counts"), lenient: true, $"{at}.counts");
        if (!counts.IsNonNegative)
        {
            throw new CheckFormatException($"{at}.counts must have five non-negative values.");
        }

        return knownKind ? new CheckHistoryEntry(runId, kind, started, ended, outcome, verdict, counts) : null;
    }

    private static CheckHistoryWarning ReadWarning(JsonElement element, string at)
    {
        var obj = Obj.Of(element, at, lenient: true);
        var code = Str(obj.Required("code"), $"{at}.code");
        if (!CheckHistoryWarningCodes.IsKnown(code))
        {
            throw new CheckFormatException($"{at}.code: unknown warning code '{code}'.");
        }

        return new CheckHistoryWarning(code, ReadNonNegativeInt(obj.Required("count"), $"{at}.count"));
    }
}
