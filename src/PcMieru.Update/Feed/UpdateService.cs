// Provenance: the flow of Updater.CheckAsync of the owner's pl-cloud src/PartsLife.Update/Updater.cs (Parts Life, MIT, same owner; see NOTICE.txt), rebuilt for this
// repo: it asks for the release, SHA256.txt and the outer manifest and stops there (no zip). What is new here: the mode gate is the first line; the check is serialised
// by check.lock; a refusal says which rule held (UpdateRefusal); the candidate must be newer than both the running and the installed version; the comments are in English.
using System.Security.Cryptography;
using System.Text;
using PcMieru.Update.Bundle;
using PcMieru.Update.Staging;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Feed;

/// <summary>What the rest of the app asks of the update feed (DESIGN-B 2). The GUI holds this; a smoke run holds a fake.</summary>
public interface IUpdateService
{
    /// <summary>
    /// One check. In <see cref="UpdateMode.Ask"/> and <see cref="UpdateMode.Off"/> it returns "not allowed" without touching anything (no client, no file, no
    /// network). <paramref name="manual"/> skips "not due yet" but not a rate limit. A cancelled <paramref name="ct"/> throws OperationCanceledException.
    /// </summary>
    Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct);

    /// <summary>What the state file says (last check, wait, last candidate, the staged update). Reads a local file; asks nobody.</summary>
    UpdateStatus ReadStatus();

    /// <summary>
    /// Downloads and unpacks the zip of <paramref name="candidate"/> into this install's cache (DESIGN-B 3.3), and checks it in full. It does something ONLY when <paramref name="mode"/> is
    /// <see cref="UpdateMode.Automatic"/>, this install can take a swap, and the candidate is one <see cref="CheckAsync"/> validated; in every other case it returns
    /// <see cref="UpdateStageKind.NotAttempted"/> and sends nothing. A fake service never stages.
    /// </summary>
    Task<UpdateStageResult> StageAsync(UpdateMode mode, UpdateCheck candidate, CancellationToken ct) =>
        Task.FromResult(UpdateStageResult.NotAttempted("this service does not stage"));

    /// <summary>
    /// After a start: forgets what the install has outgrown (a candidate, a stage or a refusal that is not newer than <paramref name="installed"/>) and removes the stage folders that are no longer wanted
    /// (the one a commit just used, the ones an earlier stage left). Asks nobody; does nothing when another check holds the lock.
    /// </summary>
    void Tidy(AppVersion installed)
    {
    }

    /// <summary>Remembers that the result with this transaction id (12 hex) has been shown, so that it is shown once.</summary>
    void MarkResultShown(string tx)
    {
    }

    /// <summary>Reconciles the persisted attempt with the running version and the runner's last result.</summary>
    HandOffDecision InspectHandOff() => new(true);

    /// <summary>Records a hand-off before the runner can start, holding the check lock until its answer.</summary>
    UpdateAttemptLease? BeginHandOff(StagedUpdate stage) => null;

    /// <summary>Acknowledges the permanent failure when the settings page displays it.</summary>
    void MarkHandOffFailureShown(string attemptId)
    {
    }

    /// <summary>Forgets the staged update and removes its folder (the runner refused it, or it can no longer be trusted): the next check stages it again.</summary>
    void DiscardStage()
    {
    }
}

/// <summary>What <see cref="UpdateService"/> needs to know.</summary>
public sealed class UpdateServiceOptions
{
    /// <summary>The version of this program.</summary>
    public required AppVersion Running { get; init; }

    /// <summary>
    /// The version of the install (step 4 passes installed.json's). A candidate must be newer than this one too. Null: the running version (nothing is installed
    /// apart from what runs).
    /// </summary>
    public AppVersion? Installed { get; init; }

    /// <summary>L\update\&lt;id&gt; (see <see cref="InstallId.StateFolder"/>).</summary>
    public required string StateFolder { get; init; }

    /// <summary>L, the root below which staging refuses reparse points. Standalone callers can use the state folder as their root.</summary>
    public string? LocalFolder { get; init; }

    /// <summary>Tags that must not be offered again (step 4 reads them from its blocked list). Compared to the tag as written, "v1.2.3".</summary>
    public IReadOnlyCollection<string> BlockedTags { get; init; } = [];

