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
    Action<RunStopReason> RequestStop);

/// <summary>What the writer did, read after it has stopped.</summary>
internal sealed record WriterOutcome(bool Ok, Exception? Failure, long Rows, long DroppedTicks, long LastRowOffsetUs, long LastMarkerOffsetUs);

/// <summary>
/// The one thread that touches a run's files while it is recording (DESIGN-B §7.1). The sampler's handler only enqueues (never blocks, never does I/O);
/// this thread writes the CSV rows, flushes every <see cref="RunWriterInit.FlushIntervalUs"/> and at the end (a flush and then a force to the disk),
/// saves the JSON atomically (a <c>.tmp</c> written, forced and renamed over it) at every flush and every mark, and checks the free space at every flush.
/// Any I/O failure ends the writing, asks the recorder for an <c>io_error</c> stop and leaves what was already saved readable.
/// </summary>
internal sealed class RunWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly RunWriterInit _init;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly LinkedList<Item> _queue = new();
    private readonly TaskCompletionSource<WriterOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RunMarker> _markers = [];
    private readonly HashSet<string> _warnedGpus = new(StringComparer.Ordinal);
    private readonly Thread _thread;

    private int _sampleCount;
    private bool _finishing;
    private bool _started;

    // The state below belongs to the writer thread while it runs; it is read by others only before Start or after the thread has ended.
    private IRunFile? _csv;
    private RunLayout? _layout;
    private RunRowFormatter? _formatter;
    private long _rows;
    private long _dropped;
    // No row yet: below every offset, so the backlog rule (a sample acquired before the start) is the one thing that keeps a pre-start sample out.
    private long _lastOffsetUs = long.MinValue;
    private long? _lastSequence;
    private long? _lastFlushStampUs;
    private Exception? _failure;
    private bool _stopRequested;

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
    /// Saves the JSON atomically. Call it before <see cref="Start"/> (the first one) or after <see cref="FinishAsync"/> has finished (the last one); in
    /// between only the writer thread saves.
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
        RunFiles.SaveAtomic(_init.FileSystem, _init.TempPath, _init.JsonPath, RunJson.Write(header));
    }

    private void Loop()
    {
        try
        {
            while (true)
            {
                Item item;
                lock (_gate)
                {
                    while (_queue.Count == 0)
                    {
                        Monitor.Wait(_gate);
                    }

                    item = _queue.First!.Value;
                    _queue.RemoveFirst();
                    if (item is SampleItem)
                    {
                        _sampleCount--;
                    }
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

    private void HandleSample(SampleItem item)
    {
        if (_failure is not null)
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
            if (offsetUs <= _lastOffsetUs)
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
            offsetUs = Math.Max(Math.Max(0, item.NowUs - _init.StartUs), _lastOffsetUs + 1);
        }

        // The first row fixes the columns (DESIGN-B §7.1); the header, the JSON's metrics and sources are written with it, so a run that dies a second
        // later is still readable. A flush is also due when a warning was added, so the JSON carries it.
        var urgent = _layout is null;
        if (_layout is null)
        {
            _layout = RunLayout.Create(sample.Gpus);
            _formatter = new RunRowFormatter(_layout);
            Write(_layout.HeaderLine());
        }
        else
        {
            // A GPU that appears after the columns were fixed gets none; the run says so once.
            foreach (var gpu in sample.Gpus)
            {
                if (_layout.SourceOfGpu(gpu.Id) < 0 && _warnedGpus.Add(gpu.Id))
                {
                    _markers.Add(new RunMarker(MarkerKind.Warning, offsetUs, sample.TakenAtUtc, "late_gpu:" + gpu.Name));
                    urgent = true;
                }
            }
        }

        Write(_formatter!.Format(sample, offsetUs, _init.StartUs) + "\n");
        _rows++;
        _lastOffsetUs = offsetUs;

        // The flush clock is the time the sample was queued (not the time it was acquired), so a stalled thread cannot hide a due flush and a test
        // moves it by moving the fake clock: nothing here polls or sleeps.
        if (urgent || item.NowUs - (_lastFlushStampUs ?? item.NowUs) >= _init.FlushIntervalUs)
        {
            Flush(item.NowUs);
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
        csv.Write(Utf8.GetBytes(text));
    }

    /// <summary>The CSV is written and forced to the disk, the JSON is saved, and the free space is checked (below the limit asks for a <c>low_space</c> stop).</summary>
    private void Flush(long stampUs)
    {
        var csv = _csv ?? throw new ObjectDisposedException(nameof(RunWriter));
        csv.FlushToDisk();
        SaveJson(RunStatus.Recording, null, null, Math.Max(0, _lastOffsetUs));
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
