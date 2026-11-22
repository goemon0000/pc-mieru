using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// One finished run in the pcm-check/1 format (DESIGN-A §3). Immutable. <see cref="Counts"/> and
/// <see cref="Verdict"/> are functions of the items (<see cref="CheckCounts.From"/>, <see cref="CheckVerdicts.Overall"/>):
/// build a result with <see cref="Create"/> so they cannot drift; a stored result that disagrees is rejected on read.
/// The JSON is written and read by a strict, hand-written converter (<see cref="CheckResultJsonConverter"/>).
/// </summary>
[JsonConverter(typeof(CheckResultJsonConverter))]
public sealed record CheckResult(
    string Format,
    string App,
    Guid RunId,
    CheckKind Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    CheckOutcome Outcome,
    CheckVerdict Verdict,
    CheckCounts Counts,
    IReadOnlyList<CheckItem> Items,
    IReadOnlyList<string> Notes)
{
    /// <summary>The only format string this assembly reads and writes.</summary>
    public const string FormatId = "pcm-check/1";

    /// <summary>Counts and the overall verdict are computed from the items.</summary>
    public static CheckResult Create(
        string app,
        Guid runId,
        CheckKind kind,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CheckOutcome outcome,
        IReadOnlyList<CheckItem> items,
        IReadOnlyList<string>? notes = null) =>
        new(
            FormatId,
            app,
            runId,
            kind,
            startedAtUtc,
            endedAtUtc,
            outcome,
            CheckVerdicts.Overall(items),
            CheckCounts.From(items),
            items,
            notes ?? []);
}
