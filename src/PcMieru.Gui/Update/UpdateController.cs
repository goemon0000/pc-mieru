using PcMieru.Update.Feed;

namespace PcMieru.Gui;

/// <summary>
/// The update check of this window (DESIGN-B 3.1, 4): owns the two timers, the one check that may run at a time, and what the settings page and the status
/// bar say about it. Owned by <see cref="GuiContext"/>, like the check run: the page comes and goes while a check runs.
///
/// **What runs depends on the mode, and in Ask and Off nothing does.** No timer is made, the service is never called, no HTTP client exists. Only Notify and
/// Automatic start the timers (Automatic behaves like Notify in this step: nothing is downloaded, nothing is installed). A choice that could not be written
/// to gui.ini is Ask (<see cref="GuiSettings.TrySaveUpdateMode"/> leaves the mode unchanged), and a choice of "off" that could not be written still stops
/// the network for the rest of this run.
///
/// What a running Notify costs while nothing is due: one 60 s timer (once) and one hourly timer; every hour a small local read of state.json by the
/// service decides that it is not time yet. A check that is due (once per 24 h) makes one HTTP client, three small requests at most, and disposes it.
/// </summary>
internal sealed class UpdateController : IDisposable
{
    private readonly IUpdateService _service;
    private readonly GuiSettings _settings;
    private readonly IUpdateTimers _timers;
    private readonly Func<Placement> _placement;
    private readonly AppVersion _running;
    private readonly TimeProvider _clock;
    private readonly HashSet<AppVersion> _announced = [];
    private IDisposable? _first;
    private IDisposable? _poll;
    private CancellationTokenSource? _checking;
    private UpdateCheck? _last;
    private bool _started;
    private bool _blocked;
    private bool _disposed;

