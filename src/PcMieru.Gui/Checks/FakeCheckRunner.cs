using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// The runner of <c>--fakes</c>: it measures nothing and touches nothing (no disk, no WMI, no load, no administrator prompt). It reports a few
/// steps the way a real run does, so the screen can be seen working, and answers with a valid sample result that is "saved" into the in-memory
/// history. A load takes the length of its request on the screen but only about a second in fact: the reported seconds are scaled, the work is not.
/// A stop is answered with a stopped result, saved too, like the real runner does.
/// </summary>
internal sealed class FakeCheckRunner : ICheckRunner, IFakeImplementation
{
    private const int LoadSteps = 8;

    private readonly TimeSpan _step;
    private readonly Action<CheckResult>? _saved;

    /// <param name="step">How long one reported step takes (zero: no wait at all).</param>
    /// <param name="saved">Told about each result that was "saved" (the in-memory history takes it).</param>
    public FakeCheckRunner(TimeSpan? step = null, Action<CheckResult>? saved = null)
    {
        _step = step ?? TimeSpan.FromMilliseconds(180);
        _saved = saved;
    }

    public async Task<CheckRun> RunAsync(CheckRequest request, IProgress<CheckProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();
        ct.ThrowIfCancellationRequested();

        var runId = Guid.NewGuid();
        var start = CheckTime.TruncateToMilliseconds(DateTimeOffset.UtcNow);
        var isLoad = request.Kind is CheckKind.Cpu or CheckKind.Memory;
        var target = isLoad ? (request.Duration ?? TimeSpan.FromSeconds(30)).TotalSeconds : 0;

        progress?.Report(new CheckProgress(runId, CheckStage.Starting, 0, target));
        CheckResult result;
        try
        {
            if (isLoad)
            {
                for (var i = 1; i <= LoadSteps; i++)
                {
                    await Wait(ct).ConfigureAwait(false);
                    var elapsed = target * i / LoadSteps;
                    progress?.Report(request.Kind == CheckKind.Cpu
                        ? new CheckProgress(runId, CheckStage.Loading, elapsed, target, 38 + (i % 3), "chunks/s")
                        : new CheckProgress(runId, CheckStage.Loading, elapsed, target, 5200 + (i * 40), "MiB/s"));
                }
            }
            else
            {
                progress?.Report(new CheckProgress(runId, CheckStage.Collecting, 0, 0));
                await Wait(ct).ConfigureAwait(false);
                await Wait(ct).ConfigureAwait(false);
            }

            progress?.Report(new CheckProgress(runId, CheckStage.Judging, target, target));
            result = FakeCheckData.For(request, runId, start, target);
        }
        catch (OperationCanceledException)
        {
            result = FakeCheckData.Stopped(request.Kind, runId, start, isLoad ? Math.Max(1, target / 4) : 0);
        }

        progress?.Report(new CheckProgress(runId, CheckStage.Saving, target, target));
        _saved?.Invoke(result);
        var run = new CheckRun(result, CheckSaveState.Saved($"fake-{runId:N}.json"));
        progress?.Report(new CheckProgress(runId, CheckStage.Done, target, target));
        return run;
    }

    private Task Wait(CancellationToken ct) => _step <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(_step, ct);
}
