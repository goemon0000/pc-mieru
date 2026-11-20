namespace PcMieru.Checks;

/// <summary>Why a run is <see cref="CheckOutcome.Failed"/> (the reason is on the <see cref="CheckRun"/>; the result itself has no reason field). A fixed list.</summary>
public static class CheckRunFailureCodes
{
    /// <summary>The storage inspection could not be done at all (the source threw something that is not a stop).</summary>
    public const string InspectionFailed = "inspection_failed";

    /// <summary>The result the check built broke a pcm-check/1 rule. This is a bug of the writer, never of the machine: nothing invalid is saved, an empty failed result is saved in its place.</summary>
    public const string ResultInvalid = "result_invalid";

    /// <summary>Anything else that was not expected.</summary>
    public const string InternalError = "internal_error";

    public static IReadOnlyList<string> All { get; } = [InspectionFailed, ResultInvalid, InternalError];
}

/// <summary>
/// What a run gives back (DESIGN-A §2): the final <see cref="Result"/> and, separately, how its save went. The measurement's
/// <see cref="CheckResult.Outcome"/> and the save are two different things: a result that could not be saved is still the result
/// (<see cref="Save"/> says what happened, the screen keeps the result). <see cref="FailureCode"/> and <see cref="FailureDetail"/> are set
/// when the run failed: a <see cref="CheckRunFailureCodes"/> code and a short note without paths, serial numbers or IDs.
/// </summary>
public sealed record CheckRun(CheckResult Result, CheckSaveState Save, string? FailureCode = null, string? FailureDetail = null)
{
    /// <summary>The file is committed to the history.</summary>
    public bool IsSaved => Save.Status == CheckSaveStatus.Saved;
}
