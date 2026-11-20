using System.Globalization;

namespace PcMieru.Checks;

/// <summary>What the store is made of, so a test can pass a clock, a file system and a fixed process ID and nonce.</summary>
internal sealed class CheckHistoryStoreOptions
{
    /// <summary>A write in flight is minutes at most (in fact milliseconds); a leftover this old, with our own name, is abandoned.</summary>
    public static readonly TimeSpan DefaultTempMaxAge = TimeSpan.FromHours(1);

    public ICheckHistoryFileSystem Files { get; init; } = PhysicalHistoryFileSystem.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>Eight lowercase hex digits, new on every call.</summary>
    public Func<string> Nonce { get; init; } = static () => Guid.NewGuid().ToString("N")[..8];

    public TimeSpan TempMaxAge { get; init; } = DefaultTempMaxAge;
}

/// <summary>What a retention pass did: files deleted, and files it meant to delete but could not.</summary>
internal sealed record CheckRetentionResult(int Deleted, int Failed)
{
    public static CheckRetentionResult None { get; } = new(0, 0);
}

/// <summary>
/// The result store the GUI and the CLI use (DESIGN-A §4): it saves a result as one file, lists and reads the history, moves a
/// corrupt file aside, and keeps the newest 200 valid files per kind. The MCP server does not use this class; it gets
/// <see cref="ReadOnlyCheckHistory"/>, which cannot write.
/// <para>
/// There is no lock around the history files, in this process or across processes: every file is written once under its own
/// unique name and never changed afterwards, a save never overwrites anything, and a file that disappears between the listing
/// and the read is simply not counted. (The cross-process lock of DESIGN-A §4 is for the baseline, a later step.)
/// </para>
/// </summary>
public sealed class CheckHistoryStore : IReadOnlyCheckHistory
{
    private const int MaxNameAttempts = 64;

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

    public string Root => _scanner.Root;

    /// <summary>
    /// Lists the history. A corrupt file met on the way is moved aside (<c>name.json.bad-&lt;UTC&gt;</c>) and reported
    /// (<see cref="CheckHistoryWarningCodes.Quarantined"/>, or <see cref="CheckHistoryWarningCodes.QuarantineFailed"/> when the rename failed).
    /// A file of a later version, a locked file and a file the user may not read are kept and reported.
    /// </summary>
    public Task<CheckHistoryPage> ListAsync(CheckHistoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Scan(query, ct).Page, ct);
    }

    public Task<CheckResult?> GetAsync(Guid runId, CancellationToken ct) => Task.Run(() => GetDetail(runId, ct).Result, ct);

    /// <summary>The listing with the per-file reasons behind its warnings (the CLI and the tests read them).</summary>
    internal CheckHistoryScan Scan(CheckHistoryQuery query, CancellationToken ct) => _scanner.Scan(query, Quarantine, ct);

    internal CheckHistoryDetail GetDetail(Guid runId, CancellationToken ct) => _scanner.Get(runId, Quarantine, ct);

    /// <summary>
    /// Saves the result as <c>&lt;kind&gt;-&lt;UTC&gt;-&lt;runId:N&gt;.json</c>, atomically: the bytes are written to a new unique .tmp file in the
    /// same folder (<see cref="FileMode.CreateNew"/>), flushed to disk and closed, then renamed to the final name, which must not exist.
    /// It never overwrites a file and never deletes a committed one to undo anything. A failure comes back as a
    /// <see cref="CheckSaveState"/> with a <see cref="CheckSaveFailureCodes"/> code; it is never thrown. After a committed save the
    /// kind's retention and the clean-up of abandoned .tmp files run, and a problem there never turns the save into a failure.
    /// <paramref name="ct"/> is looked at before the work starts only: a write that has begun is finished.
    /// </summary>
    public Task<CheckSaveState> SaveAsync(CheckResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Task.Run(() => Save(result), ct);
    }

    internal CheckSaveState Save(CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // 1. Strict validation, then the bytes, then a strict read back: what is written is exactly what reads back.
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
        catch (Exception ex) when (ex is CheckFormatException or ArgumentException)
        {
            return CheckSaveState.Failed(CheckSaveFailureCodes.InvalidResult, ex.Message);
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
            files.Move(tempPath, finalPath);
            tempPath = null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            var finalExists = renaming && SafeExists(finalPath);
            RemoveOwnTemp(tempPath);
            return Failure(ex, finalExists);
        }

        // 3. Housekeeping after the commit. Never a failure of the save.
        try
        {
            ApplyRetention(result.Kind);
            CleanStaleTemps();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // The file is committed; the next save does the housekeeping again.
        }

        return CheckSaveState.Saved(finalName);
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
            // Left behind: CleanStaleTemps removes it once it is old enough.
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

    private static CheckSaveState Failure(Exception ex, bool finalExists)
    {
        var detail = string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
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
    /// <item>Never deleted, never moved: a corrupt file (the listing reports and moves it aside; retention does not, so the warning is not lost),
    /// a file of a later format or kind, a locked file, a file that cannot be read for permission or I/O reasons, a file of another kind,
    /// a .tmp file, a .bad file, any other name. A file that cannot be read counts as not valid, so the window may keep one file more
    /// than 200 for a while, never fewer.</item>
    /// <item>With 200 files or fewer of the kind, nothing is read at all.</item>
    /// </list>
    /// </summary>
    internal CheckRetentionResult ApplyRetention(CheckKind kind)
    {
        var kindName = SnakeCaseEnum<CheckKind>.Name(kind);
        var candidates = CheckHistoryScanner.Candidates(_scanner.ListFiles(), kindName);
        if (candidates.Count <= CheckHistoryScanner.KeepPerKind)
        {
            return CheckRetentionResult.None;
        }

        var valid = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var file in candidates)
        {
            if (_scanner.Classify(file).State != HistoryFileState.Valid)
            {
                continue;
            }

            if (valid < CheckHistoryScanner.KeepPerKind)
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
    /// Deletes the .tmp files of an abandoned write. A file is ours to delete only when both hold: its name is exactly
    /// <c>&lt;history file name&gt;.pcmtmp-&lt;pid&gt;-&lt;8 hex&gt;.tmp</c> (see <see cref="CheckHistoryNames.IsOwnTemp"/>), and it was last written at
    /// least <see cref="CheckHistoryStoreOptions.TempMaxAge"/> (one hour) before now. A write in flight is far younger, holds the file
    /// exclusively (so the delete would fail anyway), and a file dated in the future is never old. No other .tmp file, no .bad file and no
    /// committed file is touched. Returns the number deleted.
    /// </summary>
    internal int CleanStaleTemps()
    {
        var now = _options.Time.GetUtcNow().UtcDateTime;
        var deleted = 0;
        foreach (var entry in _scanner.ListFiles())
        {
            if (!CheckHistoryNames.IsOwnTemp(entry.Name) || now - entry.LastWriteUtc < _options.TempMaxAge)
            {
                continue;
            }

            try
            {
                _options.Files.Delete(Path.Combine(Root, entry.Name));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by someone (a live writer) or not ours to delete after all: leave it.
            }
        }

        return deleted;
    }
}
