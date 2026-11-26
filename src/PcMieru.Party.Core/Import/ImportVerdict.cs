namespace PcMieru.Party.Import;

/// <summary>What the importer says about one source <c>state.json</c> of the old Parts Life.</summary>
public enum ImportStatus
{
    /// <summary>Every known field was read as written. <see cref="ImportVerdict.Converted"/> is ready to be written to the new place.</summary>
    Acceptable,

    /// <summary>
    /// Some known fields were broken and had to be replaced by defaults (<see cref="ImportVerdict.RepairedFields"/>).
    /// <see cref="ImportVerdict.Converted"/> holds the repaired state, but this is NOT a success: the caller must show
    /// the fields and get consent again, and must keep the untouched original before writing anything.
    /// </summary>
    NeedsRepair,

    /// <summary>The source must not be imported. <see cref="ImportVerdict.Reason"/> says why; there are no converted bytes.</summary>
    Hold,
}

/// <summary>Stable reason codes of a <see cref="ImportStatus.Hold"/>. They are data for the UI and for tests; never parse the free text.</summary>
public static class ImportReason
{
    /// <summary>More than <see cref="StateImport.MaxBytes"/> bytes.</summary>
    public const string TooLarge = "too-large";

    /// <summary>Not valid JSON (also: empty, invalid UTF-8, trailing text, comments, trailing commas).</summary>
    public const string BrokenJson = "broken-json";

    /// <summary>Nested deeper than <see cref="StateImport.MaxDepth"/>.</summary>
    public const string TooDeep = "too-deep";

    /// <summary>The same key twice in one object, anywhere in the document.</summary>
    public const string DuplicateKey = "duplicate-key";

    /// <summary>Valid JSON, but the top level is not an object.</summary>
    public const string NotAnObject = "not-an-object";

    /// <summary><c>SaveVersion</c> is present but not an integer (string, null, fraction, ...).</summary>
    public const string VersionType = "version-type";

    /// <summary><c>SaveVersion</c> is negative.</summary>
    public const string VersionNegative = "version-negative";

    /// <summary><c>SaveVersion</c> is newer than this program knows. A newer writer may hold more than this program can carry.</summary>
    public const string VersionFuture = "version-future";

    /// <summary>Experience or the story position (Journey/Chapter/Scene) is missing or unreadable.</summary>
    public const string MainFieldsUnreadable = "main-fields-unreadable";

    /// <summary>The source parsed, but could not be turned into the new form (for example a lone surrogate in a string).</summary>
    public const string ConversionFailed = "conversion-failed";

    /// <summary>A bug in the importer. Nothing is converted.</summary>
    public const string InternalError = "internal-error";

    /// <summary>The converted state would be larger than <see cref="StateImport.MaxOutputBytes"/> (escaping can make text larger than the source).</summary>
    public const string OutputTooLarge = "output-too-large";

    // ---- The codes of the engine (ImportEngine). Added after the codes above; none of those changed. ----

    /// <summary>Plan: every known field was read as written; ready to commit.</summary>
    public const string Accept = "accept";

    /// <summary>Plan: some known fields must be repaired; commit needs consent.</summary>
    public const string NeedsRepair = "needs-repair";

    /// <summary>Result: the converted save is at the destination.</summary>
    public const string Imported = "imported";

    /// <summary>Result: a fresh save was written to the destination.</summary>
    public const string StartedFresh = "started-fresh";

    /// <summary>The destination already holds a save that can be read. Nothing is written over it.</summary>
    public const string AlreadyHave = "already-have";

    /// <summary>Neither <c>PartsLife\state.json</c> nor <c>PartsParty\state.json</c> exists.</summary>
    public const string NoCandidate = "no-candidate";

    /// <summary>The destination exists but is not a save that can be read (not JSON, not an object, ...). It is kept as it is.</summary>
    public const string DestinationBroken = "destination-broken";

    /// <summary>The destination exists but is larger than <see cref="StateImport.MaxOutputBytes"/>. It is kept as it is.</summary>
    public const string DestinationTooLarge = "destination-too-large";

