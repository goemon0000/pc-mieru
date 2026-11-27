using PcMieru.Core.Siblings;

namespace PcMieru.Party;

/// <summary>The language PCMieru.exe is set to (DESIGN-C 3 item 4): the <c>language</c> key of its gui.ini, read and never written.</summary>
internal static class GuiLanguage
{
    /// <summary>"ja" / "en", or null when the file is missing, unreadable or says anything else (the window then changes nothing).</summary>
    internal static string? Read() => Read(GuiIniFile.DefaultPath);

    /// <summary>The same for the file at <paramref name="path"/> (tests use a temp file; the real gui.ini is only named by <see cref="Read()"/>).</summary>
    internal static string? Read(string path) => GuiIniFile.ReadLanguage(path) switch
    {
        PcMieru.Core.Language.Ja => "ja",
        PcMieru.Core.Language.En => "en",
        _ => null,
    };
}
