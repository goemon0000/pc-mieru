using System.Diagnostics;
using System.Text;
using PcMieru.Core;

namespace PcMieru.Checks;

/// <summary>Everything the writer is built from (the recorder fills it in at the start of a run).</summary>
internal sealed record RunWriterInit(
    IRunFileSystem FileSystem,
    Guid RunId,
    string App,
    DateTime StartedAtUtc,
    long StartUs,
    string JsonPath,
    string TempPath,
    string CsvName,
    IRunFile Csv,
    long FlushIntervalUs,
    long MinFreeBytes,
    int QueueCapacity,
    Action<RunStopReason> RequestStop,
    Func<long> NowUs,
    Action<TimeSpan>? Pause,
    long MaxCsvBytes,
    int IdleWaitMs);

/// <summary>What the writer did, read after it has stopped.</summary>
internal sealed record WriterOutcome(bool Ok, Exception? Failure, long Rows, long DroppedTicks, long LastRowOffsetUs, long LastMarkerOffsetUs);

/// <summary>
/// The one thread that touches a run's files while it is recording (DESIGN-B §7.1). The sampler's handler only enqueues (never blocks, never does I/O);
/// this thread writes the CSV rows, flushes every <see cref="RunWriterInit.FlushIntervalUs"/> (also when no sample comes: the wait for the next item is
/// bounded by <see cref="RunWriterInit.IdleWaitMs"/>, so rows reach the disk within about one interval even if the samples stop) and at the end (a flush and
/// then a force to the disk), saves the JSON atomically (a <c>.tmp</c> written, forced and renamed over it) at every flush and every mark, and checks the free
/// space at every flush. A write error ends the writing, asks the recorder for an <c>io_error</c> stop and leaves what was already saved readable. A JSON
/// replace that meets a sharing violation or an access denied is tried again (<see cref="RunFiles.SaveAtomic"/>); if a periodic flush's save still fails
/// it is put off to the next flush (the CSV is the truth, the JSON is rebuilt from it by the recovery), while a mark's save and the last save fail the run.
/// </summary>
internal sealed class RunWriter
{
    /// <summary>The CSV waits at most this many accepted samples for a sample whose GPU stage completed, then its columns are fixed with the GPUs it has.</summary>
    internal const int LayoutWaitTicks = 3;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly RunWriterInit _init;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly LinkedList<Item> _queue = new();
    private readonly TaskCompletionSource<WriterOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RunMarker> _markers = [];
    private readonly HashSet<string> _warnedGpus = new(StringComparer.Ordinal);
    private readonly List<Accepted> _pending = [];
    private readonly Thread _thread;

    private int _sampleCount;
    private bool _finishing;
    private bool _started;
    private int _deferredSaves;

    // The state below belongs to the writer thread while it runs; it is read by others only before Start or after the thread has ended.
    private IRunFile? _csv;
    private RunLayout? _layout;
    private RunRowFormatter? _formatter;
    private long _rows;
    private long _rowsFlushed;
    private long _csvBytes;
    private long _dropped;
    // No row yet: below every offset, so the backlog rule (a sample acquired before the start) is the one thing that keeps a pre-start sample out.
    private long _lastOffsetUs = long.MinValue;
    // The offset of the last ACCEPTED sample (a sample waiting for the columns is accepted but not yet a row): the order rule is checked against this one.
    private long _lastAcceptedUs = long.MinValue;
    private long? _lastSequence;
    private long? _lastFlushStampUs;
    private Exception? _failure;
    private bool _stopRequested;
    private bool _sizeCapReached;
    private bool _jsonDeferred;

    public RunWriter(RunWriterInit init)
    {
        _init = init;
        _csv = init.Csv;
        _directory = Path.GetDirectoryName(Path.GetFullPath(init.JsonPath)) ?? ".";
        _thread = new Thread(Loop, 256 * 1024) { IsBackground = true, Name = "PCMieru run writer" };
    }

    private abstract record Item;

    private sealed record SampleItem(RecordingSample Sample, long NowUs) : Item;

    private sealed record MarkerItem(MarkerKind Kind, long OffsetUs, DateTime Utc, string? Detail, TaskCompletionSource<RunMarker> Done) : Item;

    private sealed record FinishItem : Item;

    /// <summary>A sample that passed the order and backlog rules: its row time, and the time it was queued (the flush clock).</summary>
    private sealed record Accepted(RecordingSample Sample, long OffsetUs, long NowUs);

