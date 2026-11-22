namespace PcMieru.Checks;

/// <summary>Where a run's result file stands. A run that is not saved yet, saved, or failed to save (DESIGN-A §2, §4).</summary>
public enum CheckSaveStatus
{
    /// <summary>No save was attempted (yet).</summary>
    NotSaved,

    /// <summary>The file is committed: it was written, flushed and renamed into place.</summary>
    Saved,

    /// <summary>The save failed. The result is still in memory and on the screen; no file was committed.</summary>
    Failed,
}

/// <summary>
/// The save state a CheckRun carries next to its <see cref="CheckResult"/> (DESIGN-A §2: a failed save is shown, it never
/// takes the result away). Immutable. A failure is a value, never an exception, and never deletes a file that was already
/// committed. <see cref="Detail"/> is a short diagnostic (exception type and HRESULT) without a path or a user name.
/// </summary>
public sealed record CheckSaveState(CheckSaveStatus Status, string? FileName, string? FailureCode, string? Detail)
{
    public static CheckSaveState NotSaved { get; } = new(CheckSaveStatus.NotSaved, null, null, null);

    public static CheckSaveState Saved(string fileName) => new(CheckSaveStatus.Saved, fileName, null, null);

    public static CheckSaveState Failed(string failureCode, string? detail = null) => new(CheckSaveStatus.Failed, null, failureCode, detail);
}

/// <summary>Why a save failed. A fixed list; the writer never invents another.</summary>
public static class CheckSaveFailureCodes
{
    /// <summary>The result breaks a pcm-check/1 rule (the strict validation before writing), or would not read back as the same bytes. Nothing was written.</summary>
    public const string InvalidResult = "invalid_result";

    /// <summary>A file with this name is already there. A first-time history save never overwrites it.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>The history folder or the file refused the write (permission).</summary>
    public const string AccessDenied = "access_denied";

    /// <summary>No space left on the volume.</summary>
    public const string DiskFull = "disk_full";

    /// <summary>Any other I/O failure.</summary>
    public const string IoError = "io_error";

    public static IReadOnlyList<string> All { get; } = [InvalidResult, AlreadyExists, AccessDenied, DiskFull, IoError];
}
