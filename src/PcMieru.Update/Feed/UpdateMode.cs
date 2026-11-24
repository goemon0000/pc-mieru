namespace PcMieru.Update.Feed;

/// <summary>
/// What the owner of this PC allowed the update feed to do (DESIGN-B 2 and 8). <see cref="Ask"/> is not a choice: it is "no choice yet" (never asked,
/// an unknown value, an unreadable settings file, a closed dialog, a failed save). Nothing leaves the PC in <see cref="Ask"/> or <see cref="Off"/>.
/// </summary>
public enum UpdateMode
{
    /// <summary>No choice yet. The first-run dialog asks (again on the next start). No network.</summary>
    Ask = 0,

    /// <summary>Look at the latest release once a day and say that there is one. Nothing is downloaded but two small text files.</summary>
    Notify,

    /// <summary>Install by itself. In step 2 of the update work this still only notifies (there is no download yet), but it is stored and shown.</summary>
    Automatic,

    /// <summary>Never look.</summary>
    Off,
}

/// <summary>The words of the <c>update.mode</c> key of gui.ini.</summary>
public static class UpdateModes
{
    /// <summary>The key in gui.ini.</summary>
    public const string IniKey = "update.mode";

    /// <summary>"auto", "notify" or "off". <see cref="UpdateMode.Ask"/> has no word: it is the absence of the key.</summary>
    public static string? ToIni(UpdateMode mode) => mode switch
    {
        UpdateMode.Automatic => "auto",
        UpdateMode.Notify => "notify",
        UpdateMode.Off => "off",
        _ => null,
    };

    /// <summary>Anything but auto / notify / off (missing, empty, unknown, "ask", another case of those words is read: "NOTIFY" = notify) is <see cref="UpdateMode.Ask"/>.</summary>
    public static UpdateMode FromIni(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "auto" => UpdateMode.Automatic,
        "notify" => UpdateMode.Notify,
        "off" => UpdateMode.Off,
        _ => UpdateMode.Ask,
    };

    /// <summary>
    /// The mode that <paramref name="path"/> (gui.ini) says, read the way the GUI's own settings reader reads it (<c>GuiSettings.Load</c>: lines, a key before the first '=', trimmed, the LAST
    /// <c>update.mode</c> wins, an unknown word is Ask), so that the runner (which must not load the GUI) and the GUI agree; a test holds the two to the same answers. A missing, unreadable or absurdly large
    /// file is <see cref="UpdateMode.Ask"/>: the runner swaps only when the file says "auto".
    /// </summary>
    public static UpdateMode ReadFromFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024)
            {
                return UpdateMode.Ask;
            }

            var mode = UpdateMode.Ask;
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim() == IniKey)
                {
                    mode = FromIni(line[(eq + 1)..].Trim());
                }
            }

            return mode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return UpdateMode.Ask;
        }
    }

    /// <summary>The two modes in which the feed may be asked.</summary>
    public static bool MayUseNetwork(UpdateMode mode) => mode is UpdateMode.Notify or UpdateMode.Automatic;
}
