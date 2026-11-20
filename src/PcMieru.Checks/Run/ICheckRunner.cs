namespace PcMieru.Checks;

/// <summary>
/// Runs one check (DESIGN-A §2). The GUI and the CLI take this type; a check kind plugs in behind it (<see cref="CheckRunner"/>).
/// </summary>
public interface ICheckRunner
{
    /// <summary>
    /// Runs the check and saves its result. A started run is always saved, also when it was stopped or failed. The result's
    /// <see cref="CheckResult.Outcome"/> is <c>stopped</c> when the token was cancelled before the result was final (what was found by then
    /// is kept), <c>failed</c> when the run as a whole could not be done, <c>completed</c> otherwise: a missing individual reading does
    /// not make a run failed. The token is not passed to the save. A save that fails does not take the result away:
    /// <see cref="CheckRun.Save"/> says what happened. Throws <see cref="ArgumentException"/> for a request that is not valid,
    /// <see cref="NotSupportedException"/> for a kind this runner has no check for, and <see cref="OperationCanceledException"/> when the
    /// token was already cancelled on the way in: none of these is a started run, and nothing is saved.
    /// </summary>
    Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct);
}