    /// <summary>How many periodic JSON saves were put off to the next flush because the replace kept failing for a moment (a count for tests and the trace).</summary>
    public int DeferredSaves => Volatile.Read(ref _deferredSaves);

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _thread.Start();
    }

    /// <summary>Never blocks and never does I/O. When the queue is full the OLDEST waiting sample goes (a jump in the sequence shows it, and the writer counts it as a dropped tick).</summary>
    public void EnqueueSample(RecordingSample sample, long nowUs)
    {
        lock (_gate)
        {
            if (_finishing)
            {
                return;
            }

            if (_sampleCount >= _init.QueueCapacity)
            {
                for (var node = _queue.First; node is not null; node = node.Next)
                {
                    if (node.Value is SampleItem)
                    {
                        _queue.Remove(node);
                        _sampleCount--;
                        break;
                    }
                }
            }

            _queue.AddLast(new SampleItem(sample, nowUs));
            _sampleCount++;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Queues a mark behind the samples that came before it. The task finishes when the JSON with the mark is on the disk. A mark is never dropped.</summary>
    public Task<RunMarker> EnqueueMarker(MarkerKind kind, long offsetUs, DateTime utc, string? detail)
    {
        var done = new TaskCompletionSource<RunMarker>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_finishing)
            {
                done.SetException(new InvalidOperationException("The run is stopping or has ended."));
                return done.Task;
            }

            _queue.AddLast(new MarkerItem(kind, Math.Max(0, offsetUs), utc, detail, done));
            Monitor.Pulse(_gate);
        }

        return done.Task;
    }

    /// <summary>Queues the end: everything before it is written, the CSV is forced to the disk and the file is closed. Calling it again waits for the same end.</summary>
    public Task<WriterOutcome> FinishAsync()
    {
        lock (_gate)
        {
            if (!_finishing)
            {
                _finishing = true;
                _queue.AddLast(new FinishItem());
                Monitor.Pulse(_gate);
            }
        }

        return _done.Task;
    }

    /// <summary>For a start that failed before <see cref="Start"/>: closes the CSV. Never call it after Start.</summary>
    public void Abort()
    {
        var csv = Interlocked.Exchange(ref _csv, null);
        csv?.Dispose();
    }

    /// <summary>
    /// Saves the JSON atomically (a sharing violation or access denied on the replace is tried again, see <see cref="RunFiles.SaveAtomic"/>). Call it
    /// before <see cref="Start"/> (the first one) or after <see cref="FinishAsync"/> has finished (the last one); in between only the writer thread saves.
    /// </summary>
    public void SaveJson(RunStatus status, RunStopReason? reason, DateTime? endedAtUtc, long durationUs)
    {
        var header = new RunHeader(
            _init.RunId,
            _init.App,
            _init.StartedAtUtc,
            endedAtUtc,
            durationUs,
            status,
            reason,
            _dropped,
            _rows,
            _init.CsvName,
            _layout?.Metrics ?? [],
            _layout?.SourceInfos ?? [],
            _markers.ToArray());
        RunFiles.SaveAtomic(_init.FileSystem, _init.TempPath, _init.JsonPath, RunJson.Write(header), _init.Pause);
        _jsonDeferred = false;
    }

    private void Loop()
    {
        try
        {
            while (true)
            {
                Item? item = null;
                lock (_gate)
                {
                    while (_queue.Count == 0)
                    {
                        // A wait that ran out with nothing queued is an idle moment: the writer flushes what is due instead of waiting for a sample that may never come.
                        if (!Monitor.Wait(_gate, _init.IdleWaitMs))
                        {
                            break;
                        }
                    }

                    if (_queue.Count > 0)
                    {
                        item = _queue.First!.Value;
                        _queue.RemoveFirst();
                        if (item is SampleItem)
                        {
                            _sampleCount--;
                        }
                    }
                }

                if (item is null)
                {
                    Idle();
                    continue;
                }

                if (item is FinishItem)
                {
                    break;
                }

                try
                {
                    switch (item)
                    {
                        case SampleItem sample:
                            HandleSample(sample);
                            break;
                        case MarkerItem marker:
                            HandleMarker(marker);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    if (item is MarkerItem failed)
                    {
                        failed.Done.TrySetException(ex);
                    }
                }
            }

            FinalFlush();
        }
        catch (Exception ex)
        {
            _failure ??= ex;
        }
        finally
        {
            try
            {
                Interlocked.Exchange(ref _csv, null)?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failure ??= ex;
            }

            // A mark that was queued but never handled (the thread ended on an unexpected error) must not wait forever.
            lock (_gate)
            {
                foreach (var left in _queue)
                {
                    if (left is MarkerItem marker)
                    {
                        marker.Done.TrySetException(new InvalidOperationException("The run has ended."));
                    }
                }
            }

            _done.TrySetResult(new WriterOutcome(
                _failure is null,
                _failure,
                _rows,
                _dropped,
                Math.Max(0, _lastOffsetUs),
                _markers.Count == 0 ? 0 : _markers.Max(m => m.OffsetUs)));
        }
    }

    /// <summary>Nothing came for a while: samples that are still waiting for the columns are written with what there is, and a flush that is due is done.</summary>
    private void Idle()
    {
        if (_failure is not null)
        {
            return;
        }

        try
        {
            var now = _init.NowUs();
            if (_layout is null)
            {
                if (_pending.Count > 0 && now - _pending[0].NowUs >= _init.FlushIntervalUs)
                {
                    CommitLayout(UnionOfPendingGpus());
                }

                return;
            }

            if ((_rows != _rowsFlushed || _jsonDeferred) && now - (_lastFlushStampUs ?? now) >= _init.FlushIntervalUs)
            {
                Flush(now);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void HandleSample(SampleItem item)
    {
        if (_failure is not null || _sizeCapReached)
        {
            return;
        }

        var sample = item.Sample;
        long offsetUs;
        if (sample.Core.Frame is { } frame)
        {
            // The backlog rule: a sample that was acquired before the run started belongs to nobody's recording (the sampler's queue can still hold
            // one from before the subscription). It is dropped here, by the time it was acquired, and is not counted as a dropped tick.
            if (sample.Core.AcquiredUs < _init.StartUs)
            {
                return;
            }

            offsetUs = sample.Core.AcquiredUs - _init.StartUs;
            if (offsetUs <= _lastAcceptedUs)
            {
                return;
            }

            // The sampler's frame sequence is a counter that is never reset: a number that is not above the last one is a replay, and a jump means a
            // tick was dropped (the 64-deep queue overflowed) or failed. No row is invented for it; it is only counted.
            if (_lastSequence is { } last)
            {
                if (frame.Sequence <= last)
                {
                    return;
                }

                if (frame.Sequence > last + 1)
                {
                    _dropped += frame.Sequence - last - 1;
                }
            }

            _lastSequence = frame.Sequence;
        }
        else
        {
            // No Core frame at all (not expected from the real sampler): the row gets the time it was queued.
            offsetUs = Math.Max(Math.Max(0, item.NowUs - _init.StartUs), _lastAcceptedUs + 1);
        }

        _lastAcceptedUs = offsetUs;
        var accepted = new Accepted(sample, offsetUs, item.NowUs);

        // The columns are fixed at the first accepted sample whose GPU stage completed (DESIGN-B §7.1): a sample whose GPU stage timed out or has not
        // started yet has no GPUs in it, and fixing the columns from it would leave every adapter without a column for the whole run. It waits for the
        // next sample, at most LayoutWaitTicks accepted samples in all, then the columns are fixed with the GPUs there are. The samples that waited are
        // written first, in order: none is lost.
        if (_layout is null)
        {
            _pending.Add(accepted);
            if (sample.Stages.GpuCounterUs != 0)
            {
                CommitLayout(sample.Gpus);
            }
            else if (_pending.Count >= LayoutWaitTicks)
            {
                CommitLayout(UnionOfPendingGpus());
            }

            return;
        }

        var urgent = WriteRow(accepted);
        CheckSizeCap();

        // The flush clock is the time the sample was queued (not the time it was acquired), so a stalled thread cannot hide a due flush and a test
        // moves it by moving the fake clock: nothing here polls or sleeps.
        if (urgent || item.NowUs - (_lastFlushStampUs ?? item.NowUs) >= _init.FlushIntervalUs)
        {
            Flush(item.NowUs);
        }
    }

    private IReadOnlyList<RecordedGpu> UnionOfPendingGpus() =>
        _pending.SelectMany(static p => p.Sample.Gpus).DistinctBy(static g => g.Id, StringComparer.Ordinal).ToList();

    /// <summary>Fixes the columns, writes the header and the samples that waited, and flushes at once (the header, the JSON's metrics and sources go out together, so a run that dies a second later is still readable).</summary>
    private void CommitLayout(IReadOnlyList<RecordedGpu> gpus)
    {
        _layout = RunLayout.Create(gpus);
        _formatter = new RunRowFormatter(_layout);
        Write(_layout.HeaderLine());
        foreach (var waiting in _pending)
        {
            WriteRow(waiting);
        }

        var stamp = _pending[^1].NowUs;
        _pending.Clear();
        CheckSizeCap();
        Flush(stamp);
    }

    /// <summary>Writes one row. Returns true when a warning was added (the JSON should carry it soon).</summary>
    private bool WriteRow(Accepted accepted)
    {
        var sample = accepted.Sample;
        var urgent = false;

        // A graphics driver restart gives an adapter a new id: the column of the adapter that went away goes on with it (only when the match is unambiguous).
        foreach (var (_, gpu) in _layout!.RebindLate(sample.Gpus))
        {
            _markers.Add(new RunMarker(MarkerKind.Warning, accepted.OffsetUs, sample.TakenAtUtc, "gpu_rebound:" + gpu.Name));
            urgent = true;
        }

        // A GPU that appears after the columns were fixed gets none; the run says so once.
        foreach (var gpu in sample.Gpus)
        {
            if (_layout.SourceOfGpu(gpu.Id) < 0 && _warnedGpus.Add(gpu.Id))
            {
                _markers.Add(new RunMarker(MarkerKind.Warning, accepted.OffsetUs, sample.TakenAtUtc, "late_gpu:" + gpu.Name));
                urgent = true;
            }
        }

        Write(_formatter!.Format(sample, accepted.OffsetUs, _init.StartUs) + "\n");
        _rows++;
        _lastOffsetUs = accepted.OffsetUs;
        return urgent;
    }

    /// <summary>The CSV stops growing at the recorder's own cap, below the size the reader accepts: the run ends with <c>size_limit</c> and stays readable.</summary>
    private void CheckSizeCap()
    {
        if (_sizeCapReached || _csvBytes < _init.MaxCsvBytes)
        {
            return;
        }

        _sizeCapReached = true;
        if (!_stopRequested)
        {
            _stopRequested = true;
            _init.RequestStop(RunStopReason.SizeLimit);
        }
    }

    private void HandleMarker(MarkerItem item)
    {
        if (_failure is not null)
        {
            item.Done.TrySetException(new IOException("The recording writer has failed.", _failure));
            return;
        }

        var marker = new RunMarker(item.Kind, item.OffsetUs, item.Utc, item.Detail);
        _markers.Add(marker);
        try
        {
            SaveJson(RunStatus.Recording, null, null, Math.Max(0, _lastOffsetUs));
        }
        catch
        {
            _markers.Remove(marker);
            throw;
        }

        item.Done.TrySetResult(marker);
    }

    private void Write(string text)
    {
        var csv = _csv ?? throw new ObjectDisposedException(nameof(RunWriter));
        var bytes = Utf8.GetBytes(text);
        csv.Write(bytes);
        _csvBytes += bytes.Length;
    }

    /// <summary>
    /// The CSV is written and forced to the disk, the JSON is saved, and the free space is checked (below the limit asks for a <c>low_space</c> stop).
    /// A JSON replace that still meets a sharing violation or an access denied after its retries is put off to the next flush: the CSV is on the disk and is
    /// the truth, so the run goes on. Any other failure of the save ends the run.
    /// </summary>
    private void Flush(long stampUs)
    {
        var csv = _csv ?? throw new ObjectDisposedException(nameof(RunWriter));
        csv.FlushToDisk();
        _rowsFlushed = _rows;
        try
        {
            SaveJson(RunStatus.Recording, null, null, Math.Max(0, _lastOffsetUs));
        }
        catch (Exception ex) when (RunFiles.IsTransientFailure(ex))
        {
            _jsonDeferred = true;
            Interlocked.Increment(ref _deferredSaves);
            Trace.TraceWarning("PCMieru: the run's JSON could not be replaced ({0}); it is saved again at the next flush.", ex.Message);
        }

        _lastFlushStampUs = stampUs;

        if (_init.FileSystem.GetFreeBytes(_directory) is { } free && free < _init.MinFreeBytes && !_stopRequested)
        {
            _stopRequested = true;
            _init.RequestStop(RunStopReason.LowSpace);
        }
    }

    private void FinalFlush()
    {
        if (_failure is not null || _csv is not { } csv)
        {
            return;
        }

        try
        {
            if (_layout is null && _pending.Count > 0)
            {
                // The samples stopped before the columns were fixed: they are written with the GPUs they have.
                CommitLayout(UnionOfPendingGpus());
            }

            csv.FlushToDisk();
        }
        catch (Exception ex)
        {
            _failure = ex;
        }
    }

    /// <summary>The writing ends: later samples are ignored, later marks fail, and the recorder is asked to stop with <c>io_error</c> (once).</summary>
    private void Fail(Exception ex)
    {
        _failure ??= ex;
        if (!_stopRequested)
        {
            _stopRequested = true;
            _init.RequestStop(RunStopReason.IoError);
        }
    }
}
