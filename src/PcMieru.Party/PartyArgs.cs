namespace PcMieru.Party;

/// <summary>The language of the window of Parts Life. Only the two the product ships.</summary>
internal enum PartyLanguage
{
    Ja,
    En,
}

/// <summary>What the command line of PCMieru.Party.exe asked for.</summary>
/// <param name="Language">From <c>--lang ja|en</c>; null = follow the UI language of the OS.</param>
/// <param name="Smoke">
/// <c>--smoke</c>: unattended. Every message goes to stderr instead of a box nobody can click, the window closes by itself after a short while,
/// "party smoke ok" is written to stdout and the exit code is 0.
/// </param>
/// <param name="ShotPath">
/// <c>--shot &lt;png&gt;</c>, valid only together with <c>--smoke</c>: after the first render the window is written as a PNG and the program exits 0.
/// </param>
/// <param name="ShotTab">
/// <c>--tab view|startup|checks|data</c>, valid only together with <c>--shot</c>: that settings tab is opened before the shot is taken.
/// </param>
/// <param name="ImportPreview">
/// <c>--import-preview ready|repair|oldapp|hold</c>, valid only together with <c>--smoke</c> (and usable with <c>--shot</c>): shows the dialog of the first start for that case with a made-up
/// plan (no engine, no file) and ends with "party smoke ok".
/// </param>
/// <param name="Logon">
/// <c>--logon</c>: this start comes from the Run key of the sign-in (DESIGN-C 3 item 5), not from a click. Not valid together with <c>--smoke</c>. The window has no minimized, hidden or tray state of its own
/// (it is a small window that stays out of the taskbar and keeps the place it was left at in its save), so a logon start is the same start as a normal one: the single-resident check, the first-start import
/// flow and the saved position are all unchanged. The flag only says where the start came from.
/// </param>
internal sealed record PartyOptions(PartyLanguage? Language, bool Smoke, string? ShotPath = null, PcMieru.Party.Ui.SettingsTab? ShotTab = null, PartyImportPreview? ImportPreview = null, bool Logon = false);

/// <summary>The cases of <c>--import-preview</c>: the three-choice question for a save that is ready or needs repairs, and the message for a running old product or a hold.</summary>
internal enum PartyImportPreview
{
    Ready,
    Repair,
    OldApp,
    Hold,
}

/// <summary>
/// Command line of PCMieru.Party.exe. Parsed by hand like the one of PCMieru.exe (src/PcMieru.Gui/Program.cs): <c>--lang</c> takes ja or en, an argument nobody knows is refused,
/// so a typo is never silently ignored. Pure string work: nothing here touches the file system, a window or a handle.
/// </summary>
internal static class PartyArgs
{
    public static bool TryParse(string[] args, out PartyOptions options, out string problem)
    {
        PartyLanguage? language = null;
        var smoke = false;
        var logon = false;
        string? shot = null;
        PcMieru.Party.Ui.SettingsTab? tab = null;
        PartyImportPreview? preview = null;
        problem = string.Empty;
        options = new PartyOptions(null, false);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lang":
                    language = (i + 1 < args.Length ? args[++i] : null)?.ToLowerInvariant() switch
                    {
                        "ja" => PartyLanguage.Ja,
                        "en" => PartyLanguage.En,
                        _ => null,
                    };
                    if (language is null)
                    {
                        problem = "--lang must be ja or en";
                        return false;
                    }

                    break;
                case "--smoke":
                    smoke = true;
                    break;
                case "--logon":
                    logon = true;
                    break;
                case "--shot":
                    shot = i + 1 < args.Length ? args[++i] : null;
                    if (string.IsNullOrWhiteSpace(shot))
                    {
                        problem = "--shot needs a png path";
                        return false;
                    }

                    // A live file (state.json, a legacy save) must never be handed in as the shot: an absolute path that ends in .png, and Program creates it
                    // with CreateNew (it never overwrites). Pure string work.
                    if (!System.IO.Path.IsPathFullyQualified(shot) || !shot.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        problem = "--shot needs an absolute path that ends in .png";
                        return false;
                    }

                    break;
                case "--tab":
                    tab = (i + 1 < args.Length ? args[++i] : null)?.ToLowerInvariant() switch
                    {
                        "view" => PcMieru.Party.Ui.SettingsTab.View,
                        "startup" => PcMieru.Party.Ui.SettingsTab.Startup,
                        "checks" => PcMieru.Party.Ui.SettingsTab.Checks,
                        "data" => PcMieru.Party.Ui.SettingsTab.Data,
                        _ => null,
                    };
                    if (tab is null)
                    {
                        problem = "--tab must be view, startup, checks or data";
                        return false;
                    }

                    break;
                case "--import-preview":
                    preview = (i + 1 < args.Length ? args[++i] : null)?.ToLowerInvariant() switch
                    {
                        "ready" => PartyImportPreview.Ready,
                        "repair" => PartyImportPreview.Repair,
                        "oldapp" => PartyImportPreview.OldApp,
                        "hold" => PartyImportPreview.Hold,
                        _ => null,
                    };
                    if (preview is null)
                    {
                        problem = "--import-preview must be ready, repair, oldapp or hold";
                        return false;
                    }

                    break;
                default:
                    problem = "unknown argument: " + args[i];
                    return false;
            }
        }

        if (shot is not null && !smoke)
        {
            problem = "--shot is valid only together with --smoke";
            return false;
        }

        if (tab is not null && shot is null)
        {
            problem = "--tab is valid only together with --shot";
            return false;
        }

        if (preview is not null && !smoke)
        {
            problem = "--import-preview is valid only together with --smoke";
            return false;
        }

        if (logon && smoke)
        {
            problem = "--logon is not valid together with --smoke";
            return false;
        }

        options = new PartyOptions(language, smoke, shot, tab, preview, logon);
        return true;
    }
}
