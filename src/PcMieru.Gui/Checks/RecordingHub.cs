using System.Security.Cryptography;
using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>Where the window's recording stands. Everything but <see cref="Idle"/> counts as "a recording is in the way" for closing the window.</summary>
internal enum RecordingPhase
{
    Idle,

    /// <summary>The recorder is taking its lease and making the files.</summary>
    Starting,

    Recording,

    /// <summary>The stop is running: the writer is drained and the final file saved.</summary>
    Stopping,
}

/// <summary>The run being recorded, as the window knows it. The recorder's own handle stays inside <see cref="LiveRecordingBackend"/> (its constructor is the library's).</summary>
internal sealed record RecordingRun(Guid RunId, DateTime StartedAtUtc, string JsonPath, string CsvPath, Task<RunResult> Completion);

/// <summary>
/// What the hub needs of a recorder, with no handle in it: the window has one recording at a time, so "the current run" is enough. The live one wraps
/// <see cref="RunRecorder"/>; <see cref="FakeRecordingBackend"/> is the smoke run's and the tests'.
/// </summary>
internal interface IRecordingBackend
{
    Task<RecordingRun> StartAsync(CancellationToken ct);

    /// <summary>Throws <see cref="InvalidOperationException"/> when the run is not recording (it ended meanwhile).</summary>
    Task<RunMarker> MarkAsync(MarkerKind kind, CancellationToken ct);

    /// <summary>Stops whatever run there is; null when none is going.</summary>
    Task<RunResult?> StopCurrentAsync(RunStopReason reason);
}

/// <summary>The real recorder behind the seam.</summary>
internal sealed class LiveRecordingBackend(IRunRecorder recorder) : IRecordingBackend
{
    private RunHandle? _run;

    public async Task<RecordingRun> StartAsync(CancellationToken ct)
    {
        var handle = await recorder.StartAsync(ct).ConfigureAwait(false);
        _run = handle;
        return new RecordingRun(handle.RunId, handle.StartedAtUtc, handle.JsonPath, handle.CsvPath, handle.Completion);
    }

    public Task<RunMarker> MarkAsync(MarkerKind kind, CancellationToken ct) =>
        _run is { } run ? recorder.MarkAsync(run, kind, ct) : throw new InvalidOperationException("No run is being recorded.");

    public Task<RunResult?> StopCurrentAsync(RunStopReason reason) => recorder.StopCurrentAsync(reason);
}

/// <summary>
/// The window's one recording: the phase, the marks, the elapsed time, and the list's store, made only when something asks for them. Nothing here
/// touches the disk until the tab is shown (the store, to list) or a recording is started (the recorder): building the hub, the page or the window
/// reads nothing, creates nothing and takes no lease. The recording is the hub's, not the tab's: it goes on when the tab, the page or the window
/// changes, and only the user's stop, the window closing or the recorder's own end (low space, a write error) ends it.
/// </summary>
internal sealed class RecordingHub : IDisposable
{
    private readonly Func<IRecordingBackend> _makeBackend;
    private readonly Func<IRunStore> _makeStore;
    private readonly Func<string> _folder;
    private string? _folderCache;
    private readonly Func<DateTime> _utcNow;
    private readonly List<RunMarker> _marks = [];
    private IRecordingBackend? _backend;
    private IRunStore? _store;
    private RecordingRun? _run;
    private RecordingPhase _phase;
    private System.Windows.Forms.Timer? _timer;
    private Task? _stopTask;
    private bool _closing;
    private bool _stopGaveUp;
    private bool _disposed;

