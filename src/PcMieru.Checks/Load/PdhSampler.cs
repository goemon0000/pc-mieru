using System.Diagnostics;
using System.Runtime.ExceptionServices;
using PcMieru.Core.Collection;

namespace PcMieru.Checks.Load;

/// <summary>
/// Takes the PDH (and Energy Meter) sample of the CPU load off the control path (DESIGN-A §6.2). The call into the counters can hang and no
/// code of ours can interrupt it, so it never runs on the thread that keeps the load's time: it runs on a thread of its own, and the control
/// loop asks for the answer with a bounded wait.
/// <list type="bullet">
/// <item>One request at a time. While the last call has not come back, the next ask is a missing sample at once: calls do not pile up behind a stuck one.</item>
/// <item>An answer that does not arrive within the wait, or a cancel that cuts the wait short, is a missing sample, never 0. If the stuck call comes back later, its answer is dropped.</item>
/// <item>A stuck call is never waited for: <see cref="Dispose"/> does not join the thread, which is a background one and ends when (if) the call returns.</item>
/// </list>
/// </summary>
internal sealed class PdhSampler : IDisposable
{
    private readonly Func<CpuPdhSample> _sample;
    private readonly CancellationTokenRegistration _onCancel;
    private readonly CancellationToken _ct;
    private readonly object _gate = new();
    private Thread? _thread;
    private bool _requested;
    private bool _inCall;
    private bool _abandoned;
    private bool _closed;
    private bool _hasAnswer;
    private CpuPdhSample _answer;
    private Exception? _fatal;

    /// <param name="sample">The session's own call (<see cref="ICpuTestSession.SamplePdh"/>).</param>
    /// <param name="ct">The run's token: it cuts a wait short.</param>
    public PdhSampler(Func<CpuPdhSample> sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sample);
        _sample = sample;
        _ct = ct;
        _onCancel = ct.Register(() =>
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        });
    }

    /// <summary>Whether a call that was started has not come back yet (a stuck one, or the one being waited for).</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _requested || _inCall;
            }
        }
    }

    /// <summary>
    /// One sample, asked for now: the answer if it arrives within <paramref name="wait"/> (<see cref="Timeout.InfiniteTimeSpan"/>: only a
    /// cancel ends the wait), otherwise a missing sample. Real time: this is a wait for another thread, not a tick of the load's clock.
    /// </summary>
    /// <exception cref="OutOfMemoryException">The call itself ran out of memory: that is not a missing sample, it is rethrown here, as it would have been on this thread.</exception>
    public CpuPdhSample Take(TimeSpan wait)
    {
        lock (_gate)
        {
            ThrowFatal();
            if (_closed || _requested || _inCall)
            {
                return CpuPdhSample.Missing();
            }

            _hasAnswer = false;
            _abandoned = false;
            _requested = true;
            _thread ??= StartThread();
            Monitor.PulseAll(_gate);

            var begin = Stopwatch.GetTimestamp();
            while (!_hasAnswer && _fatal is null && !_closed && !_ct.IsCancellationRequested)
            {
                if (wait == Timeout.InfiniteTimeSpan)
                {
                    Monitor.Wait(_gate);
                    continue;
                }

                var left = wait - Stopwatch.GetElapsedTime(begin);
                if (left <= TimeSpan.Zero)
                {
                    break;
                }

                Monitor.Wait(_gate, left);
            }

            ThrowFatal();
            if (_hasAnswer)
            {
                _hasAnswer = false;
                return _answer;
            }

            _abandoned = true; // the call is still out there: when it returns, its answer is for nobody
            return CpuPdhSample.Missing();
        }
    }

    /// <summary>Stops asking. A call in flight is not waited for: its thread ends by itself when the call returns.</summary>
    public void Dispose()
    {
        _onCancel.Dispose();
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void ThrowFatal()
    {
        if (_fatal is { } fatal)
        {
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }
    }

    private Thread StartThread()
    {
        var thread = new Thread(Serve) { IsBackground = true, Name = "PcMieru.CpuLoad.Sampler" };
        thread.Start();
        return thread;
    }

    private void Serve()
    {
        while (true)
        {
            lock (_gate)
            {
                while (!_requested && !_closed)
                {
                    Monitor.Wait(_gate);
                }

                if (_closed)
                {
                    return;
                }

                _requested = false;
                _inCall = true;
            }

            var sample = CpuPdhSample.Missing();
            Exception? fatal = null;
            try
            {
                sample = _sample();
            }
            catch (Exception ex)
            {
                // What the PDH could not give is a missing sample, never a failed second of the load. Running out of memory is neither: it is
                // handed to the control loop, which fails the run (this thread must not end the process).
                if (ex is OutOfMemoryException)
                {
                    fatal = ex;
                }
            }

            lock (_gate)
            {
                _inCall = false;
                if (fatal is not null)
                {
                    _fatal = fatal;
                }
                else if (!_abandoned && !_closed)
                {
                    _answer = sample;
                    _hasAnswer = true;
                }

                Monitor.PulseAll(_gate);
            }
        }
    }
}
