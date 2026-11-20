using System.Diagnostics;

namespace PcMieru.Checks.Storage;

/// <summary>
/// The short cross-process lock of the baseline (DESIGN-A §4, §14.5): it is taken at the start of a run to hand out the next
/// <c>acq</c>, and again at record time. It is never held while sampling. An interface so that a test can run something at the
/// moment a lock is taken and so simulate the interleaving of two runs.
/// </summary>
internal interface IBaselineLock
{
    /// <summary>The lock, held until the returned object is disposed, or null when it could not be taken within <paramref name="timeout"/>.</summary>
    IDisposable? TryAcquire(TimeSpan timeout);
}

/// <summary>
/// The lock is a file inside the baseline folder, <c>.lock</c>, opened exclusively (<see cref="FileShare.None"/>) and held open until
/// the lock is released. Whoever opens it first holds the lock; the others retry at a short interval until their deadline. The lock is
/// the folder's own, so every spelling of the path (a trailing separator, another case, a <c>..</c>) is the same file and the same
/// lock, and any user or session that can write the folder takes part (a named mutex would be per session and per name).
///
/// There is no owner to die: when the holder ends, however it ends, Windows closes its handles and the file is free at once, so
/// there is no abandoned state to recover from. The file itself stays (empty) and is never deleted, so no one can take a lock on a
/// file that another one is just removing. Its name matches neither a disk history (<c>disk-*.json</c>) nor a temporary file
/// (<c>*.tmp</c>), so nothing that lists the folder reads, counts or cleans it. What the lock guards is files that are replaced whole,
/// so a lock that could not be taken is never "forced": the run compares and records nothing (<c>baseline_read_failed</c>).
/// </summary>
internal sealed class FileBaselineLock : IBaselineLock
{
    /// <summary>The name of the lock file in the baseline folder.</summary>
    public const string FileName = ".lock";

    /// <summary>How long to wait between two tries. Short, because the lock is held for a few file reads and writes only.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    private const int ErrorSharingViolation = 0x20;
    private const int ErrorLockViolation = 0x21;

    private readonly string _directory;
    private readonly string _path;
    private readonly Action<TimeSpan> _sleep;

    /// <param name="directory">The baseline folder (<c>&lt;root&gt;\baseline</c>). It is created when it is not there yet.</param>
    /// <param name="sleep">Waits between two tries. Tests give their own.</param>
    public FileBaselineLock(string directory, Action<TimeSpan>? sleep = null)
    {
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, FileName);
        _sleep = sleep ?? Thread.Sleep;
    }

    public IDisposable? TryAcquire(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1, FileOptions.None);
                return new Held(stream);
            }
            catch (IOException ex) when (IsHeldByAnother(ex))
            {
                // Someone has it: try again until the deadline.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return null; // the folder cannot be made or the file cannot be opened: waiting does not change that
            }

            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            _sleep(left < RetryInterval ? left : RetryInterval);
        }
    }

    /// <summary>A sharing violation or a lock violation: another handle has the file open without sharing. Nothing else counts as "busy".</summary>
    private static bool IsHeldByAnother(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private sealed class Held(FileStream stream) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                stream.Dispose();
            }
        }
    }
}
