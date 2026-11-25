using PcMieru.Core.Hardware.Collectors;

namespace PcMieru.Core.Collection;

/// <summary>
/// Keeps a <see cref="StorageTopology"/> up to date on its own background loop, never on the sampling thread: <see cref="Current"/> and
/// <see cref="Settled"/> are only field reads. The drives are re-checked every <see cref="DriveEveryMs"/> (a missed device event costs at most
/// that), or sooner, at most every <see cref="InvalidateGapMs"/>, after <see cref="Invalidate"/> says a PhysicalDisk row has no drive here or the
/// other way round. The volumes (space) are read at most every <see cref="VolumeEveryMs"/>, counted from the start of the previous volume read, an
/// early drive read included: when the drives are read and the volumes are not due, the topology is published with <c>Volumes = null</c> (unknown:
/// the old list may name the old drives) until the next due volume read fills them in. The regular drive pass is always at least that far after the
/// last volume read, so it reads both. A failed read makes <see cref="Current"/> null (kinds unknown) until the next one succeeds: it never keeps a
/// stale answer and never guesses.
/// <para>
/// <see cref="Current"/> is the last read, which may be older than the drives on the machine. <see cref="Settled"/> is that same topology only
/// when it can be tied to the PhysicalDisk rows being sampled now: <see cref="NoteDisks"/> counts a generation each time the set of row numbers
/// changes from one frame to the next (a drive came, went, or was swapped for another under the same number), and a drive read is stamped with the
/// generation at its start. <see cref="Settled"/> is null until a read that started after the last change has been published, so a reused drive
/// number never earns for the old drive's kind: a kind may be missing for a moment, it is never the wrong one.
/// </para>
/// <see cref="Dispose"/> stops the loop at once and does not wait: a read that is still inside the OS call finishes on its own and its result is
/// thrown away. Publishing and <see cref="Dispose"/> exclude each other, so nothing is published after <see cref="Dispose"/> returns and
/// <see cref="Current"/> and <see cref="Settled"/> are null from then on.
/// </summary>
internal sealed class StorageTopologyRefresher : IDisposable
{
    internal const long VolumeEveryMs = 30_000;
    internal const long DriveEveryMs = 60_000;
    internal const long InvalidateGapMs = 10_000;

    /// <summary>How often the loop looks at the clock; a read only happens when its interval has passed.</summary>
    private const int PollMs = 1_000;

    /// <summary>A published topology and the row generation that was current when the drive read behind it started.</summary>
    private sealed record Published(StorageTopology Topology, long Generation);

    private readonly IStorageTopologyReader _reader;
    private readonly Func<long> _nowMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startLock = new();
    private readonly object _publishLock = new();
    private Task? _loop;

    // Only Step touches these, and Step never runs twice at once (one loop, or one test calling it).
    private IReadOnlyList<StorageDrive>? _drives;
    private IReadOnlyList<StorageVolumeUse>? _volumes;
    private long _drivesGeneration;
    private long? _drivesAtMs;
    private long? _volumesAtMs;
    private volatile bool _invalidated;
    private volatile Published? _published;

    // Only NoteDisks touches the previous set (one sampling thread). The generation is read by Step and by Settled, on other threads.
    private HashSet<int>? _previousRows;
    private long _rowGeneration;

    public StorageTopologyRefresher(IStorageTopologyReader reader, Func<long> nowMs)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>The topology as of the last pass; null until the first successful read, after a failed one and after <see cref="Dispose"/>.</summary>
    public StorageTopology? Current => _published?.Topology;

    /// <summary>
    /// <see cref="Current"/> when its drive read started after the last change in the set of PhysicalDisk rows, null otherwise (a drive may have
    /// been swapped under a number: the kinds are unknown until a read that started after the change is published). What the board should use.
    /// </summary>
    public StorageTopology? Settled
    {
        get
        {
            var published = _published;
            return published is not null && published.Generation == Volatile.Read(ref _rowGeneration) ? published.Topology : null;
        }
    }

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
    /// Compares the drive numbers of the PhysicalDisk rows with the previous frame's and with the known drives. When a number appeared or vanished
    /// since the previous frame, the row generation moves on (so <see cref="Settled"/> is null until a later read is published) and the drives are
    /// asked for again; when they only differ from the known drives, the drives are asked for again. The first call only records the set. A row
    /// that is not <c>PhysicalDrive&lt;n&gt;</c> is ignored. Cheap and I/O free, so the sampling thread may call it (and only that one thread).
    /// </summary>
    public void NoteDisks(IReadOnlyList<DiskInfo> disks)
    {
        var rows = new HashSet<int>();
        foreach (var d in disks)
        {
            if (StorageTopology.TryDriveNumber(d.Id, out var n))
            {
                rows.Add(n);
            }
        }

        // Tracked even while nothing is published: a change that happens during the first read must still outdate it.
        var previous = _previousRows;
        _previousRows = rows;
        if (previous is not null && !rows.SetEquals(previous))
        {
            Interlocked.Increment(ref _rowGeneration);
            Invalidate();
            return;
        }

        if (_published is { } known && !rows.SetEquals(known.Topology.Drives.Select(static d => d.Number)))
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
        var volumesDue = _volumesAtMs is not { } v || now - v >= VolumeEveryMs;
        if (!drivesDue && (!volumesDue || _drives is null))
        {
            return;
        }

        if (drivesDue)
        {
            _invalidated = false;
            _drivesAtMs = now;

            // Taken before the read: a row change that lands while the read is inside the OS call leaves it with an older generation.
            _drivesGeneration = Volatile.Read(ref _rowGeneration);
            _drives = SafeRead(_reader.ReadDrives);

            // The old list may name the old drives; unknown until a volume read that follows these drives.
            _volumes = null;
            if (_cts.IsCancellationRequested)
            {
                return;
            }
        }

        // Space reads stay VolumeEveryMs apart even when an invalidation forced the drive read early; this pass then publishes Volumes = null.
        if (_drives is not null && volumesDue)
        {
            _volumesAtMs = _nowMs();
            _volumes = SafeRead(_reader.ReadVolumes);
        }

        Publish();
    }

    public void Dispose()
    {
        lock (_publishLock)
        {
            _cts.Cancel();
            _published = null;
        }
    }

    /// <summary>Publishes what Step holds; nothing after <see cref="Dispose"/>. Excludes <see cref="Dispose"/> so the check and the write are one step.</summary>
    private void Publish()
    {
        lock (_publishLock)
        {
            if (!_cts.IsCancellationRequested)
            {
                _published = _drives is null ? null : new Published(new StorageTopology(_drives, _volumes), _drivesGeneration);
            }
        }
    }

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
