using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>One recording as the v0.7.4 file holds it: a time and the counters (no <c>acq</c>), and <c>withheld</c> when it was held back.</summary>
internal sealed record OldEntry(DateTimeOffset At, IReadOnlyDictionary<string, ulong> Counters, bool Withheld = false, long? Acq = null);

/// <summary>
/// The files of the old tool's folder, written the way v0.7.4 wrote them (indented, camelCase, the time with the local offset, no
/// <c>acq</c>), so the migration is tried on the real shapes and not on what the new code writes itself.
/// </summary>
internal static class OldFiles
{
    public static string At(DateTimeOffset time) =>
        time.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    /// <summary>The counters of <see cref="Drives.Nvme"/> with the same defaults.</summary>
    public static IReadOnlyDictionary<string, ulong> Nvme(
        ulong media = 0,
        ulong poh = 100,
        ulong written = 1000,
        ulong unsafeShutdowns = 5,
        ulong errorLog = 0,
        ulong used = 3,
        ulong spare = 100,
        ulong critical = 0) =>
        new SortedDictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["nvme.media_errors"] = media,
            ["nvme.error_log_entries"] = errorLog,
            ["nvme.unsafe_shutdowns"] = unsafeShutdowns,
            ["nvme.power_on_hours"] = poh,
            ["nvme.data_units_written"] = written,
            ["nvme.percentage_used"] = used,
            ["nvme.available_spare"] = spare,
            ["nvme.critical_warning"] = critical,
        };

    public static string History(string key, string model, params OldEntry[] entries)
    {
        const string nl = "\r\n";
        var text = new StringBuilder();
        text.Append('{').Append(nl);
        text.Append("  \"key\": \"").Append(key).Append("\",").Append(nl);
        text.Append("  \"model\": \"").Append(model).Append("\",").Append(nl);
        text.Append("  \"entries\": [").Append(nl);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            text.Append("    {").Append(nl);
            text.Append("      \"at\": \"").Append(At(entry.At)).Append("\",").Append(nl);
            text.Append("      \"counters\": {").Append(nl);
            var counters = entry.Counters.ToList();
            for (var c = 0; c < counters.Count; c++)
            {
                text.Append("        \"").Append(counters[c].Key).Append("\": ").Append(counters[c].Value.ToString(CultureInfo.InvariantCulture));
                text.Append(c < counters.Count - 1 ? "," : "").Append(nl);
            }

            text.Append("      }");
            if (entry.Acq is { } acq)
            {
                text.Append(',').Append(nl).Append("      \"acq\": ").Append(acq.ToString(CultureInfo.InvariantCulture));
            }

            if (entry.Withheld)
            {
                text.Append(',').Append(nl).Append("      \"withheld\": true");
            }

            text.Append(nl).Append("    }").Append(i < entries.Length - 1 ? "," : "").Append(nl);
        }

        text.Append("  ]").Append(nl).Append('}');
        return text.ToString();
    }

    /// <summary>twins.json of v0.7.4: a JSON array of key strings.</summary>
    public static string Twins(params string[] keys)
    {
        const string nl = "\r\n";
        var text = new StringBuilder("[").Append(nl);
        for (var i = 0; i < keys.Length; i++)
        {
            text.Append("  \"").Append(keys[i]).Append('"').Append(i < keys.Length - 1 ? "," : "").Append(nl);
        }

        return text.Append(']').ToString();
    }

    public static void Put(string directory, string name, string text)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), text, new UTF8Encoding(false));
    }

    /// <summary>Every file of a folder: its name, SHA-256 and last-write time. Two snapshots are equal when nothing in the folder changed.</summary>
    public static IReadOnlyList<string> Snapshot(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFileSystemEntries(directory)
                .Order(StringComparer.Ordinal)
                .Select(path => File.Exists(path)
                    ? $"{Path.GetFileName(path)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))} {File.GetLastWriteTimeUtc(path).Ticks}"
                    : $"{Path.GetFileName(path)}/")
                .ToList()
            : [];
}
