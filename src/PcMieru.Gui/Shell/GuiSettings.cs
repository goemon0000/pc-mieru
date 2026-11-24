using System.Text;
using PcMieru.Update.Feed;

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
/// <para>
/// A save is a locked read-modify-replace (DESIGN-B 4): <c>gui.ini.lock</c> is held, the file on disk is read, the new text goes to <c>gui.ini.tmp</c>, is flushed to
/// disk and replaces <c>gui.ini</c>. <c>update.mode</c> is special: it is the owner's consent, so the one on disk is the newest and an object that was loaded
/// earlier never writes its own (old) value back over it; only <see cref="TrySaveUpdateMode"/> changes it.
/// </para>
/// </summary>
internal sealed class GuiSettings
{
    /// <summary>Allowed sampling intervals in milliseconds (SPEC: 0.5 / 1 / 2 / 5 seconds; pause is a separate switch).</summary>
    public static readonly int[] IntervalChoices = [500, 1000, 2000, 5000];

    private readonly string? _path;
    private readonly Dictionary<string, string> _extra = new(StringComparer.Ordinal);
    private int _intervalMs = 1000;

    /// <summary>
    /// The word of an update.mode this build does not know (a newer build's "weekly", a typo): it reads as Ask, and an ordinary save writes it back unchanged,
    /// so an older build saving a theme does not erase a newer build's choice. A choice made here replaces it. Null when the file had no such word.
    /// </summary>
    private string? _unknownModeWord;

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

    /// <summary>
    /// What the owner chose for the update check (gui.ini "update.mode": auto, notify, off). Ask until the owner chose: a missing, unknown or unreadable value is Ask.
    /// Only <see cref="TrySaveUpdateMode"/> changes it (and a load); an ordinary <see cref="TrySave"/> never writes this object's copy over the file's.
    /// </summary>
    public UpdateMode UpdateMode { get; private set; } = UpdateMode.Ask;

    /// <summary>How often a save tries for the lock (a test lowers it); the wait between tries is <see cref="LockWaitMs"/>. 25 x 20 ms: about half a second, which a click on the screen can bear.</summary>
    internal int LockAttempts { get; set; } = 25;

    internal int LockWaitMs { get; set; } = 20;

    /// <summary>
    /// An ordinary save found a different <c>update.mode</c> on disk than this object held (another window or run chose since) and took it over. The owner's
    /// choice of the moment is the one on disk, so whoever acts on the mode (the update controller: timers on or off) must follow it. Not raised by a choice made
    /// here (<see cref="TrySaveUpdateMode"/>): its caller already acts on that.
    /// </summary>
    internal event Action<UpdateMode>? UpdateModeAdopted;

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

    /// <summary>
    /// The settings of this run. A normal start reads the user's file (and saves to it). A smoke run, a <c>--measure</c> run and a run with <c>--fakes</c> (fake
    /// data for a trial) get the defaults in memory: nothing is read from <paramref name="path"/>, and since the store has no file <see cref="Save"/> writes
    /// nothing, on close or ever. A measurement or a trial therefore neither depends on what the owner chose nor changes it. (The update's verification start,
    /// <c>--verify-update</c>, is not the owner's session either: <see cref="VerifyUpdate"/> makes its own in-memory settings and never gets here.)
    /// </summary>
    internal static GuiSettings ForRun(GuiOptions options, string path) =>
        options.SmokeOutput is null && options.MeasureOutput is null && !options.ForceFakes ? Load(path) : new GuiSettings();

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
            case UpdateModes.IniKey:
                UpdateMode = UpdateModes.FromIni(value);
                _unknownModeWord = UpdateMode == UpdateMode.Ask && value.Trim() is { Length: > 0 } word && !word.Equals("ask", StringComparison.OrdinalIgnoreCase) ? word : null;
                break;
            default:
                if (!IsReserved(key))
                {
                    _extra[key] = value;
                }

