namespace PcMieru.Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

        // A smoke run and a --measure run never read or write the user's settings file.
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

        Strings.Current = settings.Language;

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
        using var theme = new ThemeService(settings.Theme);
        var context = new GuiContext(services, pump, theme, settings, measure: marks);
        using var form = new MainForm(context);

        if (options.StartPage is { } page && !form.ShowPage(page))
        {
            MessageBox.Show("--page: " + page, "PCMieru", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (options.SmokeOutput is { } output)
        {
            return SmokeRun.Run(form, context, output, options.ScreenshotDirectory, options.OnScreen, options.Dpi, startedAt, options.NetMeter);
        }

        Application.Run(form);

        // A window closed by Windows ending (or the task manager) did not wait for a check that was told to stop. The message loop is gone, so
        // waiting here blocks no screen: the stopped run gets a few seconds to write its result, and what is not saved by then is left to the
        // folder's own .tmp clean-up (DESIGN-A §7).
        context.Checks.WaitUntilIdle(CheckController.ExitWait);
        return 0;
    }

    /// <summary>The command line asks for a measurement run (<c>--measure</c> is on it, however well the rest parses).</summary>
    internal static bool IsMeasureRun(string[] args) => Array.IndexOf(args, "--measure") >= 0;

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
    string? MeasureOutput = null)
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
        string? measure = null;
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

        if (measure is not null && (smoke is not null || page is not null))
        {
            problem = "--measure starts on the process page and is not combined with --smoke or --page";
            return false;
        }

        options = new GuiOptions(language, theme, page, smoke, shots, fakes, onScreen, dpi, textSize, netMeter, measure);
        return true;
    }
}
