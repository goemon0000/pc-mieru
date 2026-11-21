using System.Globalization;

namespace PcMieru.Checks;

/// <summary>What the store is made of, so a test can pass a clock, a file system, a fixed process ID and nonce, and a sleeper.</summary>
internal sealed class CheckHistoryStoreOptions
{
    /// <summary>A write in flight is minutes at most (in fact milliseconds); a leftover this old, with this name shape, is abandoned.</summary>
    public static readonly TimeSpan DefaultTempMaxAge = TimeSpan.FromHours(1);

    public ICheckHistoryFileSystem Files { get; init; } = PhysicalHistoryFileSystem.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>Eight lowercase hex digits, new on every call.</summary>
    public Func<string> Nonce { get; init; } = static () => Guid.NewGuid().ToString("N")[..8];

    public TimeSpan TempMaxAge { get; init; } = DefaultTempMaxAge;

    /// <summary>Waits between the tries of the final move (see <see cref="CheckHistoryStore.MoveRetryDelays"/>). A test passes a recorder that does not wait.</summary>
    public Action<TimeSpan> Sleep { get; init; } = static delay => Thread.Sleep(delay);
}

/// <summary>What a retention pass did: files deleted, and files it meant to delete but could not.</summary>
internal sealed record CheckRetentionResult(int Deleted, int Failed)
{
    public static CheckRetentionResult None { get; } = new(0, 0);
}

/// <summary>What the clean-up of abandoned .tmp files did: deleted, or promoted to a committed file (and the kinds of those, for the retention).</summary>
internal sealed record CheckTempCleanResult(int Deleted, int Promoted, IReadOnlyList<CheckKind> PromotedKinds)
{
    public static CheckTempCleanResult None { get; } = new(0, 0, []);
}

/// <summary>
/// The result store the GUI and the CLI use (DESIGN-A §4): it saves a result as one file, lists and reads the history, moves a
/// corrupt file aside, and keeps the newest 200 valid files per kind. The MCP server does not use this class; it gets
/// <see cref="CheckHistory.OpenReadOnly"/>, which cannot write. The store is not an <see cref="IReadOnlyCheckHistory"/> (it is an
/// <see cref="ICheckHistoryMaintainer"/>), so it can never be handed to code that expects the read-only history.
/// <para>
/// There is no lock around the history files, in this process or across processes: every file is written once under its own
/// unique name and never changed afterwards, a save never overwrites anything, and a file that disappears between the listing
/// and the read is simply not counted. (The cross-process lock of DESIGN-A §4 is for the baseline, a later step.)
/// </para>
/// </summary>
public sealed class CheckHistoryStore : ICheckHistoryMaintainer
{
    private const int MaxNameAttempts = 64;

