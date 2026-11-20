using PcMieru.Checks.Load;
using PcMieru.Checks.Storage;
using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks;

/// <summary>
/// The run framework (DESIGN-A §2): one runner for every kind of check. A kind plugs in as an <see cref="ICheckProcedure"/>; the runner
/// owns everything that must be the same for all of them: the run's ID and times, the notifications, the single finalisation point (the
/// outcome, the result, the strict validation) and the save.
/// <list type="bullet">
/// <item>The outcome: the token cancelled before the result is final gives <c>stopped</c> (the rows found by then are kept); a failure of
/// the run as a whole gives <c>failed</c>; everything else is <c>completed</c>, with the missing individual readings as rows of their own.</item>
/// <item>A started run is saved whatever its outcome. The save gets no token (a final result is written even after a stop). A save that fails is
/// reported in <see cref="CheckRun.Save"/> and the result stays.</item>
/// <item>The result is validated with the strict <see cref="CheckResultValidator"/> before anything is written. A result that does not pass is
/// a bug of the writer: only an empty failed result with the code <see cref="CheckRunFailureCodes.ResultInvalid"/> is saved in its place. The
/// invalid one is never saved, but it is returned on <see cref="CheckRun.UnsavedResult"/> (with the failure code the run had before, and the
/// first problem) so the screen can still show what was measured.</item>
/// </list>
/// </summary>
public sealed class CheckRunner : ICheckRunner
{
    /// <summary>How long a finished run waits for the caller's progress handler to take the last notifications. The result and its save are done by then.</summary>
    internal static readonly TimeSpan ProgressPatience = TimeSpan.FromMilliseconds(250);

    private readonly CheckServices _services;
    private readonly Dictionary<CheckKind, ICheckProcedure> _procedures;

    /// <summary>The runner of these services: the checks this build has (storage, system and CPU; memory follows in a later step).</summary>
    public CheckRunner(CheckServices services)
        : this(services, [new StorageCheckProcedure(), new SystemCheckProcedure(), new CpuCheckProcedure()])
    {
    }

