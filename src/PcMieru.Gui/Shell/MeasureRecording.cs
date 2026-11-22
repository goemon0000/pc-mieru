using System.Globalization;
using System.Text;
using System.Text.Json;
using PcMieru.Checks;

namespace PcMieru.Gui;

/// <summary>
/// What a measured run is asked to record (DESIGN-B §11 step 8, <c>tools/measure.ps1 -Mode record</c>). Test-only, left out of every help text and
/// menu, and only ever with <c>--measure</c> (which already keeps the window off the user's settings):
/// <list type="bullet">
/// <item><c>--measure-record &lt;folder&gt;</c>: the one folder the recording writes into (a full path; refused inside the user's own folders, so a
/// measurement can never put a file into Documents or PC Mieru's own settings). Starting the recording without a click is the switch itself.</item>
/// <item><c>--measure-record-lead &lt;seconds&gt;</c>: how long after the window is shown the recording starts (default 60; 0 to 3600).</item>
/// <item><c>--measure-record-for &lt;seconds&gt;</c>: how long it records before it is stopped, the way the Stop button does (default 180; 5 to 7200).</item>
/// <item><c>--measure-record-interval &lt;ms&gt;</c>: the sampling interval the user setting has in this run, in memory only (500, 1000, 2000 or 5000;
/// default: leave it at 1000). A recording samples every second, so a user interval of 2 s or more is how a run shows that the period goes back after the stop.</item>
/// </list>
/// </summary>
internal sealed record MeasureRecordPlan(string Folder, int LeadSeconds, int RecordSeconds, int UserIntervalMs)
{
    public const string FolderSwitch = "--measure-record";
    public const string LeadSwitch = "--measure-record-lead";
    public const string ForSwitch = "--measure-record-for";
    public const string IntervalSwitch = "--measure-record-interval";

    public const int DefaultLeadSeconds = 60;
    public const int MaxLeadSeconds = 3600;
    public const int DefaultRecordSeconds = 180;
    public const int MinRecordSeconds = 5;
    public const int MaxRecordSeconds = 7200;

    /// <summary>The argument is one of the four switches (all start the same way).</summary>
    public static bool IsSwitch(string argument) => argument.StartsWith(FolderSwitch, StringComparison.Ordinal);

    /// <summary>Puts the user interval of this run into the in-memory settings (a measured run never saves them). Zero: nothing changes.</summary>
    public void ApplyTo(GuiSettings settings)
    {
        if (UserIntervalMs > 0)
        {
            settings.IntervalMs = UserIntervalMs;
        }
    }