    /// <param name="service">What asks the feed. The real one, a fake of the smoke run, or one that only refuses (this PC cannot say who it is).</param>
    /// <param name="settings">Holds the mode. Its <see cref="GuiSettings.UpdateMode"/> is the only place the choice lives.</param>
    /// <param name="timers">The 60 s and hourly timers. A test gives its own and fires them by hand.</param>
    /// <param name="placement">Whether an update could be put in place here (DESIGN-B 5); asked afresh every time, never remembered.</param>
    /// <param name="running">The version of this program: a candidate must be newer than it.</param>
    public UpdateController(IUpdateService service, GuiSettings settings, IUpdateTimers timers, Func<Placement> placement, AppVersion running, TimeProvider? clock = null)
    {
        _service = service;
        _settings = settings;
        _timers = timers;
        _placement = placement;
        _running = running;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The state of the check changed (started, finished, a mode was chosen). The settings page shows itself again.</summary>
    public event Action? Changed;

    /// <summary>A candidate that has not been announced in this run (once per version): the version and its Release page. The status bar says it quietly.</summary>
    public event Action<AppVersion, string?>? CandidateFound;

    /// <summary>A check ended with an exception the service did not turn into an answer. The status bar says it.</summary>
    public event Action<Exception>? Failed;

    public UpdateMode Mode => _settings.UpdateMode;

    /// <summary>This program's version.</summary>
    public AppVersion Running => _running;

    /// <summary>True when a check may go out in this run: the mode is Notify or Automatic and nothing said "off" for this run.</summary>
    public bool Allowed => !_blocked && !_disposed && UpdateModes.MayUseNetwork(Mode);

    public bool Checking => _checking is not null;

    /// <summary>The timers exist (Notify or Automatic, started). A test checks that Ask and Off never have them.</summary>
    public bool TimersRunning => _first is not null || _poll is not null;

    /// <summary>What the last check of this run found; null before one ended.</summary>
    public UpdateCheck? Last => _last;

    /// <summary>The newest check or timer task, for a test to await. Never awaited by the window.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>What state.json says (last check, the wait after a rate limit, the last candidate). A local read; asks nobody.</summary>
    public UpdateStatus Status()
    {
        try
        {
            return _service.ReadStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStatus.Empty;
        }
    }

    /// <summary>Whether an update could be put in place here, looked at now (a probe of the folder, never remembered).</summary>
    public Placement CurrentPlacement() => _placement();

    /// <summary>The newest version this PC knows of that is newer than the running one: this run's answer, else the one in state.json. Null: none.</summary>
    public (AppVersion Version, string? PageUrl)? Candidate()
    {
        if (_last is { Kind: UpdateCheckKind.Candidate, Version: { } found } check && found > _running)
        {
            return (found, check.PageUrl);
        }

        var status = Status();
        return status.CandidateVersion is { } stored && stored > _running ? (stored, status.CandidatePageUrl) : null;
    }

    /// <summary>
    /// The window is up and the choice (if any) is settled: the timers are made when the mode allows the network. In Ask and Off this makes nothing. A
    /// candidate that an earlier run left in state.json is announced once (a local read, no request).
    /// </summary>
    public void Start()
    {
        _started = true;
        if (Apply())
        {
            AnnouncePersisted();
        }
    }

    /// <summary>
    /// The owner chose a mode. The choice is written to gui.ini first (<see cref="GuiSettings.TrySaveUpdateMode"/>); only then are the timers made or
    /// dropped. Returns false when it could not be written: the mode is then what it was (Ask on a first run), no timer is made, and a choice of "off"
    /// still stops the network for the rest of this run. Choosing Off cancels a check that is running.
    /// </summary>
    public bool ChooseMode(UpdateMode mode)
    {
        var saved = _settings.TrySaveUpdateMode(mode);
        if (saved)
        {
            _blocked = false;
        }
        else if (!UpdateModes.MayUseNetwork(mode))
        {
            _blocked = true;
        }

        Apply();
        Changed?.Invoke();
        return saved;
    }

    /// <summary>The hourly look: asks the service, which decides from state.json whether 24 hours have passed. Null when nothing was asked.</summary>
    public Task<UpdateCheck?> TickAsync() => RunAsync(manual: false);

    /// <summary>"Check now": skips the 24 hour rule, never a wait that GitHub asked for. Null when nothing was asked (Ask, Off, a check already running).</summary>
    public Task<UpdateCheck?> CheckNowAsync()
    {
        var task = RunAsync(manual: true);
        Pending = task;
        return task;
    }

    public void Dispose()
    {
        _disposed = true;
        StopTimers();
        CancelChecking();
    }

    /// <summary>Makes the timers or drops them, as the mode says. True when the network is allowed.</summary>
    private bool Apply()
    {
        if (!Allowed)
        {
            StopTimers();
            CancelChecking();
            return false;
        }

        if (_started && !TimersRunning)
        {
            _first = _timers.Once(UpdateSchedule.FirstDelay, () => Pending = RunSafeAsync(manual: false));
            _poll = _timers.Every(UpdateSchedule.Poll, () => Pending = RunSafeAsync(manual: false));
        }

        return true;
    }

    private void StopTimers()
    {
        var first = _first;
        var poll = _poll;
        _first = null;
        _poll = null;
        first?.Dispose();
        poll?.Dispose();
    }

    private void CancelChecking()
    {
        var current = _checking;
        _checking = null;
        current?.Cancel();
    }

    private async Task<UpdateCheck?> RunAsync(bool manual)
    {
        if (!Allowed || _checking is not null)
        {
            return null;
        }

        var mode = Mode;
        var source = new CancellationTokenSource();
        _checking = source;
        Changed?.Invoke();
        UpdateCheck? result = null;
        var cancelled = false;
        try
        {
            result = await _service.CheckAsync(mode, manual, source.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            cancelled |= source.IsCancellationRequested;
            if (ReferenceEquals(_checking, source))
            {
                _checking = null;
            }

            source.Dispose();
            Changed?.Invoke();
        }

        // Off (or another choice) came while the check ran: what it found is not shown.
        if (cancelled || result is null || !Allowed || Mode != mode)
        {
            return null;
        }

        // "Not due yet" asked nobody and found nothing: it must not wipe what the last real check found.
        if (result is not { Kind: UpdateCheckKind.NoCandidate, Fetched: false })
        {
            _last = result;
        }

        if (result is { Kind: UpdateCheckKind.Candidate, Version: { } version })
        {
            Announce(version, result.PageUrl);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>The timer's tick: an exception must not leave the timer callback (it would end the window), it is said on the status bar.</summary>
    private async Task RunSafeAsync(bool manual)
    {
        try
        {
            await RunAsync(manual).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    private void AnnouncePersisted()
    {
        var status = Status();
        if (status.CandidateVersion is { } stored && stored > _running)
        {
            Announce(stored, status.CandidatePageUrl);
        }
    }

    private void Announce(AppVersion version, string? pageUrl)
    {
        if (_announced.Add(version))
        {
            CandidateFound?.Invoke(version, pageUrl);
        }
    }
}
