namespace PcMieru.Gui;

/// <summary>How large the text is. Small is the size Windows gives message boxes; Normal is a step above it and the default.</summary>
internal enum TextSize
{
    Small,
    Normal,
    Large,
}

internal static class TextSizes
{
    /// <summary>The body font size in points for a choice, counted from the size Windows uses for its own dialogs.</summary>
    public static float Points(TextSize size, float systemPoints) => size switch
    {
        TextSize.Small => systemPoints,
        TextSize.Large => systemPoints + 3f,
        _ => systemPoints + 1f,
    };
}

/// <summary>
/// Per-user choices, kept as "key=value" lines under %LOCALAPPDATA%\PCMieru\gui.ini. Anything unreadable falls back to
/// the defaults: a broken settings file must never stop the window from opening. A store with no path keeps
/// everything in memory (smoke runs and tests never touch the real file). Keys this version does not know are kept
/// and written back, so a newer build's settings survive an older build saving.
/// </summary>
internal sealed class GuiSettings
{
    /// <summary>Allowed sampling intervals in milliseconds (SPEC: 0.5 / 1 / 2 / 5 seconds; pause is a separate switch).</summary>
    public static readonly int[] IntervalChoices = [500, 1000, 2000, 5000];

    private readonly string? _path;
    private readonly Dictionary<string, string> _extra = new(StringComparer.Ordinal);
    private int _intervalMs = 1000;

    public GuiSettings(string? path = null) => _path = path;

    public Language Language { get; set; } = Strings.DefaultFromCulture();

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>One of <see cref="IntervalChoices"/>; any other value is snapped to the nearest choice.</summary>
    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Snap(value);
    }

    public bool AlwaysOnTop { get; set; }

    public TextSize TextSize { get; set; } = TextSize.Normal;

    /// <summary>How network rates are written everywhere they show (bits or bytes, automatic or fixed prefix). Decimal, unlike the disk and memory figures.</summary>
    public NetworkUnit NetworkUnit { get; set; } = NetworkUnit.AutoBits;

    /// <summary>Whether the network meter is started when the window opens (one UAC prompt each time). Off unless the user turns it on.</summary>
    public bool NetMeterAutostart { get; set; }

    /// <summary>Whether the sampler is paused. Not remembered across runs: opening the app should always show live numbers.</summary>
    public bool Paused { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru", "gui.ini");

    public static int Snap(int intervalMs)
    {
        var best = IntervalChoices[0];
        foreach (var choice in IntervalChoices)
        {
            if (Math.Abs(choice - intervalMs) < Math.Abs(best - intervalMs))
            {
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Reads a free-form per-page value (column widths, split position, view mode). Null when never saved.</summary>
    public string? GetValue(string key) => _extra.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int fallback) =>
        int.TryParse(GetValue(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public bool GetBool(string key, bool fallback) =>
        GetValue(key) is { } text ? text == "1" : fallback;

    public void SetValue(string key, string value)
    {
        if (IsReserved(key) || key.Contains('=') || key.Contains('\n') || value.Contains('\n'))
        {
            return;
        }

        _extra[key] = value;
    }

    public void SetInt(string key, int value) =>
        SetValue(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void SetBool(string key, bool value) => SetValue(key, value ? "1" : "0");

    public static GuiSettings Load(string path)
    {
        var settings = new GuiSettings(path);
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                settings.Apply(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return settings;
    }

    /// <summary>Parses one "key=value" line. Public for tests; unknown or malformed lines are ignored or kept, never thrown.</summary>
    public void Apply(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return;
        }

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "language" when Enum.TryParse<Language>(value, ignoreCase: true, out var language) && Enum.IsDefined(language):
                Language = language;
                break;
            case "theme" when Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var theme) && Enum.IsDefined(theme):
                Theme = theme;
                break;
            case "interval" when int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms):
                IntervalMs = ms;
                break;
            case "alwaysontop":
                AlwaysOnTop = value == "1";
                break;
            case "textsize" when Enum.TryParse<TextSize>(value, ignoreCase: true, out var textSize) && Enum.IsDefined(textSize):
                TextSize = textSize;
                break;
            case "net.unit" when Enum.TryParse<NetworkUnit>(value, ignoreCase: true, out var networkUnit) && Enum.IsDefined(networkUnit):
                NetworkUnit = networkUnit;
                break;
            case "net.meter.autostart":
                NetMeterAutostart = value == "1";
                break;
            default:
                if (!IsReserved(key))
                {
                    _extra[key] = value;
                }

                break;
        }
    }

    public string[] ToLines()
    {
        var lines = new List<string>
        {
            $"language={Language.ToString().ToLowerInvariant()}",
            $"theme={Theme.ToString().ToLowerInvariant()}",
            $"interval={IntervalMs}",
            $"alwaysontop={(AlwaysOnTop ? 1 : 0)}",
            $"textsize={TextSize.ToString().ToLowerInvariant()}",
            $"net.unit={NetworkUnit.ToString().ToLowerInvariant()}",
            $"net.meter.autostart={(NetMeterAutostart ? 1 : 0)}",
        };
        foreach (var pair in _extra.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add($"{pair.Key}={pair.Value}");
        }

        return [.. lines];
    }

    public void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, ToLines());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsReserved(string key) => key is "language" or "theme" or "interval" or "alwaysontop" or "textsize" or "net.unit" or "net.meter.autostart";
}
