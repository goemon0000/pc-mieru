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
internal sealed record PartyOptions(PartyLanguage? Language, bool Smoke);

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
                default:
                    problem = "unknown argument: " + args[i];
                    return false;
            }
        }

        options = new PartyOptions(language, smoke);
        return true;
    }
}