    /// <summary>The folders a measurement must never write into: the user's Documents, PC Mieru's own settings and the roaming data of the apps.</summary>
    public static string[] UserDataFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    ];

    public static bool TryRead(IReadOnlyDictionary<string, string> given, bool measureRun, out MeasureRecordPlan? plan, out string problem) =>
        TryRead(given, measureRun, UserDataFolders(), out plan, out problem);

    /// <summary>Reads the switches that were given (switch to value). Any refusal is one sentence that names the switch.</summary>
    internal static bool TryRead(IReadOnlyDictionary<string, string> given, bool measureRun, IEnumerable<string> userFolders, out MeasureRecordPlan? plan, out string problem)
    {
        plan = null;
        problem = string.Empty;
        if (given.Count == 0)
        {
            return true;
        }

        var first = given.Keys.First();
        if (!measureRun)
        {
            problem = first + " is only for a --measure run";
            return false;
        }

        if (!given.TryGetValue(FolderSwitch, out var folder))
        {
            problem = first + " needs " + FolderSwitch + " <folder>";
            return false;
        }

        if (!Path.IsPathFullyQualified(folder))
        {
            problem = FolderSwitch + " needs a full path (drive and folder)";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = FolderSwitch + ": " + ex.Message.ReplaceLineEndings(" ");
            return false;
        }

        foreach (var root in userFolders)
        {
            if (IsInside(root, full))
            {
                problem = FolderSwitch + " must be outside the user's own folders (" + root + ")";
                return false;
            }
        }

        if (!TryNumber(given, LeadSwitch, DefaultLeadSeconds, 0, MaxLeadSeconds, out var lead, out problem)
            || !TryNumber(given, ForSwitch, DefaultRecordSeconds, MinRecordSeconds, MaxRecordSeconds, out var recordFor, out problem))
        {
            return false;
        }

        var interval = 0;
        if (given.TryGetValue(IntervalSwitch, out var intervalText))
        {
            if (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || Array.IndexOf(GuiSettings.IntervalChoices, interval) < 0)
            {
                problem = IntervalSwitch + " must be one of " + string.Join(", ", GuiSettings.IntervalChoices);
                return false;
            }
        }

        plan = new MeasureRecordPlan(full, lead, recordFor, interval);
        return true;
    }

    private static bool TryNumber(IReadOnlyDictionary<string, string> given, string name, int fallback, int min, int max, out int value, out string problem)
    {
        value = fallback;
        problem = string.Empty;
        if (!given.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < min || value > max)
        {
            problem = name + " must be a whole number of seconds from " + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        return true;
    }

    private static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var child = Path.TrimEndingDirectorySeparator(path);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The measured run's own recording (DESIGN-B §11 step 8): after <see cref="MeasureRecordPlan.LeadSeconds"/> from the window being shown it starts a
/// recording through the window's recording hub, as the Start button does, lets it run for <see cref="MeasureRecordPlan.RecordSeconds"/> and stops it the way
/// the Stop button does. It leaves the facts <c>tools/measure.ps1</c> cannot read from outside in one JSON object per line next to the marks file:
/// <c>begin</c>, <c>start</c> (how long the start took), <c>period</c> (one line per snapshot that reached the window: the time Core measured since the
/// snapshot before it, which is not the nominal period but jitters around it; the user's interval; the pump's effective interval, which is the user's with slow mode applied and does NOT
/// show the recording's own 1 s; and the sampler loop's own period and lease count, read from the loop itself (<c>loopPeriodMs</c>, <c>leases</c>): that is where the recording's 1 s shows),
/// <c>stop</c>, <c>stopped</c> (how long the stop took, which is how long the
/// writer needed to drain, and the run's own result: rows, dropped ticks, status). Every line has the system time in 7 digits and a Z, comparable with
/// the marks and with the process times. Nothing here runs unless the switch was given; a failure is a line and never reaches the window.
/// </summary>
internal sealed class MeasureRecording : IDisposable
{
    public const string PhaseBefore = "before";
    public const string PhaseRecording = "recording";
    public const string PhaseStopping = "stopping";
    public const string PhaseAfter = "after";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly MeasureRecordPlan _plan;
    private readonly RecordingHub _hub;
    private readonly SnapshotPump _pump;
    private readonly Action<string> _writeLine;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cancel = new();
    private string _phase = PhaseBefore;
    private int _snapshotsInPhase;
    private bool _begun;
    private bool _disposed;

    public MeasureRecording(MeasureRecordPlan plan, RecordingHub hub, SnapshotPump pump, Action<string> writeLine, Func<DateTime>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _plan = plan;
        _hub = hub;
        _pump = pump;
        _writeLine = writeLine;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public string Phase => _phase;

    /// <summary>Where the events of the run with this marks file go: next to it, <c>&lt;marks name&gt;.record.jsonl</c>.</summary>
    public static string EventsPathFor(string markerFile)
    {
        var full = Path.GetFullPath(markerFile);
        return Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, Path.GetFileNameWithoutExtension(full) + ".record.jsonl");
    }

    /// <summary>The window's recording hub for this run: the real one writing into the plan's folder only (with <c>--fakes</c>, the in-memory one, which writes no file). Null without the switch: the context makes its own.</summary>
    public static RecordingHub? CreateHub(PcMieruServices services, MeasureRecordPlan? plan, bool? fake = null) =>
        plan is null ? null : RecordingComposition.Create(services, plan.Folder, fake);

    /// <summary>
    /// Makes the events file (emptied if it is there) before any window shows and hangs the run on the window: it begins when the window is shown, and ends with it.
    /// A file that cannot be made is one line on <paramref name="error"/> and the caller exits 1 (no message box: the run is unattended).
    /// </summary>
    public static bool TryAttach(MeasureRecordPlan plan, string markerFile, GuiContext context, Form form, TextWriter error, out MeasureRecording? recording)
    {
        recording = null;
        string path;
        try
        {
            path = EventsPathFor(markerFile);
            File.WriteAllText(path, string.Empty, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine(MeasureRecordPlan.FolderSwitch + ": " + ex.Message.ReplaceLineEndings(" "));
            return false;
        }

        var made = new MeasureRecording(plan, context.Recording, context.Pump, line => AppendLine(path, line));
        form.Shown += (_, _) => made.Begin();
        form.Disposed += (_, _) => made.Dispose();
        recording = made;
        return true;
    }

    /// <summary>Starts the run: the begin line, then the lead, the recording and the stop on the window's thread. Does nothing the second time.</summary>
    public void Begin()
    {
        if (_begun || _disposed)
        {
            return;
        }

        _begun = true;
        _pump.Sampled += OnSampled;
        Line("begin", json =>
        {
            json.WriteString("folder", _plan.Folder);
            json.WriteNumber("leadSeconds", _plan.LeadSeconds);
            json.WriteNumber("recordSeconds", _plan.RecordSeconds);
            json.WriteNumber("userIntervalMs", _pump.IntervalMs);
            json.WriteNumber("effectiveIntervalMs", _pump.EffectiveIntervalMs);
            json.WriteNumber("loopPeriodMs", _pump.LoopPeriodMs);
            json.WriteNumber("leases", _pump.RecordingLeases);
        });
        _ = RunSafelyAsync();
    }

    /// <summary>
    /// One snapshot reached the window: a period line. <paramref name="coreIntervalMs"/> is the snapshot's <c>IntervalMs</c>, which Core fills with the time it
    /// MEASURED since the snapshot before it (the nominal period only on the first snapshot of a run), so it jitters around the period the loop runs at a few
    /// milliseconds either way; the script reads it as a distribution and judges the nearest nominal period, never the exact number.
    /// </summary>
    internal void Observe(int coreIntervalMs)
    {
        if (_disposed)
        {
            return;
        }

        var snapshots = ++_snapshotsInPhase;
        Line("period", json =>
        {
            json.WriteString("phase", _phase);
            json.WriteNumber("coreIntervalMs", coreIntervalMs);
            json.WriteNumber("userIntervalMs", _pump.IntervalMs);
            json.WriteNumber("effectiveIntervalMs", _pump.EffectiveIntervalMs);
            json.WriteNumber("loopPeriodMs", _pump.LoopPeriodMs);
            json.WriteNumber("leases", _pump.RecordingLeases);
            json.WriteNumber("snapshotsInPhase", snapshots);
        });
    }

    /// <summary>The whole run, from the lead to the stop. A test awaits it with a delay it controls.</summary>
    internal async Task RunAsync()
    {
        var token = _cancel.Token;
        await _delay(TimeSpan.FromSeconds(_plan.LeadSeconds), token).ConfigureAwait(true);

        var startAt = _utcNow();
        var started = await _hub.StartAsync().ConfigureAwait(true);
        var startMs = Milliseconds(_utcNow() - startAt);
        if (!started)
        {
            Line("start", json =>
            {
                json.WriteBoolean("ok", false);
                json.WriteNumber("startMs", startMs);
                json.WriteString("problem", _hub.ProblemKey ?? "not started");
                json.WriteString("detail", _hub.ProblemDetail ?? string.Empty);
            });
            return;
        }

        SetPhase(PhaseRecording);
        var run = _hub.Run;
        Line("start", json =>
        {
            json.WriteBoolean("ok", true);
            json.WriteNumber("startMs", startMs);
            json.WriteString("runId", run?.RunId.ToString("D") ?? string.Empty);
            json.WriteString("json", run?.JsonPath ?? string.Empty);
            json.WriteString("csv", run?.CsvPath ?? string.Empty);
        });

        await _delay(TimeSpan.FromSeconds(_plan.RecordSeconds), token).ConfigureAwait(true);

        // The recorder may have ended the run itself (low space, a write error) before the stop: the stop is then no work and the line says so.
        var selfEnded = _hub.Phase != RecordingPhase.Recording;
        SetPhase(PhaseStopping);
        Line("stop", json =>
        {
            json.WriteString("reason", "user");
            json.WriteBoolean("selfEnded", selfEnded);
        });
        var stopAt = _utcNow();
        await _hub.StopAsync(RunStopReason.User).ConfigureAwait(true);
        var stopMs = Milliseconds(_utcNow() - stopAt);
        SetPhase(PhaseAfter);

        var result = _hub.LastResult;
        Line("stopped", json =>
        {
            json.WriteNumber("stopMs", stopMs);
            json.WriteBoolean("saved", result is not null);
            json.WriteString("status", result?.Status.ToString() ?? string.Empty);
            json.WriteString("stopReason", result?.StopReason.ToString() ?? string.Empty);
            json.WriteNumber("rows", result?.Rows ?? 0);
            json.WriteNumber("droppedTicks", result?.DroppedTicks ?? 0);
            json.WriteNumber("durationUs", result?.DurationUs ?? 0);
            json.WriteString("json", result?.JsonPath ?? string.Empty);
            json.WriteString("csv", result?.CsvPath ?? string.Empty);
            json.WriteString("error", result?.Error ?? _hub.ProblemText);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pump.Sampled -= OnSampled;
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private static void AppendLine(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Utf8NoBom.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static long Milliseconds(TimeSpan span) => (long)Math.Round(span.TotalMilliseconds, MidpointRounding.AwayFromZero);

    private async Task RunSafelyAsync()
    {
        try
        {
            await RunAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The window closed first: the close stops the recording (WindowClose) and the run ends there.
        }
        catch (Exception ex)
        {
            Line("error", json => json.WriteString("message", ex.GetType().Name + ": " + ex.Message.ReplaceLineEndings(" ")));
        }
    }

    private void OnSampled(Snapshot snapshot) => Observe(snapshot.IntervalMs);

    private void SetPhase(string phase)
    {
        _phase = phase;
        _snapshotsInPhase = 0;
    }

    private void Line(string name, Action<Utf8JsonWriter> fields)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("event", name);
            json.WriteString("utc", _utcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            fields(json);
            json.WriteEndObject();
        }

        try
        {
            _writeLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A line that cannot be written is missing from the file; the script says so and the window is not disturbed.
        }
    }
}
