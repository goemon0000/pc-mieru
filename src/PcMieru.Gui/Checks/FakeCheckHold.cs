namespace PcMieru.Gui;

/// <summary>
/// A way to hold ONE fake load run still, so the smoke run can look at a screen that a fake run passes through in a blink: a load that is
/// running with its progress on show, and a run that was told to stop and has not yet answered ("stopping"). Only the <c>--fakes</c> smoke
/// arms it; unarmed (the default, and every normal run) it does nothing. One arm holds one run, then it is spent.
/// </summary>
internal sealed class FakeCheckHold
{
    private readonly object _gate = new();
    private Gates? _armed;

    /// <summary>Holds the next load run the fake runner starts: it stops after its first progress step until <see cref="Gates.ReleaseRunning"/>, and after a stop until <see cref="Gates.ReleaseStopped"/>.</summary>
    public Gates Arm()
    {
        lock (_gate)
        {
            return _armed = new Gates();
        }
    }

    /// <summary>The runner takes the arm (null: nothing is armed). A taken arm is spent: the run after it is not held.</summary>
    internal Gates? Take()
    {
        lock (_gate)
        {
            var taken = _armed;
            _armed = null;
            return taken;
        }
    }

    /// <summary>The two gates of one held run.</summary>
    internal sealed class Gates
    {
        /// <summary>The longest a gate holds on its own, so a run that nobody releases (the smoke run was abandoned) still ends.</summary>
        internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

        private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the run reached the first gate (it reported its progress and waits).</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets the run go on (it was only held to be looked at).</summary>
        public void ReleaseRunning() => _running.TrySetResult();

        /// <summary>Lets a run that was told to stop finish with its stopped result. Also frees the first gate.</summary>
        public void ReleaseStopped()
        {
            _running.TrySetResult();
            _stopped.TrySetResult();
        }

        /// <summary>The runner waits here, after its first progress step. Throws OperationCanceledException when the run is stopped meanwhile.</summary>
        internal async Task HoldRunningAsync(CancellationToken ct)
        {
            _reached.TrySetResult();
            try
            {
                await _running.Task.WaitAsync(Limit, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Nobody released the run: it goes on anyway.
            }
        }

        /// <summary>The runner waits here when it was stopped, before it answers with its stopped result. It never throws.</summary>
        internal async Task HoldStoppedAsync()
        {
            try
            {
                await _stopped.Task.WaitAsync(Limit).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Nobody released the run: it ends anyway.
            }
        }
    }
}
