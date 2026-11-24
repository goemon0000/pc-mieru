using PcMieru.Update.Launch;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Feed;

/// <summary>
/// A GUI hand-off is counted before launching the runner. The id names this attempt in last-result.json even when the runner aborts before preparation.
/// Permanent failures are kept by build and hash so a later feed check cannot stage the same build again.
/// </summary>
public sealed record UpdateAttempt(string Version, string BuildId, string ManifestSha256, string Id, DateTimeOffset Utc, int Count,
    string? CheckGeneration, ApplyRefusal? Refusal = null, bool Permanent = false, bool Shown = false);

public enum UpdateAttemptFailure { Waiting, Permanent }

public readonly record struct HandOffDecision(bool MayStart, ApplyRefusal? Refusal = null, UpdateAttemptFailure? Failure = null, string? AttemptId = null);

/// <summary>Keeps a check or another GUI from replacing the stage or the attempt while the runner is deciding whether to take it.</summary>
public sealed class UpdateAttemptLease(string id, IDisposable held, Action<ApplyRefusal> refuse) : IDisposable
{
    public string Id { get; } = id;

    public void Refused(ApplyRefusal reason) => refuse(reason);

    public void Dispose() => held.Dispose();
}

public sealed partial class UpdateService
{
    private UpdateAttempt? _handOffRefusal;

    private bool IsBlocked(AppVersion version) =>
        _options.BlockedTags.Contains(version.Tag, StringComparer.Ordinal)
        || (_options.InstallFolder is { } install && BlockedTags.Read(new TxPaths(install).Blocked).Any(tag => tag.Version == version.ToString()));

    private static UpdateAttempt? FindAttempt(UpdateState state, string build, string hash) =>
        state.HandOffAttempts?.Find(attempt => attempt.BuildId == build && attempt.ManifestSha256 == hash);

    private static void Remember(UpdateState state, UpdateAttempt attempt)
    {
        state.HandOffAttempts ??= [];
        state.HandOffAttempts.RemoveAll(item => item.BuildId == attempt.BuildId && item.ManifestSha256 == attempt.ManifestSha256);
        state.HandOffAttempts.Add(attempt);
    }

    /// <inheritdoc />
    public HandOffDecision InspectHandOff()
    {
        var read = _store.Read();
        if (read.Staged is null && read.HandOffAttempts is not { Count: > 0 })
        {
            return new HandOffDecision(true);
        }

        if (_store.TryLock(out var held) != LockOutcome.Acquired)
        {
            return new HandOffDecision(false);
        }

        using (held)
        {
            return Reconcile(_store.Read());
        }
    }

