using System.Text;

namespace PcMieru.Core.Siblings;

/// <summary>
/// gui.ini, the settings file of PCMieru.exe (<c>%LOCALAPPDATA%\PCMieru\gui.ini</c>), as the Parts Life window sees it (DESIGN-C 3 item 4): it reads ONE key, <c>language</c>, and never
/// writes the file. GuiSettings in the GUI is the only writer and takes its path from here, so the two cannot name different files.
/// </summary>
public static class GuiIniFile
{
    /// <summary>Lines read at most (the real file has a few dozen); anything beyond is not looked at.</summary>
    private const int MaxLines = 2000;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru", "gui.ini");

    /// <summary>
    /// The <c>language</c> key of the file at <paramref name="path"/>: <c>ja</c> or <c>en</c> (any case). Null when the file is missing, cannot be read or says anything else; the caller then
    /// changes nothing. The file is opened for reading with sharing, so a save of the GUI at the same moment neither blocks nor is blocked. Like GuiSettings, the last valid line wins.
    /// </summary>
    public static Language? ReadLanguage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Language? found = null;
            for (var i = 0; i < MaxLines && reader.ReadLine() is { } line; i++)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0 || !line.AsSpan(0, eq).Trim().Equals("language", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = line.AsSpan(eq + 1).Trim();
                if (value.Equals("ja", StringComparison.OrdinalIgnoreCase))
                {
                    found = Language.Ja;
                }
                else if (value.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    found = Language.En;
                }
            }

            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
