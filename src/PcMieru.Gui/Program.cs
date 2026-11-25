using PcMieru.Update.Launch;
using PcMieru.Wire.Gate;

namespace PcMieru.Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, VerifyAdmission.Option) >= 0)
        {
            return VerifyUpdate.Run(args);
        }

        var startedAt = DateTime.Now;
        if (!GuiArgs.TryParse(args, out var options, out var problem))
        {
            // A measurement run is unattended and read from outside (tools/measure.ps1): its problems go to stderr, never to a box nobody clicks.
            if (IsMeasureRun(args))
            {
                Console.Error.WriteLine(problem.ReplaceLineEndings(" "));
            }
            else
            {
                MessageBox.Show(problem, "PCMieru", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }

        Strings.Register(GuiStrings.All);
        ApplicationConfiguration.Initialize();
        // A smoke run records an exception for its verdict instead of waiting on a message box nobody can click.
        Application.ThreadException += (_, e) =>
        {
            if (options.SmokeOutput is not null)
            {
                SmokeRun.UnhandledErrors.Enqueue(e.Exception.ToString());
                return;
            }

            MessageBox.Show(e.Exception.Message, Strings.Get("core.app.name"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        // A smoke run, a --measure run and a run with --fakes never read or write the user's settings file.
        var settings = GuiSettings.ForRun(options, GuiSettings.DefaultPath);
        if (options.Language is { } language)
        {
            settings.Language = language;
        }

        if (options.Theme is { } themeMode)
        {
            settings.Theme = themeMode;
        }

        if (options.TextSize is { } size)
        {
            settings.TextSize = size;
        }

        // --measure-record-interval (test-only): the user's interval of this run, in memory (a measured run never saves its settings).
        options.MeasureRecord?.ApplyTo(settings);

        Strings.Current = settings.Language;

        // The start gate (DESIGN-B 4): the first thing the app does once it knows the language and the theme, before the measure marker, the services, any window, the sampling and the
        // update timer. Not open = a window with the reason (or, unattended, one line on stderr and exit 1); nothing else of the app was started. Held until Main returns.
        using var theme = new ThemeService(settings.Theme);
        using var gate = StartGateScreen.Enter(theme, unattended: options.SmokeOutput is not null || options.MeasureOutput is not null, Console.Error);
        if (gate is null)
        {
            return 1;
        }

        // --measure (test-only): the marker file is made before any window exists, so a path that cannot be written is said at once.
        MeasureMarks? marks = null;
        if (options.MeasureOutput is { } markerFile)
        {
            if (!TryOpenMeasureMarks(markerFile, Console.Error, out marks))
            {
                return 1;
            }
        }

        using var services = PcMieruServices.Create(options.CoreOptions);
        using var pump = new SnapshotPump(services, settings.IntervalMs);
        var readResult = options.MeasureOutput is null && options.SmokeOutput is null && !options.ForceFakes;
        var context = new GuiContext(services, pump, theme, settings, measure: marks, lastUpdate: readResult
            ? () => UpdateResultNote.Read(InstallGate.InstallFolderOf(Environment.ProcessPath!)) : null,
            recording: MeasureRecording.CreateHub(services, options.MeasureRecord));
        if (readResult)
        {
            context.SiblingLanguageNotifier = static () => PcMieru.Core.Siblings.SiblingWindows.Notify(
                PartyLauncher.ExeName, PcMieru.Core.Siblings.MieruMessages.LanguageChanged, allowForeground: false);
        }

        using var form = new MainForm(context);

        // --measure-record (test-only, with --measure): one recording started without a click, into the folder the script gave (see MeasureRecordPlan).
        if (options.MeasureRecord is { } recordPlan && !MeasureRecording.TryAttach(recordPlan, options.MeasureOutput!, context, form, Console.Error, out _))
        {
            return 1;
        }

        if (options.StartPage is { } page && !form.ShowPage(page))
        {
            MessageBox.Show("--page: " + page, "PCMieru", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (options.SmokeOutput is { } output)
        {
            return SmokeRun.Run(form, context, output, options.ScreenshotDirectory, options.OnScreen, options.Dpi, startedAt, options.NetMeter);
        }

        // The update check starts only in a real run, once the window is up: never in a smoke run (it drives the fake service itself), never in a
        // measurement and never under --fakes (fake data must not show the first-run choice nor store an answer; settings are in memory there, see GuiSettings.ForRun).
        // With the mode still "not chosen" the first-run choice comes first; nothing is asked of the network before 「決定」 saved it.
        if (options.MeasureOutput is null && !options.ForceFakes)
        {
            form.Shown += (_, _) => form.BeginInvoke(form.StartUpdates);
        }

        Application.Run(form);

        // A window closed by Windows ending (or the task manager) did not wait for a check that was told to stop. The message loop is gone, so
        // waiting here blocks no screen: the stopped run gets a few seconds to write its result, and what is not saved by then is left to the
        // folder's own .tmp clean-up (DESIGN-A §7).
        WaitForWork(context);
        return 0;
    }

    /// <summary>
    /// After the message loop: a check that was told to stop, and a recording whose stop is still writing its result, each get their own bounded wait (3 s and
    /// 2 s). A recording the close already waited for and gave up on is not waited for again.
    /// </summary>
    internal static void WaitForWork(GuiContext context)
    {
        context.Checks.WaitUntilIdle(CheckController.ExitWait);
        context.Recording.WaitUntilSaved(RecordingHub.SavedWait);
    }

    /// <summary>The command line asks for a measurement run (<c>--measure</c> or one of the <c>--measure-record</c> switches is on it, however well the rest parses).</summary>
    internal static bool IsMeasureRun(string[] args) => Array.Exists(args, a => a == "--measure" || MeasureRecordPlan.IsSwitch(a));

    /// <summary>
    /// <c>--measure</c>: makes the marker file before any window exists. A path that cannot be made is one line on <paramref name="error"/> (what
    /// <c>tools/measure.ps1</c> reads) and the caller exits 1; no message box, because nobody is there to click it in an unattended measurement.
    /// </summary>
    internal static bool TryOpenMeasureMarks(string path, TextWriter error, out MeasureMarks? marks)
    {
        try
        {
            marks = MeasureMarks.Create(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            marks = null;
            error.WriteLine("--measure: " + ex.Message.ReplaceLineEndings(" "));
            return false;
        }
    }
}

internal sealed record GuiOptions(
    Language? Language,
    ThemeMode? Theme,
    string? Page,
    string? SmokeOutput,
    string? ScreenshotDirectory,
    bool ForceFakes,
    bool OnScreen = false,
    int Dpi = 0,
    TextSize? TextSize = null,
    bool NetMeter = false,
    string? MeasureOutput = null,
    MeasureRecordPlan? MeasureRecord = null)
{
    /// <summary>The page to open at start: the process page for <c>--measure</c> (what the marks are about), else <c>--page</c> (null: the first page).</summary>
    public string? StartPage => MeasureOutput is not null ? "processes" : Page;

    /// <summary>What the services are built with. <c>--measure</c> keeps the real services off PC Mieru's own state files (eco-state.json).</summary>
    public PcMieruOptions CoreOptions => new() { ForceFakes = ForceFakes, NoStateFiles = MeasureOutput is not null };
}

/// <summary>
/// Command line of PCMieru.exe. These switches exist for verification (smoke run, screenshots, picking a page);
/// none of them changes what the app is allowed to do.
/// </summary>
internal static class GuiArgs
{
    public static bool TryParse(string[] args, out GuiOptions options, out string problem)
    {
        Language? language = null;
        ThemeMode? theme = null;
        string? page = null;
        string? smoke = null;
        string? shots = null;
        var fakes = false;
        var onScreen = false;
        var dpi = 0;
        TextSize? textSize = null;
        var netMeter = false;
        var openChecks = false;
        string? measure = null;
        var measureRecord = new Dictionary<string, string>(StringComparer.Ordinal);
        problem = string.Empty;
        options = new GuiOptions(null, null, null, null, null, false);

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--lang":
                    language = Next()?.ToLowerInvariant() switch
                    {
                        "ja" => Language.Ja,
                        "en" => Language.En,
                        _ => null,
                    };
                    if (language is null)
                    {
                        problem = "--lang must be ja or en";
                        return false;
                    }

                    break;
                case "--theme":
                    theme = Next()?.ToLowerInvariant() switch
                    {
                        "system" => ThemeMode.System,
                        "light" => ThemeMode.Light,
                        "dark" => ThemeMode.Dark,
                        _ => null,
                    };
                    if (theme is null)
                    {
                        problem = "--theme must be system, light or dark";
                        return false;
                    }

                    break;
                case "--page":
                    page = Next();
                    if (string.IsNullOrEmpty(page))
                    {
                        problem = "--page needs a page id";
                        return false;
                    }

                    break;
                case "--open-checks":
                    // What the Parts Life window starts this exe with when no main window is open (DESIGN-C 3 item 3): the same as --page checks.
                    openChecks = true;
                    break;
                case "--smoke":
                    smoke = Next();
                    if (string.IsNullOrEmpty(smoke))
                    {
                        problem = "--smoke needs an output file";
                        return false;
                    }

                    break;
                case "--shots":
                    shots = Next();
                    if (string.IsNullOrEmpty(shots))
                    {
                        problem = "--shots needs a directory";
                        return false;
                    }

                    break;
                case "--measure":
                    // Test-only (tools/measure.ps1), left out of every help text and menu: writes the two render-time marks to this file and
                    // runs on the default settings, starting on the process page, without ever writing gui.ini or eco-state.json.
                    measure = Next();
                    if (string.IsNullOrEmpty(measure))
                    {
                        problem = "--measure needs an output file";
                        return false;
                    }

                    break;
                case MeasureRecordPlan.FolderSwitch:
                case MeasureRecordPlan.LeadSwitch:
                case MeasureRecordPlan.ForSwitch:
                case MeasureRecordPlan.IntervalSwitch:
                case MeasureRecordPlan.TimesSwitch:
                case MeasureRecordPlan.GapSwitch:
                case MeasureRecordPlan.GcSwitch:
                    // Test-only (tools/measure.ps1 -Mode record), with --measure: a recording of the measured run itself. Read after the loop.
                    var recordSwitch = args[i];
                    var recordValue = Next();
                    if (string.IsNullOrEmpty(recordValue) || !measureRecord.TryAdd(recordSwitch, recordValue))
                    {
                        problem = recordSwitch + (string.IsNullOrEmpty(recordValue) ? " needs a value" : " is given twice");
                        return false;
                    }

                    break;
                case "--fakes":
                    fakes = true;
                    break;
                case "--onscreen":
                    onScreen = true;
                    break;
                case "--netmeter":
                    // Smoke only: also start the real network meter and check that traffic reaches the process list.
                    // It needs an elevated shell; the smoke run never brings up a UAC prompt.
                    netMeter = true;
                    break;
                case "--textsize":
                    textSize = Next()?.ToLowerInvariant() switch
                    {
                        "small" => TextSize.Small,
                        "normal" => TextSize.Normal,
                        "large" => TextSize.Large,
                        _ => null,
                    };
                    if (textSize is null)
                    {
                        problem = "--textsize must be small, normal or large";
                        return false;
                    }

                    break;
                case "--dpi":
                    if (!int.TryParse(Next(), out dpi) || dpi is < 100 or > 300)
                    {
                        problem = "--dpi must be a scale percentage from 100 to 300";
                        return false;
                    }

                    break;
                default:
                    problem = "unknown argument: " + args[i];
                    return false;
            }
        }

        if (openChecks)
        {
            if (page is not null && !string.Equals(page, "checks", StringComparison.Ordinal))
            {
                problem = "--open-checks is --page checks and is not combined with another --page";
                return false;
            }

            page = "checks";
        }

        if (measure is not null && (smoke is not null || page is not null))
        {
            problem = "--measure starts on the process page and is not combined with --smoke or --page";
            return false;
        }

        if (!MeasureRecordPlan.TryRead(measureRecord, measure is not null, out var recordPlan, out problem))
        {
            return false;
        }

        options = new GuiOptions(language, theme, page, smoke, shots, fakes, onScreen, dpi, textSize, netMeter, measure, recordPlan);
        return true;
    }
}
