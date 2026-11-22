// Provenance: the flow of Updater.CheckAsync of the owner's pl-cloud src/PartsLife.Update/Updater.cs (Parts Life, MIT, same owner; see NOTICE.txt), rebuilt for this
// repo: it asks for the release, SHA256.txt and the outer manifest and stops there (no zip). What is new here: the mode gate is the first line; the check is serialised
// by check.lock; a refusal says which rule held (UpdateRefusal); the candidate must be newer than both the running and the installed version; the comments are in English.
using System.Security.Cryptography;
using System.Text;
using PcMieru.Update.Bundle;

namespace PcMieru.Update.Feed;

/// <summary>What the rest of the app asks of the update feed (DESIGN-B 2). The GUI holds this; a smoke run holds a fake.</summary>
public interface IUpdateService
{
    /// <summary>
    /// One check. In <see cref="UpdateMode.Ask"/> and <see cref="UpdateMode.Off"/> it returns "not allowed" without touching anything (no client, no file, no
    /// network). <paramref name="manual"/> skips "not due yet" but not a rate limit. A cancelled <paramref name="ct"/> throws OperationCanceledException.
    /// </summary>
    Task<UpdateCheck> CheckAsync(UpdateMode mode, bool manual, CancellationToken ct);

    /// <summary>What the state file says (last check, wait, last candidate). Reads a local file; asks nobody.</summary>
    UpdateStatus ReadStatus();
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

    /// <summary>Tags that must not be offered again (step 4 reads them from its blocked list). Compared to the tag as written, "v1.2.3".</summary>
    public IReadOnlyCollection<string> BlockedTags { get; init; } = [];

    /// <summary>Makes the transport of one check (disposed after it). Null: the real HTTP client, made only when a check really goes out.</summary>
    public Func<IFeedTransport>? TransportFactory { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long a MANUAL check waits for check.lock when another check holds it (the one that Off just cancelled has not let go yet, or another window is just
    /// finishing). A scheduled tick never waits: it gives way at once. Zero: do not wait.
    /// </summary>
    public TimeSpan ManualLockWait { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The check of the feed (DESIGN-B 3.1). Order of the rules: the mode first (nothing else is looked at in Ask / Off), then check.lock, then the wait after a rate limit
/// (a manual check waits too), then "not due yet" (a scheduled check only), then the one write that says "I looked now", then the requests: the API, SHA256.txt and the
/// outer manifest. It never asks for the zip.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private readonly UpdateServiceOptions _options;
    private readonly UpdateStateStore _store;

    public UpdateService(UpdateServiceOptions options)
    {
        _options = options;
        _store = new UpdateStateStore(options.StateFolder);
    }

    public UpdateStatus ReadStatus() => _store.Read().ToStatus();

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

        if (_options.BlockedTags.Contains(release.Tag, StringComparer.Ordinal))
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

        return UpdateCheck.Found(release.Version, release.PageUrl, manifest, UpdateEndpoints.AssetUrl(release.Tag, assets.Zip.Name), zipHash);
    }

    private static UpdateCheck Refused(UpdateRefusal reason, string detail) => UpdateCheck.Refuse(reason, detail, fetched: true);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