                break;
        }
    }

    public string[] ToLines() => Compose(UpdateMode, _extra, _unknownModeWord);

    private string[] Compose(UpdateMode mode, IReadOnlyDictionary<string, string> extra, string? unknownModeWord)
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

        // Ask has no word: the key is simply absent until the owner has chosen.
        if (UpdateModes.ToIni(mode) is { } word)
        {
            lines.Add($"{UpdateModes.IniKey}={word}");
        }
        else if (mode == UpdateMode.Ask && unknownModeWord is not null)
        {
            // A word this build does not know reads as Ask and is kept as it was (M4): the file is not ours to tidy.
            lines.Add($"{UpdateModes.IniKey}={unknownModeWord}");
        }

        foreach (var pair in extra.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add($"{pair.Key}={pair.Value}");
        }

        return [.. lines];
    }

    /// <summary>Saves; what went wrong is not reported (an ordinary setting that cannot be written is lost quietly, as it always was). See <see cref="TrySave"/>.</summary>
    public void Save() => TrySave();

    /// <summary>
    /// The locked read-modify-replace save of everything. True when the file now says what this object says (a store with no file counts: nothing is written).
    /// The <c>update.mode</c> on disk is kept whatever this object holds: it may be newer than the copy this object loaded.
    /// </summary>
    public bool TrySave() => SaveLocked(null);

    /// <summary>
    /// Saves the owner's choice for the update check. True only when it reached the file (or the store has no file); on false <see cref="UpdateMode"/> is unchanged
    /// and the caller must behave as if nothing was chosen (no network). The newest choice wins: this one replaces whatever the file says.
    /// </summary>
    public bool TrySaveUpdateMode(UpdateMode mode) => SaveLocked(mode);

    private bool SaveLocked(UpdateMode? choice)
    {
        var before = UpdateMode;
        var saved = SaveCore(choice);
        if (saved && choice is null && UpdateMode != before)
        {
            // Raised after the lock is let go: the handler may save or start timers.
            UpdateModeAdopted?.Invoke(UpdateMode);
        }

        return saved;
    }

    private bool SaveCore(UpdateMode? choice)
    {
        if (_path is null)
        {
            if (choice is { } held)
            {
                UpdateMode = held;
                _unknownModeWord = null;
            }

            return true;
        }

        string? tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var gate = TakeLock();
            if (gate is null)
            {
                return false;
            }

            var disk = ReadDisk();
            var mode = choice ?? disk.UpdateMode;

            // Keys of the file that this object never saw (another run's, a newer build's) stay; this object's own values win over the file's for the same key.
            var extra = new Dictionary<string, string>(disk._extra, StringComparer.Ordinal);
            foreach (var pair in _extra)
            {
                extra[pair.Key] = pair.Value;
            }

            tmp = _path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true);
                foreach (var line in Compose(mode, extra, choice is null ? disk._unknownModeWord : null))
                {
                    writer.WriteLine(line);
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, null);
            }
            else
            {
                File.Move(tmp, _path);
            }

            tmp = null;
            UpdateMode = mode;
            _unknownModeWord = choice is null ? disk._unknownModeWord : null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (tmp is not null)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }

            return false;
        }
    }

    /// <summary>The file as it is now, read into a fresh object. A file that is not there is the defaults; one that cannot be read throws (the save then fails, it never guesses).</summary>
    private GuiSettings ReadDisk()
    {
        var disk = new GuiSettings();
        try
        {
            foreach (var line in File.ReadAllLines(_path!))
            {
                disk.Apply(line);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        return disk;
    }

    /// <summary>The right to rewrite gui.ini: <c>gui.ini.lock</c> opened for nobody else. Waits for another writer a bounded number of tries, then gives up (null).</summary>
    private FileStream? TakeLock()
    {
        var lockPath = _path + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (attempt >= LockAttempts)
                {
                    return null;
                }

                Thread.Sleep(LockWaitMs);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static bool IsReserved(string key) => key is "language" or "theme" or "interval" or "alwaysontop" or "textsize" or "net.unit" or "net.meter.autostart" or UpdateModes.IniKey;
}