    /// <param name="backend">Makes the recorder, the first time a recording starts.</param>
    /// <param name="store">Makes the store of the list, the first time the list is read.</param>
    /// <param name="folder">Where the recordings are kept (a path only: asking for it creates nothing).</param>
    /// <param name="utcNow">The clock of the elapsed time. Null: the system's.</param>
    public RecordingHub(Func<IRecordingBackend> backend, Func<IRunStore> store, Func<string> folder, Func<DateTime>? utcNow = null)
    {
        _makeBackend = backend;
        _makeStore = store;
        _folder = folder;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    /// <summary>The run list or an open run changed, the phase changed, a mark was set, or a second went by while recording. Raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>A run ended, whoever ended it. The result is null when the recorder gave none (the stop failed).</summary>
    public event Action<RunResult?>? RunEnded;

    public RecordingPhase Phase => _phase;

    public bool IsActive => _phase != RecordingPhase.Idle;

    public RecordingRun? Run => _run;

    public Guid? ActiveRunId => _run?.RunId;

    /// <summary>Time since the run began (the recorder's start time on the system clock), never negative; zero when nothing is recording.</summary>
    public TimeSpan Elapsed => _run is { } run ? Max(_utcNow() - run.StartedAtUtc) : TimeSpan.Zero;

    /// <summary>
    /// The clock as it was when <see cref="Changed"/> was last raised: one value for everything that shows it (the state line, the list's row, the head of
    /// the summary, the status bar). Each of them reading <see cref="Elapsed"/> for itself, at its own moment, showed a second apart when the read crossed
    /// a second; this is read once per tick and every part of the window shows that.
    /// </summary>
    public TimeSpan ShownElapsed { get; private set; }

    public IReadOnlyList<RunMarker> Marks => _marks;

    /// <summary>How the last run ended (null before the first one ends in this window).</summary>
    public RunResult? LastResult { get; private set; }

    /// <summary>The key of the text that says why the last start, mark or stop did not work (null: nothing is wrong), and what to put in it.</summary>
    public string? ProblemKey { get; private set; }

    public string? ProblemDetail { get; private set; }

    public string ProblemText => ProblemKey is null ? string.Empty : Strings.Format(ProblemKey, new Dictionary<string, string> { ["error"] = ProblemDetail ?? string.Empty });

    /// <summary>Where the recordings are kept. Asked of the provider the first time it is needed (the tab being shown, "open folder") and kept: the tab reads it on the UI thread and must not ask Windows again.</summary>
    public string Folder => _folderCache ??= _folder();

    /// <summary>Tells whether a folder is there. Replaced by the fakes (their folder is made up); a click on "open folder" asks it.</summary>
    internal Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    /// <summary>Tells whether a file is there (the CSV of a run, before it is handed to the shell). Replaced by the fakes.</summary>
    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>The temp folder an old-app CSV is copied into before it is opened. Replaced by a test.</summary>
    internal Func<string> TempFolder { get; set; } = Path.GetTempPath;

    /// <summary>
    /// Copies a file to a name that is not there (the old-app CSV into the temp folder): a name that is there is an error and is never written over (a link left
    /// at it would be followed, and a file another program holds cannot be written). The earlier app's files are read only for this one and a copy keeps that,
    /// so the copy is made writable: the program that opens it may save. Replaced by a test that must not touch the disk.
    /// </summary>
    internal Action<string, string> CopyFile { get; set; } = static (from, to) =>
    {
        File.Copy(from, to, overwrite: false);
        var attributes = File.GetAttributes(to);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(to, attributes & ~FileAttributes.ReadOnly);
        }
    };

