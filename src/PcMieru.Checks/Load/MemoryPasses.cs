namespace PcMieru.Checks.Load;

/// <summary>The stop flag of a memory run. Set by the cancel, by the deadline, by a fall of the free memory, by a fault and at the end of the control loop.</summary>
internal sealed class MemoryStop
{
    private int _set;

    public bool IsSet => Volatile.Read(ref _set) != 0;

    public void Request() => Volatile.Write(ref _set, 1);
}

/// <summary>
/// The passes of the memory test over the borrowed blocks, shared by every work thread (DESIGN-A §6.2). A pass is a write phase and then a
/// verify phase over all blocks; the pattern kind rotates from one pass to the next and every pass has a seed of its own.
/// <list type="bullet">
/// <item>The work is cut into slices (4 MiB in a real run). A thread takes the next slice of the phase under a lock, does it outside the lock and
/// reports it under the lock; a phase ends when its last slice is reported, and the thread that reports it opens the next phase. A thread that
/// finds no slice left while another still works on the last ones waits for that phase to end.</item>
/// <item>A thread looks at the stop flag and at the absolute deadline between two slices, never inside one.</item>
/// <item>A phase that was cut short keeps what it did: the bytes of the slices that were reported, and the time up to the last of them.</item>
/// </list>
/// </summary>
internal sealed class MemoryPasses
{
    private static readonly PatternKind[] Kinds = Enum.GetValues<PatternKind>();

    private readonly object _gate = new();
    private readonly IReadOnlyList<IMemoryBlock> _blocks;
    private readonly IMemoryTestSession _session;
    private readonly ILoadClock _clock;
    private readonly MemoryStop _stop;
    private readonly long _startStamp;
    private readonly TimeSpan _duration;
    private readonly int _wordsPerBlock;
    private readonly int _sliceWords;
    private readonly int _slicesPerBlock;
    private readonly long _slicesPerPhase;

    private int _pass;
    private PatternKind _kind;
    private ulong _seed;
    private bool _verifying;
    private long _cursor;
    private int _inFlight;
    private long _phaseBytes;
    private TimeSpan _phaseStart;
    private TimeSpan _lastDone;

    private long _passes;
    private long _writeBytes;
    private double _writeSeconds;
    private long _verifiedBytes;
    private double _verifySeconds;
    private long _mismatches;
    private long _processedBytes;

    public MemoryPasses(IReadOnlyList<IMemoryBlock> blocks, IMemoryTestSession session, MemoryStop stop, long startStamp, TimeSpan duration, int sliceWords)
    {
        _blocks = blocks;
        _session = session;
        _clock = session.Clock;
        _stop = stop;
        _startStamp = startStamp;
        _duration = duration;
        _wordsPerBlock = checked((int)(session.BlockBytes / sizeof(ulong)));
        _sliceWords = Math.Clamp(sliceWords, 1, _wordsPerBlock);
        _slicesPerBlock = (_wordsPerBlock + _sliceWords - 1) / _sliceWords;
        _slicesPerPhase = (long)blocks.Count * _slicesPerBlock;
        _kind = Kinds[0];
        _seed = session.NextSeed();
        _phaseStart = _clock.Elapsed(startStamp);
        _lastDone = _phaseStart;
    }

    /// <summary>Passes whose write and verify both finished over the whole borrowed range.</summary>
    public long Passes => Locked(() => _passes);

    /// <summary>Bytes written and bytes verified so far, the slices of a phase that is still running included. For the pace shown while the run goes on.</summary>
    public long ProcessedBytes
    {
        get
        {
            lock (_gate)
            {
                return _processedBytes;
            }
        }
    }

    /// <summary>
    /// One slice of work for one thread: the step of a work thread. False when the thread is to end (the stop flag, or the deadline). It
    /// never catches anything: an exception ends the thread and reaches its fault handler.
    /// </summary>
    public bool Step()
    {
        if (_stop.IsSet)
        {
            return false;
        }

        if (_clock.Elapsed(_startStamp) >= _duration)
        {
            _stop.Request();
            return false;
        }

        long slice;
        PatternKind kind;
        ulong seed;
        bool verifying;
        lock (_gate)
        {
            if (_cursor >= _slicesPerPhase)
            {
                slice = -1; // every slice of the phase is taken and some are not done yet: the one who finishes the last opens the next phase
                kind = default;
                seed = 0;
                verifying = false;
            }
            else
            {
                slice = _cursor++;
                _inFlight++;
                kind = _kind;
                seed = _seed;
                verifying = _verifying;
            }
        }

        if (slice < 0)
        {
            Thread.Yield();
            return true;
        }

        var block = (int)(slice / _slicesPerBlock);
        var first = (int)(slice % _slicesPerBlock) * _sliceWords;
        var count = Math.Min(_sliceWords, _wordsPerBlock - first);
        var baseIndex = ((long)block * _wordsPerBlock) + first;
        long bad = 0;
        var span = _blocks[block].Words(first, count);
        if (verifying)
        {
            // Every mismatch is counted; none is kept: what the result may say about one is that there were some.
            bad = Patterns.Verify(span, kind, seed, baseIndex, keep: 0).Mismatches;
        }
        else
        {
            Patterns.Fill(span, kind, seed, baseIndex);
        }

        Report(count * (long)sizeof(ulong), bad);
        return true;
    }

    /// <summary>
    /// After every thread has ended: the phase that was cut short is folded into the totals with what it did. Whole phases were folded when they
    /// ended, so a verify that was cut short counts for its bytes and does not make a pass.
    /// </summary>
    public void Settle()
    {
        lock (_gate)
        {
            if (_phaseBytes > 0)
            {
                Fold(_verifying, _phaseBytes, Math.Max(0, (_lastDone - _phaseStart).TotalSeconds));
                _phaseBytes = 0;
            }
        }
    }

    public long WriteBytes => Locked(() => _writeBytes);

    public double WriteSeconds => Locked(() => _writeSeconds);

    public long VerifiedBytes => Locked(() => _verifiedBytes);

    public double VerifySeconds => Locked(() => _verifySeconds);

    public long Mismatches => Locked(() => _mismatches);

    private T Locked<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    private void Report(long bytes, long mismatches)
    {
        lock (_gate)
        {
            _inFlight--;
            _phaseBytes += bytes;
            _processedBytes += bytes;
            _mismatches += mismatches;
            _lastDone = _clock.Elapsed(_startStamp);
            if (_cursor >= _slicesPerPhase && _inFlight == 0)
            {
                EndPhase();
            }
        }
    }

    // Under the lock: the last slice of the phase was reported.
    private void EndPhase()
    {
        Fold(_verifying, _phaseBytes, Math.Max(0, (_lastDone - _phaseStart).TotalSeconds));
        if (_verifying)
        {
            _passes++; // the write and the verify of the whole range are both done
            _pass++;
            _kind = Kinds[_pass % Kinds.Length];
            _seed = _session.NextSeed();
            _verifying = false;
        }
        else
        {
            _verifying = true;
        }

        _cursor = 0;
        _phaseBytes = 0;
        _phaseStart = _lastDone;
    }

    private void Fold(bool verifying, long bytes, double seconds)
    {
        if (verifying)
        {
            _verifiedBytes += bytes;
            _verifySeconds += seconds;
        }
        else
        {
            _writeBytes += bytes;
            _writeSeconds += seconds;
        }
    }
}
