namespace PcMieru.Checks;

/// <summary>Why a run is <see cref="CheckOutcome.Failed"/> (the reason is on the <see cref="CheckRun"/>; the result itself has no reason field). A fixed list.</summary>
public static class CheckRunFailureCodes
{
    /// <summary>The storage inspection could not be done at all (the source threw something that is not a stop).</summary>
    public const string InspectionFailed = "inspection_failed";

    /// <summary>
    /// The result the check built broke a pcm-check/1 rule. This is a bug of the writer, never of the machine: nothing invalid is saved, an empty
    /// failed result is saved in its place. The result that broke the rule is not lost: it is on <see cref="CheckRun.UnsavedResult"/>, for the screen.
    /// </summary>
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
/// <para>
/// A result that did not pass the strict validation (a bug of the writer, <see cref="CheckRunFailureCodes.ResultInvalid"/>) is not saved: the
/// empty failed result is <see cref="Result"/> and is the only one saved. What the check actually built stays on <see cref="UnsavedResult"/>
/// so the screen can still show it, with <see cref="OriginalFailureCode"/> and <see cref="FirstProblem"/> as the diagnosis.
/// </para>
/// </summary>
/// <param name="Result">The final result: the one that is saved, or was meant to be.</param>
/// <param name="Save">How the save of <paramref name="Result"/> went.</param>
/// <param name="FailureCode">A <see cref="CheckRunFailureCodes"/> code when the run failed, else null.</param>
/// <param name="FailureDetail">A short note without paths, serial numbers or IDs, with <paramref name="FailureCode"/>.</param>
/// <param name="UnsavedResult">
/// Set only when the strict validation rejected what the check built: that original result, never saved and possibly invalid (it broke a
/// rule, so it may hold values a reader would refuse). For display and diagnosis only. Null when nothing was rejected, and when the result
/// could not even be built.
/// </param>
/// <param name="OriginalFailureCode">
/// The failure code the run had before the replacement (for example <c>inspection_failed</c> when the procedure failed and the result it left
/// did not validate either). Null when the run had not failed. Set only together with a replacement; otherwise see <paramref name="FailureCode"/>.
/// </param>
/// <param name="FirstProblem">The first problem the validator found in <paramref name="UnsavedResult"/>, cut short. Null when there is no unsaved result.</param>
public sealed record CheckRun(
    CheckResult Result,
    CheckSaveState Save,
    string? FailureCode = null,
    string? FailureDetail = null,
    CheckResult? UnsavedResult = null,
    string? OriginalFailureCode = null,
    string? FirstProblem = null)
{
    /// <summary>The file is committed to the history.</summary>
    public bool IsSaved => Save.Status == CheckSaveStatus.Saved;
}
