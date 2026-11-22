using PcMieru.Update.Bundle;

namespace PcMieru.Update.Feed;

/// <summary>
/// Why a check did not end in a candidate although it did not fail quietly (DESIGN-B 3.1 and 11). One value per rule, so that a test can say which
/// rule held and the GUI can say it in Japanese and English (the key is <c>gui.update.refusal.</c> + the lower-case name; a test keeps the two in step).
/// "Nothing newer" is not a refusal (that is <see cref="UpdateCheckKind.NoCandidate"/>), and neither is a rate limit (that is <see cref="UpdateCheckKind.Waiting"/>).
/// </summary>
public enum UpdateRefusal
{
    None = 0,

    // --- the release ---
    /// <summary>The answer of the API is not a release (not JSON, not an object, no tag).</summary>
    FeedUnreadable,

    /// <summary>A draft release.</summary>
    DraftRelease,

    /// <summary>A pre-release.</summary>
    PrereleaseRelease,

    /// <summary>The tag is not a plain "vX.Y.Z".</summary>
    TagNotStable,

    /// <summary>The Release page of the answer is not this repo's page of that tag (another repo, another tag, another host).</summary>
    ReleasePageMismatch,

    /// <summary>The tag is on the list of versions that must not be offered again (a version that failed on this PC).</summary>
    BlockedTag,

    // --- the assets ---
    /// <summary>One of the three files (zip, outer manifest, SHA256.txt) is not on the release.</summary>
    AssetMissing,

    /// <summary>One of the three files is on the release twice.</summary>
    AssetDuplicate,

    /// <summary>One of the three files is not finished uploading, or is empty.</summary>
    AssetNotReady,

    /// <summary>The download address of one of the three files is not exactly this repo's address of that tag and name.</summary>
    AssetUrlMismatch,

    /// <summary>SHA256.txt or the manifest is larger than its limit, by the size the release says.</summary>
    AssetTooLarge,

    /// <summary>SHA256.txt has no single line for the zip, or none for the manifest.</summary>
    HashListLine,

    /// <summary>The "digest" the API gives for a file is not the hash SHA256.txt (or the bytes) say.</summary>
    DigestMismatch,

    /// <summary>The manifest that came is not the one SHA256.txt names.</summary>
    ManifestHashMismatch,

    /// <summary>The manifest is not a valid pcm-bundle/1 manifest.</summary>
    ManifestInvalid,

    /// <summary>The manifest is for another version than the tag.</summary>
    ManifestVersionMismatch,

    /// <summary>A body is longer than its limit (counted on the bytes that actually came).</summary>
    BodyTooLarge,

    // --- the connection ---
    /// <summary>A redirect to an address that is not on the list (another host, another port, another path).</summary>
    RedirectHostNotAllowed,

    /// <summary>A redirect from https to http.</summary>
    RedirectDowngrade,

    /// <summary>More than five redirects.</summary>
    RedirectTooMany,

    /// <summary>A redirect to an address that carries a user name or password.</summary>
    RedirectCredentials,

    /// <summary>A redirect without a place to go.</summary>
    RedirectMissingLocation,

    /// <summary>The server answered with an error status (not a rate limit).</summary>
    HttpError,

    /// <summary>No answer: no connection, a timeout, a broken stream.</summary>
    NetworkFailure,

    // --- this PC ---
    /// <summary>The state folder cannot be used (cannot be created or written), so the check does not run: it could not remember that it ran.</summary>
    StateUnavailable,

    /// <summary>Another check of this install is running (it holds check.lock).</summary>
    AnotherCheckRunning,

    /// <summary>The GUI could not read its own version (not "X.Y.Z"), so it cannot tell what is newer and does not check.</summary>
    OwnVersionUnknown,
}

/// <summary>
/// Which refusals JUDGED A RELEASE and which only say that the check did not get far enough to judge one (stage B step 2 review, S3). A candidate that an earlier
/// check found stays on file through an <see cref="IsOperational"/> failure (the network was down, GitHub answered with an error, this PC could not read or write its
/// state, another check was running): such a failure learned nothing about the release. It is replaced only by an outcome that did judge one: "nothing newer"
/// after parsing, a candidate, or a refusal of the release's own rules (<see cref="JudgedARelease"/>). A test lists the enum, so a new reason must be put on one side.
/// </summary>
public static class UpdateRefusals
{
    /// <summary>The check did not judge a release: the answer did not come, was not readable, was cut off, was sent somewhere else, or this PC could not take part.</summary>
    public static bool IsOperational(UpdateRefusal reason) => reason is
        UpdateRefusal.FeedUnreadable or UpdateRefusal.BodyTooLarge or
        UpdateRefusal.RedirectHostNotAllowed or UpdateRefusal.RedirectDowngrade or UpdateRefusal.RedirectTooMany or
        UpdateRefusal.RedirectCredentials or UpdateRefusal.RedirectMissingLocation or
        UpdateRefusal.HttpError or UpdateRefusal.NetworkFailure or
        UpdateRefusal.StateUnavailable or UpdateRefusal.AnotherCheckRunning or UpdateRefusal.OwnVersionUnknown;