    internal CheckRunner(CheckServices services, IEnumerable<ICheckProcedure> procedures)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(procedures);
        _services = services;
        _procedures = procedures.ToDictionary(static p => p.Kind);
    }

    /// <summary>The kinds this runner can run.</summary>
    public IReadOnlyCollection<CheckKind> SupportedKinds => _procedures.Keys;

    /// <remarks>
    /// The returned task completes after the result is final and saved, but it may then wait up to <see cref="ProgressPatience"/> (250 ms) for
    /// the progress handler to take the last notifications: a caller that times a run sees that wait after the save, never before it.
    /// </remarks>
    public async Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        if (!_procedures.TryGetValue(request.Kind, out var procedure))
        {
            throw new NotSupportedException($"There is no {request.Kind.ToString().ToLowerInvariant()} check in this build.");
        }

        ct.ThrowIfCancellationRequested();

        // From here the run exists: it ends in a result, and the result is saved.
        var runId = _services.RunIds();
        var startedUtc = CheckTime.TruncateToMilliseconds(_services.Time.GetUtcNow());
        var pump = new CheckProgressPump(progress);
        try
        {
            var collector = new CheckCollector();
            var context = new CheckContext(_services, request, runId, startedUtc, collector, pump);
            context.Stage(CheckStage.Starting);

            var end = await ExecuteAsync(procedure, context, ct).ConfigureAwait(false);
            var final = Finalize(runId, request.Kind, startedUtc, collector, end);

            context.Stage(CheckStage.Saving);
            var save = await SaveAsync(final.Result).ConfigureAwait(false);
            context.Stage(CheckStage.Done);
            return new CheckRun(final.Result, save, final.FailureCode, final.FailureDetail, final.UnsavedResult, final.OriginalFailureCode, final.FirstProblem);
        }
        finally
        {
            await pump.CompleteAsync(ProgressPatience).ConfigureAwait(false);
        }
    }

    // How the procedure ended. A cancellation of the caller's token is a stop; any other exception is a failure of the run.
    private static async Task<ProcedureEnd> ExecuteAsync(ICheckProcedure procedure, CheckContext context, CancellationToken ct)
    {
        try
        {
            await procedure.ExecuteAsync(context, ct).ConfigureAwait(false);

            // The seal point (DESIGN-A 2): the procedure has returned its rows. A procedure that returned is stopped only when it says so
            // (MarkStopped, with its rows made partial); the token is NOT read here. A procedure that returned without saying so has whole rows,
            // so a stop that came while it was finishing, or after it returned, does not make it "stopped with whole rows" (which could then be
            // ok overall). A procedure that throws on the stop (storage, system) ends below as stopped.
            var stopped = context.Collector.StoppedByProcedure;
            return new ProcedureEnd(stopped ? CheckOutcome.Stopped : CheckOutcome.Completed, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ProcedureEnd(CheckOutcome.Stopped, null, null);
        }
        catch (CheckRunFailedException failure)
        {
            return new ProcedureEnd(CheckOutcome.Failed, failure.Code, failure.Detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ProcedureEnd(CheckOutcome.Failed, CheckRunFailureCodes.InternalError, Describe(ex));
        }
    }

    /// <summary>
    /// The one place a result is made final (DESIGN-A §2). The outcome was decided at the seal point (see <see cref="ExecuteAsync"/>) and is not
    /// read again here: a stop that arrives after the procedure has returned its rows does not touch it. The result is validated strictly; one
    /// that does not pass is replaced for the save by an empty failed one, and is kept (unsaved) with the failure code the run had and the first
    /// problem.
    /// </summary>
    private FinalResult Finalize(Guid runId, CheckKind kind, DateTimeOffset startedUtc, CheckCollector collector, ProcedureEnd end)
    {
        var outcome = end.Outcome;
        var endedUtc = CheckTime.TruncateToMilliseconds(_services.Time.GetUtcNow());
        if (endedUtc < startedUtc)
        {
            endedUtc = startedUtc; // a clock that went back during the run: the run did not take negative time
        }

        try
        {
            var result = CheckResult.Create(_services.App, runId, kind, startedUtc, endedUtc, outcome, [.. collector.Items], [.. collector.Notes]);
            var problems = CheckResultValidator.Validate(result);
            if (problems.Count == 0)
            {
                return new FinalResult(result, end.FailureCode, end.FailureDetail);
            }

            var firstProblem = Truncate(problems[0]);
            return Replacement(runId, kind, startedUtc, endedUtc, CheckRunFailureCodes.ResultInvalid, firstProblem, result, end.FailureCode, firstProblem);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The result could not even be built, so there is nothing to keep but the code the run had.
            return Replacement(runId, kind, startedUtc, endedUtc, CheckRunFailureCodes.InternalError, Describe(ex), null, end.FailureCode, null);
        }
    }

    // The empty failed result that stands in for one that could not be made or did not pass: no rows, no notes, valid by construction.
    // The original (when there is one) rides along on the run, unsaved.
    private FinalResult Replacement(
        Guid runId,
        CheckKind kind,
        DateTimeOffset startedUtc,
        DateTimeOffset endedUtc,
        string code,
        string detail,
        CheckResult? unsaved,
        string? originalFailureCode,
        string? firstProblem) =>
        new(CheckResult.Create(_services.App, runId, kind, startedUtc, endedUtc, CheckOutcome.Failed, [], []), code, detail, unsaved, originalFailureCode, firstProblem);

    private async Task<CheckSaveState> SaveAsync(CheckResult result)
    {
        try
        {
            // No token: a stopped run is saved, and the user's stop does not reach a write after the result is final.
            return await _services.History.SaveAsync(result).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The store never throws; a replacement that does is still a failed save, not a lost result.
            return CheckSaveState.Failed(CheckSaveFailureCodes.IoError, Describe(ex));
        }
    }

    /// <summary>The exception's type and message, cut short. Messages of the code under test carry no path, serial number or ID.</summary>
    internal static string Describe(Exception ex) => Truncate($"{ex.GetType().Name}: {ex.Message}");

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private readonly record struct ProcedureEnd(CheckOutcome Outcome, string? FailureCode, string? FailureDetail);

    private readonly record struct FinalResult(
        CheckResult Result,
        string? FailureCode,
        string? FailureDetail,
        CheckResult? UnsavedResult = null,
        string? OriginalFailureCode = null,
        string? FirstProblem = null);
}
