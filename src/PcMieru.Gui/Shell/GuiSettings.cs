namespace PcMieru.Gui;

/// <summary>
/// Per-user choices, kept as two "key=value" lines under %LOCALAPPDATA%\PCMieru. Anything unreadable falls back to
/// the defaults: a broken settings file must never stop the window from opening. A store with no path keeps
/// everything in memory (smoke runs and tests never touch the real file).
/// </summary>
internal sealed class GuiSettings
{
    private readonly string? _path;

    public GuiSettings(string? path = null) => _path = path;

    public Language Language { get; set; } = Strings.DefaultFromCulture();

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru", "gui.ini");

    public static GuiSettings Load(string path)
    {
        var settings = new GuiSettings(path);
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (key == "language" && Enum.TryParse<Language>(value, ignoreCase: true, out var language) && Enum.IsDefined(language))
                {
                    settings.Language = language;
                }
                else if (key == "theme" && Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var theme) && Enum.IsDefined(theme))
                {
                    settings.Theme = theme;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return settings;
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
            File.WriteAllLines(_path, [$"language={Language.ToString().ToLowerInvariant()}", $"theme={Theme.ToString().ToLowerInvariant()}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