    /// <inheritdoc />
    public UpdateAttemptLease? BeginHandOff(StagedUpdate stage)
    {
        if (_store.TryLock(out var held) != LockOutcome.Acquired)
        {
            return null;
        }

        try
        {
            var state = _store.Read();
            if (state.ToStatus(_options.StateFolder).Staged != stage || !Reconcile(state).MayStart)
            {
                return null;
            }

            var previous = FindAttempt(state, stage.BuildId, stage.ManifestSha256);
            var attempt = new UpdateAttempt(stage.Version, stage.BuildId, stage.ManifestSha256,
                TxPaths.NewTx(), _options.Clock.GetUtcNow(), (previous?.Count ?? 0) + 1, state.CheckGeneration);
            Remember(state, attempt);
            if (!_store.TryWrite(state))
            {
                return null;
            }

            var lease = new UpdateAttemptLease(attempt.Id, held!, reason =>
            {
                // These refusals were answered before acceptance. They do not spend a swap attempt, and can be reconsidered when the check or build changes.
                if (reason is ApplyRefusal.StageChanged or ApplyRefusal.NotNewer or ApplyRefusal.NotAutomatic or ApplyRefusal.Blocked or ApplyRefusal.StageNotFound)
                {
                    var refused = attempt with { Refusal = reason, Count = attempt.Count - 1, Permanent = reason == ApplyRefusal.Blocked };
                    _handOffRefusal = refused;
                    Remember(state, refused);
                    if (reason == ApplyRefusal.Blocked)
                    {
                        DiscardStage(state);
                    }
                    else
                    {
                        _store.TryWrite(state);
                    }
                }
            });
            held = null; // the lease now owns the lock
            return lease;
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>Called under check.lock; only a matching transaction can classify a recorded attempt.</summary>
    private HandOffDecision Reconcile(UpdateState state)
    {
        var cleared = state.HandOffAttempts?.RemoveAll(attempt => AppVersion.TryParse(attempt.Version, out var version) && version <= _options.Running) > 0;
        var stage = state.ToStatus(_options.StateFolder).Staged;
        if (stage is not null && AppVersion.TryParse(stage.Version, out var installed) && installed <= _options.Running)
        {
            DiscardStage(state);
            stage = null;
        }
        else if (cleared)
        {
            _store.TryWrite(state);
        }

        if (stage is null)
        {
            return PermanentNotice(state, mayStart: true);
        }

        var attempt = FindAttempt(state, stage.BuildId, stage.ManifestSha256);
        if (attempt is { Refusal: null } && _handOffRefusal is { } inMemory && attempt.Id == inMemory.Id
            && (inMemory.Permanent || inMemory.CheckGeneration == state.CheckGeneration))
        {
            attempt = inMemory;
        }
        var blocked = AppVersion.TryParse(stage.Version, out var stagedVersion) && IsBlocked(stagedVersion);
        if (attempt?.Permanent == true || blocked)
        {
            return RejectBuild(state, stage, attempt);
        }

        if (attempt is null)
        {
            return PermanentNotice(state, mayStart: true);
        }

        if (attempt.Refusal is { } refusal && attempt.CheckGeneration == state.CheckGeneration)
        {
            return new HandOffDecision(false, refusal);
        }

        if (attempt.Refusal is not null)
        {
            return PermanentNotice(state, mayStart: true);
        }

        var reason = _options.InstallFolder is { } install
            ? LastResult.ReadAttemptReason(new TxPaths(install).LastResult, attempt.Id, stage.Version, stage.BuildId) : null;
        var transient = reason is null or "diskFull" or "filesInUse" or "busy" or "swapIoFailure" or "ioFailure";
        if (!transient || attempt.Count >= 3)
        {
            return RejectBuild(state, stage, attempt);
        }

        var delay = TimeSpan.FromHours(attempt.Count == 1 ? 1 : 6);
        var mayStart = _options.Clock.GetUtcNow() - attempt.Utc >= delay;
        return PermanentNotice(state, mayStart) is { Failure: not null } notice
            ? notice : new HandOffDecision(mayStart, Failure: mayStart ? null : UpdateAttemptFailure.Waiting, AttemptId: attempt.Id);
    }

    private HandOffDecision RejectBuild(UpdateState state, StagedUpdate stage, UpdateAttempt? attempt)
    {
        attempt ??= new UpdateAttempt(stage.Version, stage.BuildId, stage.ManifestSha256,
            TxPaths.NewTx(), _options.Clock.GetUtcNow(), 0, state.CheckGeneration);
        Remember(state, attempt with { Permanent = true });
        DiscardStage(state);
        return PermanentNotice(state, mayStart: false);
    }

    private static HandOffDecision PermanentNotice(UpdateState state, bool mayStart) =>
        state.HandOffAttempts?.FindLast(attempt => attempt.Permanent && !attempt.Shown) is { } pending
            ? new HandOffDecision(mayStart, Failure: UpdateAttemptFailure.Permanent, AttemptId: pending.Id)
            : new HandOffDecision(mayStart);

    /// <inheritdoc />
    public void MarkHandOffFailureShown(string attemptId)
    {
        if (_store.TryLock(out var held) != LockOutcome.Acquired)
        {
            return;
        }

        using (held)
        {
            var state = _store.Read();
            if (state.HandOffAttempts?.Find(attempt => attempt.Id == attemptId && attempt.Permanent && !attempt.Shown) is { } attempt)
            {
                Remember(state, attempt with { Shown = true });
                _store.TryWrite(state);
            }
        }
    }
}