    /// <summary>
    /// The waits between the tries of the final move: 25, 50, 100, 200 and 400 ms, 775 ms in all. A rename of a file that was just
    /// closed can meet a sharing violation (32) or an access denied (5) for a moment, typically a virus scanner or an indexer that
    /// still holds the file; these two are retried, nothing else is.
    /// </summary>
    internal static readonly TimeSpan[] MoveRetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    ];

    private readonly CheckHistoryStoreOptions _options;
    private readonly CheckHistoryScanner _scanner;

    /// <summary>A store on this folder (created at the first save). The default folder is <see cref="DefaultRoot"/>.</summary>
    public CheckHistoryStore(string root)
        : this(root, new CheckHistoryStoreOptions())
    {
    }

    internal CheckHistoryStore(string root, CheckHistoryStoreOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _scanner = new CheckHistoryScanner(options.Files, root);
    }

    /// <summary><c>%LOCALAPPDATA%\PCMieru\checks</c>. Only computes the path; nothing is created or read.</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru", "checks");

    /// <summary>
    /// The folder the v0.7.4 app kept its checks in (a <c>checks</c> folder under its own name in the roaming application data): the one the
    /// baseline reads once when it has no record of its own. Only computes the path; nothing is created or read. The app and the CLI pass this and
    /// never build the path themselves. (The old name is written in one file only, <c>BaselineEngine.cs</c>, which NOTICE.txt lists.)
    /// </summary>
    public static string DefaultLegacyDirectory() => Storage.BaselineEngine.DefaultLegacyDirectory();

    public string Root => _scanner.Root;

    /// <summary>
    /// Lists the history. A corrupt file met on the way is moved aside (<c>name.json.bad-&lt;UTC&gt;</c>) and reported
    /// (<see cref="CheckHistoryWarningCodes.Quarantined"/>, or <see cref="CheckHistoryWarningCodes.QuarantineFailed"/> when the rename failed).
    /// A file of a later version, a locked file and a file the user may not read are kept and reported. A folder that does not exist
    /// is an empty page; a folder that cannot be opened or enumerated throws <see cref="CheckHistoryFolderException"/>.
    /// </summary>
    public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Scan(query, ct).Page, ct);
    }

    /// <summary>The run: found, not found, or unreadable with the warning code (a corrupt file of the run is moved aside like in <see cref="ListAsync"/>).</summary>
    public Task<CheckHistoryGetResult> GetAsync(Guid runId, CancellationToken ct) => Task.Run(() => GetDetail(runId, ct).ToResult(), ct);

    /// <summary>The listing with the per-file reasons behind its warnings (the CLI and the tests read them).</summary>
    internal CheckHistoryScan Scan(CheckHistoryQuery query, CancellationToken ct) => _scanner.Scan(query, Quarantine, ct);

    internal CheckHistoryDetail GetDetail(Guid runId, CancellationToken ct) => _scanner.Get(runId, Quarantine, ct);

    /// <summary>
    /// Saves the result as <c>&lt;kind&gt;-&lt;UTC&gt;-&lt;runId:N&gt;.json</c>, atomically: the bytes are written to a new unique .tmp file in the
    /// same folder (<see cref="FileMode.CreateNew"/>), flushed to disk and closed, then renamed to the final name, which must not exist
    /// (the rename is write-through and never replaces; a sharing violation or an access denied is retried for about a second).
    /// It never overwrites a file and never deletes a committed one to undo anything. A failure comes back as a
    /// <see cref="CheckSaveState"/> with a <see cref="CheckSaveFailureCodes"/> code; nothing is thrown (only <see cref="OutOfMemoryException"/>
    /// is not caught): what breaks validation or serialization is <c>invalid_result</c>, any other failure of the write is classified
    /// (<c>access_denied</c>, <c>disk_full</c>, <c>already_exists</c>) or is <c>io_error</c>. There is no cancellation token: a result that is
    /// final is saved. After a committed save the clean-up of abandoned .tmp files and the kind's retention run, and a problem there
    /// never turns the save into a failure.
    /// </summary>
    public Task<CheckSaveState> SaveAsync(CheckResult result) => Task.Run(() => Save(result));

    internal CheckSaveState Save(CheckResult? result)
    {
        if (result is null)
        {
            return CheckSaveState.Failed(CheckSaveFailureCodes.InvalidResult, "There is no result to save.");
        }

        // 1. Strict validation, then the bytes, then a strict read back: what is written is exactly what reads back.
        // Whatever breaks here is a fault of the result, not of the disk.
        byte[] bytes;
        string finalName;
        try
        {
            var problems = CheckResultValidator.Validate(result);
            if (problems.Count > 0)
            {
                return CheckSaveState.Failed(CheckSaveFailureCodes.InvalidResult, problems[0]);
            }

            var text = CheckJson.Serialize(result, indented: true) + "\n";
            var back = CheckJson.Read(text, CheckReadMode.Strict).Result;
            if (!string.Equals(CheckJson.Serialize(back, indented: true) + "\n", text, StringComparison.Ordinal))
            {
                return CheckSaveState.Failed(CheckSaveFailureCodes.InvalidResult, "The result does not read back as the same JSON.");
            }

            bytes = System.Text.Encoding.UTF8.GetBytes(text);
            finalName = CheckHistoryNames.Format(result.Kind, result.EndedAtUtc, result.RunId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CheckSaveState.Failed(
                CheckSaveFailureCodes.InvalidResult,
                ex is CheckFormatException or ArgumentException ? ex.Message : Describe(ex));
        }

        // 2. Write to a unique .tmp, flush, close, rename without replacing.
        var files = _options.Files;
        var finalPath = Path.Combine(Root, finalName);
        string? tempPath = null;
        var renaming = false;
        try
        {
            files.CreateDirectory(Root);
            if (files.Exists(finalPath))
            {
                return CheckSaveState.Failed(CheckSaveFailureCodes.AlreadyExists, "A file with this name is already in the history.");
            }

            var (file, path) = CreateTemp(finalName);
            tempPath = path;
            using (file)
            {
                file.Write(bytes);
                file.FlushToDisk();
            }

            renaming = true;
            MoveDurably(tempPath, finalPath);
            tempPath = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var finalExists = renaming && SafeExists(finalPath);
            RemoveOwnTemp(tempPath);
            return Failure(ex, finalExists);
        }

        // 3. Housekeeping after the commit. Never a failure of the save.
        Housekeeping(result.Kind, finalName);
        return CheckSaveState.Saved(finalName);
    }

    // The final move: write-through, no replacing (the file system's Move). A sharing violation (32) or an access denied (5) is
    // retried with the short waits of MoveRetryDelays; anything else, and the last failure, goes to the caller.
    private void MoveDurably(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _options.Files.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < MoveRetryDelays.Length && IsTransientMoveFailure(ex))
            {
                _options.Sleep(MoveRetryDelays[attempt]);
            }
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_ACCESS_DENIED (5) in the low word of the HRESULT: a rename that may pass in a moment.</summary>
    internal static bool IsTransientMoveFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xFFFF) is 32 or 5;

    // After a commit: the clean-up of abandoned .tmp files (which may promote one), then the retention of the saved kind (the file
    // just saved is kept) and of the kinds of promoted files. Each step on its own: a failure in one never skips the others.
    private void Housekeeping(CheckKind savedKind, string savedName)
    {
        var clean = CheckTempCleanResult.None;
        Guard(() => clean = CleanStaleTemps());
        Guard(() => ApplyRetention(savedKind, savedName));
        foreach (var kind in clean.PromotedKinds)
        {
            if (kind != savedKind)
            {
                Guard(() => ApplyRetention(kind));
            }
        }
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The file is committed; the next save does the housekeeping again.
        }
    }

    // CreateNew with a unique name. A name that is taken (a collision of pid and nonce) is retried with a new nonce; the file
    // that was there is not touched. Any other failure is the caller's failure.
    private (ICheckHistoryNewFile File, string Path) CreateTemp(string finalName)
    {
        for (var attempt = 0; ; attempt++)
        {
            var path = Path.Combine(Root, CheckHistoryNames.TempName(finalName, _options.ProcessId, _options.Nonce()));
            try
            {
                return (_options.Files.CreateNew(path), path);
            }
            catch (IOException) when (attempt < MaxNameAttempts && SafeExists(path))
            {
                // Taken: another writer's file (or a leftover). Try another nonce.
            }
        }
    }

    // Only our own .tmp, by the exact path we just made. The committed file is never a candidate here.
    private void RemoveOwnTemp(string? tempPath)
    {
        if (tempPath is null)
        {
            return;
        }

        try
        {
            _options.Files.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: CleanStaleTemps removes (or promotes) it once it is old enough.
        }
    }

    private bool SafeExists(string path)
    {
        try
        {
            return _options.Files.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Describe(Exception ex) => string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}");

    private static CheckSaveState Failure(Exception ex, bool finalExists)
    {
        var detail = Describe(ex);
        return ex switch
        {
            UnauthorizedAccessException => CheckSaveState.Failed(CheckSaveFailureCodes.AccessDenied, detail),
            IOException io when IsDiskFull(io) => CheckSaveState.Failed(CheckSaveFailureCodes.DiskFull, detail),
            IOException when finalExists => CheckSaveState.Failed(CheckSaveFailureCodes.AlreadyExists, "A file with this name is already in the history."),
            _ => CheckSaveState.Failed(CheckSaveFailureCodes.IoError, detail),
        };
    }

    /// <summary>ERROR_HANDLE_DISK_FULL (39) or ERROR_DISK_FULL (112) in the low word of the HRESULT.</summary>
    internal static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 39 or 112;

    /// <summary>
    /// Moves a corrupt file aside as <c>name.bad-&lt;UTC&gt;</c> (then <c>-1</c>, <c>-2</c> ... when that name is taken) and never
    /// replaces anything: the destination must not exist (checked, and the rename itself refuses to overwrite). A file that is already gone
    /// is not a problem. When the rename fails the file stays exactly as it was, and nothing here writes to it.
    /// </summary>
    internal QuarantineResult Quarantine(HistoryFileName file)
    {
        var files = _options.Files;
        var source = _scanner.PathOf(file);
        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var badName = CheckHistoryNames.BadName(file.Name, _options.Time.GetUtcNow(), attempt);
            var destination = Path.Combine(Root, badName);
            try
            {
                if (files.Exists(destination))
                {
                    continue;
                }

                files.Move(source, destination);
                return QuarantineResult.Moved(badName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return QuarantineResult.Vanished;
            }
            catch (IOException) when (SafeExists(destination))
            {
                // Someone took the name between the check and the rename. Next name.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return QuarantineResult.Failed;
            }
        }

        return QuarantineResult.Failed;
    }

    /// <summary>
    /// Keeps the newest <see cref="CheckHistoryScanner.KeepPerKind"/> valid files of the kind and deletes the older valid ones.
    /// <list type="bullet">
    /// <item>Deleted: a file named like a history file of this kind, that reads as valid right now (tolerant, name and body agree), and is
    /// older than the 200 newest valid ones (end time, then UUID, as in the listing).</item>
    /// <item><paramref name="keepName"/> (the file the caller just saved) is never deleted and is never read here, whatever its age, and it
    /// counts among the 200 kept files: when it is there, the 199 newest other valid files are kept beside it. So a save of a run that ended
    /// before 200 newer files costs the oldest of those, not the file just saved.</item>
    /// <item>Never deleted, never moved: a corrupt file (the listing reports and moves it aside; retention does not, so the warning is not lost),
    /// a file of a later format or kind, a locked file, a file that cannot be read for permission or I/O reasons, a file of another kind,
    /// a .tmp file, a .bad file, any other name. A file that cannot be read counts as not valid, so the window may keep one file more
    /// than 200 for a while, never fewer.</item>
    /// <item>With 200 files or fewer of the kind, nothing is read at all.</item>
    /// </list>
    /// </summary>
    internal CheckRetentionResult ApplyRetention(CheckKind kind, string? keepName = null)
    {
        var kindName = SnakeCaseEnum<CheckKind>.Name(kind);
        var candidates = CheckHistoryScanner.Candidates(_scanner.ListFiles(), kindName);
        if (candidates.Count <= CheckHistoryScanner.KeepPerKind)
        {
            return CheckRetentionResult.None;
        }

        // The kept file takes one of the places only when it is really in the folder (if it has vanished, nothing is reserved for it).
        var keepPresent = keepName is not null && candidates.Any(f => string.Equals(f.Name, keepName, StringComparison.Ordinal));
        var places = CheckHistoryScanner.KeepPerKind - (keepPresent ? 1 : 0);

        var valid = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var file in candidates)
        {
            if (keepPresent && string.Equals(file.Name, keepName, StringComparison.Ordinal))
            {
                continue;
            }

            if (_scanner.Classify(file).State != HistoryFileState.Valid)
            {
                continue;
            }

            if (valid < places)
            {
                valid++;
                continue;
            }

            try
            {
                _options.Files.Delete(_scanner.PathOf(file));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return new CheckRetentionResult(deleted, failed);
    }

    /// <summary>
    /// Deals with the .tmp files of an abandoned write. A file is dealt with only when both hold: its name has exactly the shape
    /// <c>&lt;history file name&gt;.pcmtmp-&lt;pid&gt;-&lt;8 hex&gt;.tmp</c> (see <see cref="CheckHistoryNames.TryParseTemp"/>; the pid is not looked at,
    /// this is a rule about abandoned files by shape and age, not about who owns them), and it was last written at least
    /// <see cref="CheckHistoryStoreOptions.TempMaxAge"/> (one hour) before now. A write in flight is far younger and holds the file
    /// exclusively (so a delete would fail anyway), and a file dated in the future is never old. The leftover is first offered for
    /// <b>promotion</b>: it becomes the committed file (the same durable rename, never replacing) when all of these hold: the
    /// body reads strictly and passes the validation; kind, end time and UUID of the body are those of the final name derived from the
    /// .tmp name; and no file of the final name exists. Otherwise it is deleted. A leftover that cannot be promoted or deleted just now (held,
    /// a permission, a rename that keeps failing) stays for the next clean-up. No other .tmp file, no .bad file and no committed file is touched.
    /// </summary>
    internal CheckTempCleanResult CleanStaleTemps()
    {
        var now = _options.Time.GetUtcNow().UtcDateTime;
        var deleted = 0;
        var promoted = new List<CheckKind>();
        foreach (var entry in _scanner.ListFiles())
        {
            if (!CheckHistoryNames.TryParseTemp(entry.Name, out var final) || now - entry.LastWriteUtc < _options.TempMaxAge)
            {
                continue;
            }

            var tempPath = Path.Combine(Root, entry.Name);
            switch (TryPromote(final, tempPath))
            {
                case TempFate.Promoted:
                    if (SnakeCaseEnum<CheckKind>.TryParse(final.Kind, out var kind))
                    {
                        promoted.Add(kind);
                    }

                    break;
                case TempFate.Surplus:
                    try
                    {
                        _options.Files.Delete(tempPath);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Held by someone (a live writer) or not ours to delete after all: leave it.
                    }

                    break;
            }
        }

        return new CheckTempCleanResult(deleted, promoted.Count, promoted);
    }

    private enum TempFate
    {
        /// <summary>It was renamed to the final name: a committed file now.</summary>
        Promoted,

        /// <summary>Not promotable (invalid, not matching its name, or the final name is taken): delete it.</summary>
        Surplus,

        /// <summary>Neither just now (it vanished, or the rename failed for another reason): leave it.</summary>
        Left,
    }

    private TempFate TryPromote(HistoryFileName final, string tempPath)
    {
        var finalPath = _scanner.PathOf(final);
        if (SafeExists(finalPath))
        {
            return TempFate.Surplus;
        }

        try
        {
            var bytes = _options.Files.ReadAllBytes(tempPath, CheckHistoryScanner.MaxFileBytes);
            var result = CheckJson.Read(bytes, CheckReadMode.Strict).Result;
            if (CheckResultValidator.Validate(result).Count > 0 || CheckHistoryScanner.NameMismatch(result, final) is not null)
            {
                return TempFate.Surplus;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TempFate.Left;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // Not readable now, or not a result: it cannot be promoted. (A file that is held cannot be deleted either, and stays.)
            return TempFate.Surplus;
        }

        try
        {
            MoveDurably(tempPath, finalPath);
            return TempFate.Promoted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The final name appeared in the meantime: its content is committed, the leftover is surplus. Anything else: try again later.
            return SafeExists(finalPath) ? TempFate.Surplus : TempFate.Left;
        }
    }
}
