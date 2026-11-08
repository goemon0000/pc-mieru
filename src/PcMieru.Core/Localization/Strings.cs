using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace PcMieru.Core;

public enum Language
{
    Ja,
    En,
}

/// <summary>
/// One UI/CLI string. Key is "&lt;area&gt;.&lt;name&gt;" and the prefix says who owns it:
/// core. (integration), collection. (A), gui. (B), hw. (C), mgmt. (D), cli. report. obs. mcp. (E).
/// Placeholders are {name}; ja and en must use the same set (a test checks).
/// </summary>
public readonly record struct StringEntry(string Key, string Ja, string En);

/// <summary>
/// Process-wide string table. Each area keeps its entries in its own file (XxxStrings.cs) and registers them
/// from its Module.Register, so two areas never edit the same table. Registering the same key with the same
/// text twice is fine; a different text under an existing key throws.
/// </summary>
public static class Strings
{
    private static readonly ConcurrentDictionary<string, StringEntry> Table = new(StringComparer.Ordinal);

    static Strings()
    {
        Register(CoreStrings.All);
    }

    public static Language Current { get; set; } = DefaultFromCulture();

    public static IReadOnlyCollection<StringEntry> All => Table.Values.ToArray();

    public static Language DefaultFromCulture() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase) ? Language.Ja : Language.En;

    public static void Register(IEnumerable<StringEntry> entries)
    {
        foreach (var entry in entries)
        {
            var existing = Table.GetOrAdd(entry.Key, entry);
            if (existing != entry)
            {
                throw new InvalidOperationException($"String key '{entry.Key}' is already registered with different text.");
            }
        }
    }

    public static string Get(string key) => Get(key, Current);

    public static string Get(string key, Language language)
    {
        if (!Table.TryGetValue(key, out var entry))
        {
            return "[" + key + "]";
        }

        return language == Language.Ja ? entry.Ja : entry.En;
    }

    public static string Format(string key, IReadOnlyDictionary<string, string>? args, Language? language = null)
    {
        var text = Get(key, language ?? Current);
        if (args is null || args.Count == 0 || text.IndexOf('{') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, open - i);
            var name = text.Substring(open + 1, close - open - 1);
            sb.Append(args.TryGetValue(name, out var value) ? value : text.Substring(open, close - open + 1));
            i = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>Placeholder names used by a string, for the ja/en consistency test.</summary>
    public static IReadOnlyCollection<string> Placeholders(string text)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var i = 0;
        while ((i = text.IndexOf('{', i)) >= 0)
        {
            var close = text.IndexOf('}', i + 1);
            if (close < 0)
            {
                break;
            }

            names.Add(text.Substring(i + 1, close - i - 1));
            i = close + 1;
        }

        return names;
    }

    public static string Status(ValueStatus status) => Get("core.status." + status switch
    {
        ValueStatus.NotCollected => "notCollected",
        ValueStatus.Ok => "ok",
        ValueStatus.Unsupported => "unsupported",
        ValueStatus.Denied => "denied",
        ValueStatus.Timeout => "timeout",
        ValueStatus.Error => "error",
        ValueStatus.Gone => "gone",
        _ => "error",
    });

    /// <summary>Renders an observation in the requested language from its key and arguments.</summary>
    public static string Render(Observation observation, Language? language = null) =>
        Format(observation.MessageKey, observation.Args, language);
}

internal static class CoreStrings
{
    public static readonly StringEntry[] All =
    [
        new("core.app.name", "PCミエル", "PC Mieru"),
        new("core.status.notCollected", "未取得", "Not collected"),
        new("core.status.ok", "取得済み", "OK"),
        new("core.status.unsupported", "非対応", "Unsupported"),
        new("core.status.denied", "権限なし", "Access denied"),
        new("core.status.timeout", "応答なし", "Timed out"),
        new("core.status.error", "取得に失敗", "Failed"),
        new("core.status.gone", "終了済み", "Gone"),
        new("core.fake.notice", "サンプルデータで動作中（実測値ではありません）", "Running on sample data (not real measurements)"),
        new("core.unit.percent", "%", "%"),
        new("core.unit.bytesPerSec", "B/秒", "B/s"),
    ];
}