    /// <summary>
    /// The file to hand to the shell for a run's "open CSV". The app's own recordings are opened themselves; an old-app record never is (a program that
    /// opens a file can write to it, and the earlier app's files are read only for this one): a copy in the temp folder, <c>PCMieru-legacy-&lt;id&gt;-&lt;8 hex&gt;.csv</c>,
    /// under a new name for every click (the last copy may be held open by the program that showed it, and a name that is there is never written over), is.
    /// The older copies of the same run are cleared away afterwards, as far as they can be (one that is held stays; a link is removed as a name, never followed).
    /// Does disk work: call it off the UI thread.
    /// </summary>
    internal string CsvToOpen(RunInfo info)
    {
        if (!info.IsLegacy)
        {
            return info.CsvPath;
        }

        var id = new string((info.LegacyId ?? info.RunId.ToString("N")).Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        var folder = TempFolder();
        var copy = Path.Combine(folder, "PCMieru-legacy-" + id + "-" + RandomNumberGenerator.GetHexString(8, lowercase: true) + ".csv");
        CopyFile(info.CsvPath, copy);
        ClearOlderCopies(folder, id, copy);
        return copy;
    }

    /// <summary>Removes the earlier copies of a run's CSV from the temp folder, best effort: what is held or cannot be removed stays, and nothing is an error.</summary>
    private static void ClearOlderCopies(string folder, string id, string keep)
    {
        var head = "PCMieru-legacy-" + id;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, head + "*.csv"))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase) || !IsCopyName(name, head))
                {
                    continue;
                }

                try
                {
                    // Deletes the name (a link too: the data it led to is never touched).
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held by the program that opened it: left for the next click.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // No folder to look in, or one that cannot be listed: nothing to clear.
        }
    }

    /// <summary>Whether a file name is a copy of this run (the form of now, <c>head-8hex.csv</c>, or the one before it, <c>head.csv</c>) and no other run's whose id starts the same.</summary>
    private static bool IsCopyName(string name, string head)
    {
        if (name.Length < head.Length + 4 || !name.StartsWith(head, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var middle = name.AsSpan(head.Length, name.Length - head.Length - 4);
        return middle.Length == 0 || (middle.Length == 9 && middle[0] == '-' && middle[1..].IndexOfAnyExcept("0123456789abcdefABCDEF") < 0);
    }

    /// <summary>True once the recorder was made (a recording was started in this window). A test reads it to show nothing is made at the start.</summary>
    internal bool BackendCreated => _backend is not null;

    /// <summary>True once the list's store was made (the tab was shown). A test reads it to show nothing is made at the start.</summary>
    internal bool StoreCreated => _store is not null;

    /// <summary>The store of the list. Made on first use, on the UI thread, with no read.</summary>
    public IRunStore Store => _store ??= _makeStore();

    /// <summary>Starts a recording. False when one is already going. A refusal (no space, no sampler, a file error) is in <see cref="ProblemKey"/>, with the phase back at idle.</summary>
    public async Task<bool> StartAsync()
    {
        if (_phase != RecordingPhase.Idle || _disposed || _closing)
        {
            return false;
        }

        _phase = RecordingPhase.Starting;
        _stopGaveUp = false;
        ClearProblem();
        LastResult = null;
        _marks.Clear();
        Raise();
        try
        {
            _backend ??= _makeBackend();
            var run = await _backend.StartAsync(CancellationToken.None).ConfigureAwait(true);
            if (_disposed || _closing)
            {
                return false;
            }

            _run = run;
            _phase = RecordingPhase.Recording;
            StartTimer();
            Raise();
            _ = Watch(run);
            return true;
        }
        catch (RunStartException ex)
        {
            return FailStart(RecordStrings.KeyOf(ex.Failure), null);
        }
        catch (Exception ex)
        {
            return FailStart("gui.rtab.problem.start", CheckFormat.ExceptionDetail(ex));
        }
    }

    /// <summary>Puts a mark in the run. False when nothing is recording (the menu is only enabled while recording, but the run can end under the click).</summary>
    public async Task<bool> MarkAsync(MarkerKind kind)
    {
        if (_phase != RecordingPhase.Recording || _backend is null)
        {
            return false;
        }

        try
        {
            var mark = await _backend.MarkAsync(kind, CancellationToken.None).ConfigureAwait(true);
            if (_phase == RecordingPhase.Recording)
            {
                _marks.Add(mark);
                Raise();
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetProblem("gui.rtab.problem.mark", CheckFormat.ExceptionDetail(ex));
            Raise();
            return false;
        }
    }

    /// <summary>Stops the recording and waits for it to be saved (on the UI thread's context, without blocking it). A no-op when none is recording.</summary>
    public async Task StopAsync(RunStopReason reason = RunStopReason.User)
    {
        if (_phase != RecordingPhase.Recording || _backend is null)
        {
            return;
        }

        _phase = RecordingPhase.Stopping;
        StopTimer();
        Raise();
        RunResult? result = null;
        Exception? failure = null;
        try
        {
            var stop = _backend.StopCurrentAsync(reason);
            _stopTask = stop;
            result = await stop.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        FinishCurrent(result, failure);
    }

    /// <summary>
    /// For the window closing and the session ending, where nothing can be awaited: stops the run through the library's bounded stop
    /// (<see cref="RunRecorderExtensions.StopCurrentBlocking"/>, 2 s by default) with the reason <see cref="RunStopReason.WindowClose"/>. Returns the
    /// result, or null when nothing was recording or the stop did not finish in time (the run is then left to the next start to recover). Makes
    /// nothing when no recording was ever started in this window. It tells nobody while it runs: not <see cref="RunEnded"/> (the window is going away:
    /// no list is read) and not <see cref="Changed"/> either, because the handlers of the tab and the status bar must not run in the middle of a close.
    /// A close that is refused after all (another program can veto the end of a Windows session) leaves the window open and the hub idle: that is
    /// told to <see cref="Changed"/> afterwards, once the message loop has the thread again (and never when the hub was disposed with the window).
    /// </summary>
    public RunResult? StopForClose(TimeSpan? bound = null)
    {
        // A stop that already ran out of time is not waited for again: Windows asks twice when it ends the session (the query, then the end), and
        // the window must not hold the shutdown for a second bound.
        if (_backend is null || _phase == RecordingPhase.Idle || _stopGaveUp)
        {
            return null;
        }

        _closing = true;
        StopTimer();
        RunResult? result;
        try
        {
            result = new CurrentRunStop(_backend).StopCurrentBlocking(RunStopReason.WindowClose, bound);
        }
        finally
        {
            // The close may still be refused by someone else (another program can veto the end of a Windows session): the window then goes on, so it
            // must not stay frozen in "closing".
            _closing = false;
        }

        if (result is null)
        {
            _stopGaveUp = true;
            return null;
        }

        LastResult = result;
        _run = null;
        _phase = RecordingPhase.Idle;
        // The window's own thread, the one this is called on (it is the UI thread's context only once a window exists, so it is not asked for before).
        SynchronizationContext.Current?.Post(static state => ((RecordingHub)state!).Raise(), this);
        return result;
    }

    /// <summary>How long the process waits for a recording that is still being saved once the window is gone (the same bound as the stop of the close).</summary>
    public static readonly TimeSpan SavedWait = RunRecorderExtensions.ExitBound;

    /// <summary>
    /// For after the message loop ended (<c>Program</c>): waits, for at most <paramref name="bound"/>, for a stop that is still writing its result (the person
    /// pressed Stop and the window went away before it finished). True when nothing is being saved or it was saved in time. False when it is not saved yet,
    /// and when the close already waited out its own bound for it (a second wait would hold the exit for twice the limit). Never throws, blocks no screen: the
    /// message loop is gone.
    /// </summary>
    public bool WaitUntilSaved(TimeSpan bound)
    {
        if (_stopGaveUp)
        {
            return false;
        }

        var stop = _stopTask;
        if (stop is null || stop.IsCompleted)
        {
            return true;
        }

        try
        {
            return stop.Wait(bound);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>One second went by: the elapsed time is drawn again. The timer calls this; a test calls it with its own clock.</summary>
    internal void Tick()
    {
        if (_phase == RecordingPhase.Recording)
        {
            Raise();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopTimer();
    }

    private async Task Watch(RecordingRun run)
    {
        RunResult? result = null;
        Exception? failure = null;
        try
        {
            result = await run.Completion.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (_run?.RunId == run.RunId)
        {
            FinishCurrent(result, failure);
        }
    }

    /// <summary>The run is over (the user's stop, or the recorder's own: low space, a write error). Idempotent: whichever of the stop and the watcher comes second does nothing.</summary>
    private void FinishCurrent(RunResult? result, Exception? failure)
    {
        if (_phase == RecordingPhase.Idle || _disposed || _closing)
        {
            return;
        }

        StopTimer();
        _phase = RecordingPhase.Idle;
        _run = null;
        LastResult = result;
        if (failure is not null)
        {
            SetProblem("gui.rtab.problem.stop", CheckFormat.ExceptionDetail(failure));
        }

        Raise();
        RunEnded?.Invoke(result);
    }

    private bool FailStart(string key, string? detail)
    {
        if (_disposed || _closing)
        {
            return false;
        }

        _phase = RecordingPhase.Idle;
        SetProblem(key, detail);
        Raise();
        return false;
    }

    private void SetProblem(string key, string? detail)
    {
        ProblemKey = key;
        ProblemDetail = detail;
    }

    private void ClearProblem()
    {
        ProblemKey = null;
        ProblemDetail = null;
    }

    private void Raise()
    {
        if (!_disposed && !_closing)
        {
            ShownElapsed = Elapsed;
            Changed?.Invoke();
        }
    }

    private void StartTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void StopTimer()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    private static TimeSpan Max(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    /// <summary>The one member of the recorder the window's exit needs, so the library's bounded stop is the function that runs.</summary>
    private sealed class CurrentRunStop(IRecordingBackend backend) : IRunRecorder
    {
        public Task<RunResult?> StopCurrentAsync(RunStopReason reason) => backend.StopCurrentAsync(reason);

        public Task<RunHandle> StartAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<RunMarker> MarkAsync(RunHandle run, MarkerKind kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<RunResult> StopAsync(RunHandle run, RunStopReason reason) => throw new NotSupportedException();
    }
}