    /// <summary>The release was read and one of its rules did not hold (a draft, a pre-release, a wrong tag or page, a blocked tag, a missing or wrong file, a hash that differs, a bad manifest). None is not a reason.</summary>
    public static bool JudgedARelease(UpdateRefusal reason) => reason != UpdateRefusal.None && !IsOperational(reason);

    /// <summary>The two reasons that say "the network did not answer": they are asked again after <see cref="UpdateSchedule.RetryAfterFailure"/>, not after a day.</summary>
    public static bool IsTransient(UpdateRefusal reason) => reason is UpdateRefusal.NetworkFailure or UpdateRefusal.HttpError;
}

/// <summary>The five ways a check can end.</summary>
public enum UpdateCheckKind
{
    /// <summary>Nothing to offer (the release is not newer, or nothing was asked because it is not time yet).</summary>
    NoCandidate,

    /// <summary>A newer release whose files are all in order: its version, its Release page and its validated outer manifest.</summary>
    Candidate,

    /// <summary>A refusal with a reason (<see cref="UpdateCheck.Refusal"/>).</summary>
    Refused,

    /// <summary>GitHub asked to be left alone until <see cref="UpdateCheck.WaitUntil"/>.</summary>
    Waiting,

    /// <summary>The mode does not allow a check (Ask or Off). Nothing was sent.</summary>
    NotAllowed,
}

/// <summary>What <see cref="IUpdateService.CheckAsync"/> found.</summary>
public sealed class UpdateCheck
{
    private UpdateCheck(UpdateCheckKind kind) => Kind = kind;

    public UpdateCheckKind Kind { get; }

    /// <summary>Candidate: the version.</summary>
    public AppVersion? Version { get; private init; }

    /// <summary>Candidate: the Release page of the tag (always <see cref="UpdateEndpoints.IsReleasePage"/>).</summary>
    public string? PageUrl { get; private init; }

    /// <summary>Candidate: the outer manifest, read with the strict reader and checked against SHA256.txt.</summary>
    public BundleManifest? Manifest { get; private init; }

    /// <summary>Candidate: where the zip is. Never requested in this step.</summary>
    public Uri? ZipUrl { get; private init; }

    /// <summary>Candidate: the SHA-256 of the zip that SHA256.txt names (lower-case hex), for the download of a later step.</summary>
    public string? ZipSha256 { get; private init; }

    /// <summary>Refused: which rule held.</summary>
    public UpdateRefusal Refusal { get; private init; }

    /// <summary>Refused: the version of the release that was refused, when the check had read it far enough to know (not for a tag that is not a version).</summary>
    public AppVersion? RefusedVersion { get; private init; }

    /// <summary>Refused: a short text for a log (never shown as the reason: the reason is the enum).</summary>
    public string Detail { get; private init; } = string.Empty;

    /// <summary>Waiting: until when.</summary>
    public DateTimeOffset? WaitUntil { get; private init; }

    /// <summary>NotAllowed: the mode that did not allow it.</summary>
    public UpdateMode Mode { get; private init; }

    /// <summary>
    /// True when this call asked GitHub (a request was made). False for "not allowed", "waiting", "not due yet" and "another check is running":
    /// the caller must not read such an answer as news about the release.
    /// </summary>
    public bool Fetched { get; private init; }

    public static UpdateCheck NoCandidate(bool fetched) => new(UpdateCheckKind.NoCandidate) { Fetched = fetched };

    public static UpdateCheck Found(AppVersion version, string pageUrl, BundleManifest manifest, Uri zipUrl, string zipSha256) =>
        new(UpdateCheckKind.Candidate)
        {
            Version = version,
            PageUrl = pageUrl,
            Manifest = manifest,
            ZipUrl = zipUrl,
            ZipSha256 = zipSha256,
            Fetched = true,
        };

    public static UpdateCheck Refuse(UpdateRefusal reason, string detail, bool fetched, AppVersion? version = null) =>
        new(UpdateCheckKind.Refused) { Refusal = reason, Detail = detail, Fetched = fetched, RefusedVersion = version };

    public static UpdateCheck Wait(DateTimeOffset until, bool fetched) =>
        new(UpdateCheckKind.Waiting) { WaitUntil = until, Fetched = fetched };

    public static UpdateCheck NotAllowed(UpdateMode mode) => new(UpdateCheckKind.NotAllowed) { Mode = mode };

    public override string ToString() => Kind switch
    {
        UpdateCheckKind.Candidate => $"candidate {Version}",
        UpdateCheckKind.Refused => $"refused: {Refusal} ({Detail})",
        UpdateCheckKind.Waiting => $"waiting until {WaitUntil:O}",
        UpdateCheckKind.NotAllowed => $"not allowed in mode {Mode}",
        _ => Fetched ? "no candidate" : "no candidate (nothing asked)",
    };
}

/// <summary>What the state file says, for a page that shows it. Reading it asks nobody.</summary>
public sealed record UpdateStatus(
    DateTimeOffset? LastCheckUtc,
    DateTimeOffset? BackoffUntilUtc,
    AppVersion? CandidateVersion,
    string? CandidatePageUrl,
    UpdateRefusal LastRefusal,
    AppVersion? RefusedVersion = null)
{
    public static UpdateStatus Empty { get; } = new(null, null, null, null, UpdateRefusal.None);
}