    /// <summary>Makes the transport of one check (disposed after it). Null: the real HTTP client, made only when a check really goes out.</summary>
    public Func<IFeedTransport>? TransportFactory { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// The install folder (<c>I</c>): where installed.json is and what <see cref="UpdateService.StageAsync"/> asks <see cref="PlacementOf"/> about. Null: no stage (nothing can be swapped in).
    /// </summary>
    public string? InstallFolder { get; init; }

    /// <summary>Whether <see cref="InstallFolder"/> can take a swap. Null: <see cref="InstallPlacement.Check(string)"/>. A test gives its own answer.</summary>
    public Func<string, Placement>? PlacementOf { get; init; }

    /// <summary>What looks at the signature of a staged program file. Null: <see cref="WindowsSignatureInspector"/>.</summary>
    public ISignatureInspector? SignatureInspector { get; init; }

    /// <summary>What says how much room the volume has. Null: the real one.</summary>
    public IDiskSpace? DiskSpace { get; init; }

    /// <summary>
    /// How long a MANUAL check waits for check.lock when another check holds it (the one that Off just cancelled has not let go yet, or another window is just
    /// finishing). A scheduled tick never waits: it gives way at once. Zero: do not wait.
    /// </summary>
    public TimeSpan ManualLockWait { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The check of the feed (DESIGN-B 3.1). Order of the rules: the mode first (nothing else is looked at in Ask / Off), then check.lock, then the wait after a rate limit
/// (a manual check waits too), then "not due yet" (a scheduled check only), then the one write that says "I looked now", then the requests: the API, SHA256.txt and the
/// outer manifest. <see cref="CheckAsync"/> never asks for the zip; <see cref="StageAsync"/> does, in Automatic mode only (DESIGN-B 3.3).
/// </summary>
public sealed partial class UpdateService : IUpdateService
{
    private readonly UpdateServiceOptions _options;
    private readonly UpdateStateStore _store;

    public UpdateService(UpdateServiceOptions options)
    {
        _options = options;
        _store = new UpdateStateStore(options.StateFolder);
    }

    public UpdateStatus ReadStatus()
    {
        var status = _store.Read().ToStatus(_options.StateFolder);
        return status.CandidateVersion is { } candidate && IsBlocked(candidate)
            ? status with { CandidateVersion = null, CandidatePageUrl = null, Staged = null, LastRefusal = UpdateRefusal.BlockedTag, RefusedVersion = candidate }
            : status;
    }

    public async Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct)
    {
        if (!UpdateModes.MayUseNetwork(mode))
        {
            return UpdateCheck.NotAllowed(mode);
        }

        ct.ThrowIfCancellationRequested();

        // A manual check waits a moment for the lock (Off, then Notify, then "check now" in quick succession: the cancelled check is still unwinding); a tick does not.
        var (taken, held) = await _store.TryLockAsync(manual ? _options.ManualLockWait : TimeSpan.Zero, ct).ConfigureAwait(false);
        switch (taken)
        {
            case LockOutcome.Busy:
                return UpdateCheck.Refuse(UpdateRefusal.AnotherCheckRunning, "check.lock is held", fetched: false);
            case LockOutcome.Unavailable:
                return UpdateCheck.Refuse(UpdateRefusal.StateUnavailable, "check.lock cannot be taken", fetched: false);
        }

        using (held)
        {
            var now = _options.Clock.GetUtcNow();
            var state = _store.Read();
            if (UpdateSchedule.InBackoff(state, now))
            {
                return UpdateCheck.Wait(state.BackoffUntilUtc!.Value, fetched: false);
            }

            if (!manual && !UpdateSchedule.IsDue(state, now))
            {
                return UpdateCheck.NoCandidate(fetched: false);
            }

            // "I looked now" is written before the request: a crash or a hang in the request must not make the next start look again at once.
            state.LastCheckUtc = now;
            state.CheckGeneration = Guid.NewGuid().ToString("N");
            state.LastRefusal = null;
            if (!_store.TryWrite(state))
            {
                return UpdateCheck.Refuse(UpdateRefusal.StateUnavailable, "state.json cannot be written", fetched: false);
            }

            UpdateCheck result;
            AppVersion? read = null; // the version of the release once its answer was parsed, for a refusal that comes from a later request
            try
            {
                result = await FetchAsync(ct, v => read = v).ConfigureAwait(false);
            }
            catch (RateLimitedException limited)
            {
                var until = UpdateSchedule.BackoffUntil(now, limited.RetryAfter, limited.Reset);
                state.BackoffUntilUtc = until;
                _store.TryWrite(state);
                return UpdateCheck.Wait(until, fetched: true);
            }
            catch (UpdateHttpException refused)
            {
                result = UpdateCheck.Refuse(refused.Reason, refused.Message, fetched: true, UpdateRefusals.JudgedARelease(refused.Reason) ? read : null);
            }

            // What is remembered. A failure that judged no release (UpdateRefusals.IsOperational: no answer, an unreadable or cut-off answer, a redirect somewhere else,
            // this PC's own state) learned nothing about the release, so the candidate an earlier check found STAYS. Only an outcome that did judge one replaces it:
            // a candidate, "nothing newer" after the answer was parsed, or a refusal of the release's own rules.
            var operational = result is { Kind: UpdateCheckKind.Refused } && UpdateRefusals.IsOperational(result.Refusal);
            state.BackoffUntilUtc = null;
            if (!operational)
            {
                state.LatestVersion = result.Kind == UpdateCheckKind.Candidate ? result.Version!.Value.ToString() : null;
                state.LatestPageUrl = result.Kind == UpdateCheckKind.Candidate ? result.PageUrl : null;
            }

            state.LastRefusal = result.Kind == UpdateCheckKind.Refused ? result.Refusal.ToString() : null;
            state.RefusedVersion = result is { Kind: UpdateCheckKind.Refused, RefusedVersion: { } refusedVersion } ? refusedVersion.ToString() : null;

            // A release that is no longer offered (withdrawn, blocked, replaced by "nothing newer") is not applied: its stage goes. An operational failure learned nothing, so a stage stays.
            if (!operational && state.Staged is { } stale && !(result.Kind == UpdateCheckKind.Candidate && result.Version is { } offered && stale.Tag == offered.Tag))
            {
                state.Staged = null;
                StageCache.Clear(_options.StateFolder, localFolder: _options.LocalFolder);
            }

            _store.TryWrite(state);
            return result;
        }
    }

    private async Task<UpdateCheck> FetchAsync(CancellationToken ct, Action<AppVersion> onVersionRead)
    {
        using var transport = _options.TransportFactory?.Invoke() ?? new UpdateHttp(_options.Running.ToString(), clock: _options.Clock);

        var json = await transport.GetLatestReleaseJsonAsync(ct).ConfigureAwait(false);
        if (!ReleaseParser.TryParse(json, out var release, out var why, out var detail))
        {
            return Refused(why, detail);
        }

        onVersionRead(release!.Version);

        // Newer than BOTH the running and the installed version. Same or older is not an error: there is just nothing to offer.
        var floor = _options.Installed is { } installed && installed > _options.Running ? installed : _options.Running;
        if (release.Version <= floor)
        {
            return UpdateCheck.NoCandidate(fetched: true);
        }

        // From here on the release has been read, so a refusal says which version it is about.
        UpdateCheck RefusedV(UpdateRefusal reason, string text) => UpdateCheck.Refuse(reason, text, fetched: true, release.Version);

        if (IsBlocked(release.Version))
        {
            return RefusedV(UpdateRefusal.BlockedTag, release.Tag + " is on the blocked list");
        }

        if (!ReleaseParser.TrySelect(release, out var assets, out why, out detail))
        {
            return RefusedV(why, detail);
        }

        var version = release.Version.ToString();

        // SHA256.txt first: it names the hashes of the other two. Its own API digest (if any) is checked against the bytes that came.
        var listBytes = await transport.GetSmallAssetAsync(
            UpdateEndpoints.AssetUrl(release.Tag, assets!.HashList.Name), assets.HashList.Size, Sha256List.MaxBytes, ct).ConfigureAwait(false);
        if (!ReleaseParser.DigestAgrees(assets.HashList.Digest, Hex(listBytes)))
        {
            return RefusedV(UpdateRefusal.DigestMismatch, "SHA256.txt is not what the release's digest says");
        }

        var list = Sha256List.Parse(Encoding.UTF8.GetString(listBytes));
        if (list.For(assets.Zip.Name) is not { } zipHash || list.For(assets.Manifest.Name) is not { } manifestHash)
        {
            return RefusedV(UpdateRefusal.HashListLine, "SHA256.txt has no single line for the zip and for the manifest");
        }

        if (!ReleaseParser.DigestAgrees(assets.Zip.Digest, zipHash) || !ReleaseParser.DigestAgrees(assets.Manifest.Digest, manifestHash))
        {
            return RefusedV(UpdateRefusal.DigestMismatch, "a digest of the release differs from SHA256.txt");
        }

        var manifestBytes = await transport.GetSmallAssetAsync(
            UpdateEndpoints.AssetUrl(release.Tag, assets.Manifest.Name), assets.Manifest.Size, Limits.Default.ManifestBytes, ct).ConfigureAwait(false);
        if (!string.Equals(Hex(manifestBytes), manifestHash, StringComparison.Ordinal))
        {
            return RefusedV(UpdateRefusal.ManifestHashMismatch, "the manifest is not the one SHA256.txt names");
        }

        if (!BundleManifest.TryParse(manifestBytes, out var manifest, out var verdict))
        {
            return RefusedV(UpdateRefusal.ManifestInvalid, verdict.ToString());
        }

        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
        {
            return RefusedV(UpdateRefusal.ManifestVersionMismatch, $"the manifest is for {manifest.Version}, the tag for {version}");
        }

        // Everything the stage needs and nothing it must take on trust: the two small files as they were read and the numbers the zip is checked against. Only this code makes one.
        var validated = new ValidatedRelease
        {
            Tag = release.Tag,
            Version = release.Version,
            ZipUrl = UpdateEndpoints.AssetUrl(release.Tag, assets.Zip.Name),
            ZipSha256 = zipHash,
            ZipDeclaredBytes = assets.Zip.Size,
            ZipApiDigest = assets.Zip.Digest,
            HashListBytes = listBytes,
            HashListSha256 = Hex(listBytes),
            ManifestBytes = manifestBytes,
            ManifestSha256 = manifestHash,
            Manifest = manifest,
        };
        return UpdateCheck.FoundValidated(validated, release.PageUrl);
    }

    /// <inheritdoc />
    public async Task<UpdateStageResult> StageAsync(UpdateMode mode, UpdateCheck candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // Only Automatic downloads the zip: Notify, Ask and Off never do, whatever the candidate is.
        if (mode != UpdateMode.Automatic)
        {
            return UpdateStageResult.NotAttempted($"the mode is {mode}");
        }

        if (candidate.Kind != UpdateCheckKind.Candidate || candidate.Validated is not { } release)
        {
            return UpdateStageResult.NotAttempted("the candidate is not one a check validated");
        }

        if (_options.InstallFolder is not { } install)
        {
            return UpdateStageResult.NotAttempted("no install folder");
        }

        var placement = (_options.PlacementOf ?? InstallPlacement.Check)(install);
        if (!placement.CanSwap)
        {
            return UpdateStageResult.NotAttempted($"this install cannot take a swap: {placement.Kind}");
        }

        ct.ThrowIfCancellationRequested();

        // One stage at a time per install, and not while a check runs: the same lock (check.lock). A stage does not wait for it.
        var (taken, held) = await _store.TryLockAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        switch (taken)
        {
            case LockOutcome.Busy:
                return UpdateStageResult.Refuse(UpdateRefusal.AnotherCheckRunning, "check.lock is held");
            case LockOutcome.Unavailable:
                return UpdateStageResult.Refuse(UpdateRefusal.StateUnavailable, "check.lock cannot be taken");
        }

        using (held)
        {
            var state = _store.Read();

            if (IsBlocked(release.Version))
            {
                return UpdateStageResult.Refuse(UpdateRefusal.BlockedTag, "this version is on the blocked list");
            }

            if (FindAttempt(state, release.Manifest.BuildId, release.ManifestSha256) is { Permanent: true })
            {
                return UpdateStageResult.NotAttempted("an earlier hand-off permanently refused this build");
            }

            // A stage that is already here is checked again in full (every file, length and hash); if it is whole it is kept and nothing is downloaded.
            if (state.Staged is { } existing && existing.Tag == release.Tag && existing.BuildId == release.Manifest.BuildId
                && string.Equals(existing.ManifestSha256, release.ManifestSha256, StringComparison.Ordinal)
                && await Task.Run(() => StageCache.IsIntact(_options.StateFolder, existing, ct, _options.LocalFolder), ct).ConfigureAwait(false))
            {
                StageCache.Clear(_options.StateFolder, keep: existing.Folder, localFolder: _options.LocalFolder);
                return UpdateStageResult.Done(existing, reused: true);
            }

            // A zip that the release itself published and that is bad (its contents are not its manifest, a program file has a broken signature) is bad every time: it is not downloaded again until
            // another release, or another zip under the same tag (a changed SHA256.txt), is offered. A zip that is not the one SHA256.txt names may be a transfer that was cut or a release that is still
            // being uploaded, so that one is asked again at the next check (a day later), and so is every failure of this PC or the network.
            if (state.StageRefusedTag == release.Tag && state.StageRefusedZipSha256 == release.ZipSha256
                && Enum.TryParse<UpdateRefusal>(state.StageRefusedReason, ignoreCase: false, out var before) && IsRemembered(before))
            {
                return UpdateStageResult.Refuse(before, "refused by an earlier stage of this release");
            }

            // Old stage folders go before a new one is made (and a stage that is not wanted any more with them).
            StageCache.Clear(_options.StateFolder, localFolder: _options.LocalFolder);
            state.Staged = null;

            using var transport = _options.TransportFactory?.Invoke() ?? new UpdateHttp(_options.Running.ToString(), clock: _options.Clock);
            var stager = new UpdateStager(
                transport,
                _options.SignatureInspector ?? new WindowsSignatureInspector(),
                Limits.Default,
                _options.DiskSpace ?? SystemDiskSpace.Instance);

            StageRun run;
            try
            {
                run = await stager.RunAsync(release, _options.StateFolder, _options.LocalFolder ?? _options.StateFolder, RunnerHandOff.CurrentRecord(new TxPaths(install)), TxPaths.NewTx(), ct).ConfigureAwait(false);
            }
            catch (RateLimitedException limited)
            {
                state.BackoffUntilUtc = UpdateSchedule.BackoffUntil(_options.Clock.GetUtcNow(), limited.RetryAfter, limited.Reset);
                _store.TryWrite(state);
                return UpdateStageResult.Refuse(UpdateRefusal.HttpError, "rate limited");
            }
            catch (UpdateHttpException refused)
            {
                run = StageRun.Fail(refused.Reason, refused.Message);
            }

            if (run.Staged is { } staged)
            {
                state.Staged = staged;
                state.StageRefusedTag = state.StageRefusedReason = state.StageRefusedZipSha256 = null;
                if (!_store.TryWrite(state))
                {
                    StageCache.Remove(staged.Folder);
                    return UpdateStageResult.Refuse(UpdateRefusal.StateUnavailable, "state.json cannot be written");
                }

                return UpdateStageResult.Done(staged, reused: false);
            }

            if (IsRemembered(run.Reason))
            {
                state.StageRefusedTag = release.Tag;
                state.StageRefusedReason = run.Reason.ToString();
                state.StageRefusedZipSha256 = release.ZipSha256;
            }

            _store.TryWrite(state);
            return UpdateStageResult.Refuse(run.Reason, run.Detail);
        }
    }

    /// <inheritdoc />
    public void Tidy(AppVersion installed)
    {
        if (_store.TryLock(out var held) != LockOutcome.Acquired)
        {
            return; // somebody is checking or staging; the next start tidies
        }

        using (held)
        {
            var state = _store.Read();
            var changed = false;
            if (AppVersion.TryParse(state.LatestVersion, out var candidate) && candidate <= installed)
            {
                state.LatestVersion = state.LatestPageUrl = null;
                changed = true;
            }

            if (state.Staged is { } staged && !(AppVersion.TryParse(staged.Version, out var stagedVersion) && stagedVersion > installed && staged.IsWellFormedFor(_options.StateFolder)))
            {
                state.Staged = null;
                changed = true;
            }

            if (state.StageRefusedTag is not null && !(AppVersion.TryParse(state.StageRefusedTag, out var refusedVersion) && refusedVersion > installed))
            {
                state.StageRefusedTag = state.StageRefusedReason = state.StageRefusedZipSha256 = null;
                changed = true;
            }

            StageCache.Clear(_options.StateFolder, keep: state.Staged?.Folder, localFolder: _options.LocalFolder);
            if (changed)
            {
                _store.TryWrite(state);
            }
        }
    }

    /// <inheritdoc />
    public void MarkResultShown(string tx)
    {
        if (!TxPaths.IsValidTx(tx) || _store.TryLock(out var held) != LockOutcome.Acquired)
        {
            return;
        }

        using (held)
        {
            var state = _store.Read();
            if (state.ShownResultTx != tx)
            {
                state.ShownResultTx = tx;
                _store.TryWrite(state);
            }
        }
    }

    /// <inheritdoc />
    public void DiscardStage()
    {
        if (_store.TryLock(out var held) != LockOutcome.Acquired)
        {
            return;
        }

        using (held)
        {
            DiscardStage(_store.Read());
        }
    }

    private void DiscardStage(UpdateState state)
    {
        state.Staged = null;
        if (_store.TryWrite(state))
        {
            StageCache.Clear(_options.StateFolder, localFolder: _options.LocalFolder);
        }
    }

    /// <summary>The stage refusals that are remembered by tag and zip hash: the zip is what was published and is bad in itself.</summary>
    internal static bool IsRemembered(UpdateRefusal reason) => reason is UpdateRefusal.BundleInvalid or UpdateRefusal.SignatureInvalid;

    private static UpdateCheck Refused(UpdateRefusal reason, string detail) => UpdateCheck.Refuse(reason, detail, fetched: true);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
