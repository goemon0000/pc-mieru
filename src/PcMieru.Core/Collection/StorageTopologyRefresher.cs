using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Core.Collection;

/// <summary>
/// Keeps a <see cref="StorageTopology"/> up to date on its own background loop, never on the sampling thread: <see cref="Current"/> is only a
/// field read. The volumes (space) are read at most every <see cref="VolumeEveryMs"/>; the drives are re-checked every <see cref="DriveEveryMs"/>
/// (a missed device event costs at most that), or sooner, at most every <see cref="InvalidateGapMs"/>, after <see cref="Invalidate"/> says a
/// PhysicalDisk row has no drive here or the other way round. When the drives are read, the volumes are read in the same pass, so the numbers they
/// carry belong to the drives beside them. A failed read makes <see cref="Current"/> null (kinds unknown) until the next one succeeds: it never
/// keeps a stale answer and never guesses.
/// <see cref="Dispose"/> stops the loop at once and does not wait: a read that is still inside the OS call finishes on its own and its result is
/// thrown away.
/// </summary>
internal sealed class StorageTopologyRefresher : IDisposable
{
    internal const long VolumeEveryMs = 30_000;
    internal const long DriveEveryMs = 60_000;
    internal const long InvalidateGapMs = 10_000;

    /// <summary>How often the loop looks at the clock; a read only happens when its interval has passed.</summary>
    private const int PollMs = 1_000;

    private readonly IStorageTopologyReader _reader;
    private readonly Func<long> _nowMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startLock = new();
    private Task? _loop;

    // Only Step touches these, and Step never runs twice at once (one loop, or one test calling it).
    private IReadOnlyList<StorageDrive>? _drives;
    private IReadOnlyList<StorageVolumeUse>? _volumes;
    private long? _drivesAtMs;
    private long? _volumesAtMs;
    private volatile bool _invalidated;
    private volatile StorageTopology? _current;

    public StorageTopologyRefresher(IStorageTopologyReader reader, Func<long> nowMs)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>The topology as of the last pass; null until the first successful read and after a failed one.</summary>
    public StorageTopology? Current => _current;

    /// <summary>Ends when the loop has stopped (never started: already complete). Tests await it after <see cref="Dispose"/>.</summary>
    internal Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>Starts the loop once. A no-op after <see cref="Dispose"/>.</summary>
    public void Start()
    {
        lock (_startLock)
        {
            if (_loop is not null || _cts.IsCancellationRequested)
            {
                return;
            }

            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Asks for the drives to be read again soon (a PhysicalDisk row and the known drives disagree). Cheap: sets a flag.</summary>
    public void Invalidate() => _invalidated = true;

    /// <summary>
    /// Compares the drive numbers of the PhysicalDisk rows with the known drives and calls <see cref="Invalidate"/> when they differ (a drive
    /// came or went). A row that is not <c>PhysicalDrive&lt;n&gt;</c> is ignored. Cheap and I/O free, so the sampling thread may call it.
    /// </summary>
    public void NoteDisks(IReadOnlyList<DiskInfo> disks)
    {
        var known = _current;
        if (known is null)
        {
            return;
        }

        var rows = new HashSet<int>();
        foreach (var d in disks)
        {
            if (StorageTopology.TryDriveNumber(d.Id, out var n))
            {
                rows.Add(n);
            }
        }

        if (!rows.SetEquals(known.Drives.Select(static d => d.Number)))
        {
            Invalidate();
        }
    }

    /// <summary>One pass of the loop: reads whatever is due and publishes. Public to the tests; production calls it from the loop only.</summary>
    internal void Step()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        var now = _nowMs();
        var invalidated = _invalidated && (_drivesAtMs is not { } last || now - last >= InvalidateGapMs);
        var drivesDue = _drivesAtMs is not { } d || now - d >= DriveEveryMs || invalidated;
        var volumesDue = drivesDue || _volumesAtMs is not { } v || now - v >= VolumeEveryMs;
        if (!volumesDue)
        {
            return;
        }

        if (drivesDue)
        {
            _invalidated = false;
            _drivesAtMs = now;
            _drives = SafeRead(_reader.ReadDrives);
        }

        _volumesAtMs = now;
        _volumes = _drives is null ? null : SafeRead(_reader.ReadVolumes);

        // A pass that finished after Dispose publishes nothing.
        if (!_cts.IsCancellationRequested)
        {
            _current = _drives is null ? null : new StorageTopology(_drives, _volumes);
        }
    }

    public void Dispose() => _cts.Cancel();

    private static T? SafeRead<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        // A reader that throws is "could not read": the topology goes unknown, the source never fails over it.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Step();
            try
            {
                await Task.Delay(PollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
