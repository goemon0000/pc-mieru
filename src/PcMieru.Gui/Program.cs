namespace PcMieru.Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var startedAt = DateTime.Now;
        if (!GuiArgs.TryParse(args, out var options, out var problem))
        {
            MessageBox.Show(problem, "PCMieru", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        // A smoke run never reads or writes the user's settings file.
        var settings = options.SmokeOutput is null ? GuiSettings.Load(GuiSettings.DefaultPath) : new GuiSettings();
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

        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = options.ForceFakes });
        using var pump = new SnapshotPump(services, settings.IntervalMs);
        using var theme = new ThemeService(settings.Theme);
        var context = new GuiContext(services, pump, theme, settings);
        using var form = new MainForm(context);

        if (options.Page is { } page && !form.ShowPage(page))
        {
            MessageBox.Show("--page: " + page, "PCMieru", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (options.SmokeOutput is { } output)
        {
            return SmokeRun.Run(form, context, output, options.ScreenshotDirectory, options.OnScreen, options.Dpi, startedAt, options.NetMeter);
        }

        Application.Run(form);
        return 0;
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
    bool NetMeter = false);

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

        options = new GuiOptions(language, theme, page, smoke, shots, fakes, onScreen, dpi, textSize, netMeter);
        return true;
    }
}