    /// <summary>The destination cannot be opened: access denied (also a folder named <c>state.json</c>). It is kept as it is.</summary>
    public const string DestinationDenied = "destination-denied";

    /// <summary>The destination cannot be opened: another program holds it without sharing. It is kept as it is.</summary>
    public const string DestinationSharing = "destination-sharing";

    /// <summary>The destination cannot be read: an I/O error. It is kept as it is.</summary>
    public const string DestinationIoError = "destination-io-error";

    /// <summary>The old product runs as this user.</summary>
    public const string OldAppRunning = "old-app-running";

    /// <summary>It could not be told whether the old product runs (treated like running).</summary>
    public const string OldAppUnknown = "old-app-unknown";

    /// <summary>The first candidate cannot be opened: access denied (also a folder named <c>state.json</c>). The next candidate is NOT tried.</summary>
    public const string SourceDenied = "source-denied";

    /// <summary>The first candidate cannot be opened: another program holds it without sharing. The next candidate is NOT tried.</summary>
    public const string SourceSharing = "source-sharing";

    /// <summary>The first candidate cannot be read: an I/O error. The next candidate is NOT tried.</summary>
    public const string SourceIoError = "source-io-error";

    /// <summary>The source is not the version that was inspected (identity, size or SHA-256 differs, or it is gone). Nothing is written.</summary>
    public const string SourceChanged = "source-changed";

    /// <summary>An old save appeared after the plan saw none, before the first file was written. Nothing is written; the plan is made again.</summary>
    public const string CandidateAppeared = "candidate-appeared";

    /// <summary>Commit: the repairs were not consented to. Nothing is written.</summary>
    public const string NeedsConsent = "needs-consent";

    /// <summary>The per-user import lock is kept by somebody else (another window or process doing the same thing).</summary>
    public const string LockBusy = "lock-busy";

    /// <summary>Writing the destination failed (disk, rights, a folder in the way). The temp files of this call are gone.</summary>
    public const string WriteFailed = "write-failed";

    /// <summary>The temp file did not read back as the bytes that were written, or <c>PartyState.FromJson</c> could not read it. Nothing was published.</summary>
    public const string VerifyFailed = "verify-failed";
}

/// <summary>
/// The result of <see cref="StateImport.Inspect"/>. Plain data; the importer touches no file, registry or process.
/// </summary>
public sealed class ImportVerdict
{
    public required ImportStatus Status { get; init; }

    /// <summary>Lower-case hex SHA-256 of the source bytes exactly as given (BOM included). Set for every verdict.</summary>
    public required string SourceSha256 { get; init; }

    /// <summary>The hold reason (an <see cref="ImportReason"/> code). Null unless the status is <see cref="ImportStatus.Hold"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Short English text for logs and tests (field names, sizes). Not for parsing.</summary>
    public string? Detail { get; init; }

    /// <summary>The <c>SaveVersion</c> of the source (0 when absent). Null when it could not be determined.</summary>
    public int? SourceVersion { get; init; }

    /// <summary>Known fields that were broken and replaced, as paths like <c>Adventure.Gold</c> or <c>Experience[cpu#0]</c>.</summary>
    public IReadOnlyList<string> RepairedFields { get; init; } = [];

    /// <summary>Known source fields that are deliberately not carried (see <see cref="StateImport"/>). Today only <c>UpdateMode</c>.</summary>
    public IReadOnlyList<string> DroppedFields { get; init; } = [];

    /// <summary>
    /// The state for the new destination: UTF-8 without BOM, <c>SaveVersion</c> 1, PascalCase, compact (not indented).
    /// Set for <see cref="ImportStatus.Acceptable"/> and <see cref="ImportStatus.NeedsRepair"/>; null for a hold.
    /// </summary>
    public byte[]? Converted { get; init; }

    /// <summary>"accept", "needs-repair" or "hold".</summary>
    public string Code => Status switch
    {
        ImportStatus.Acceptable => "accept",
        ImportStatus.NeedsRepair => "needs-repair",
        _ => "hold",
    };
}
