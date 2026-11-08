using System.Globalization;
using System.Text.Json;
using PcMieru.Wire;

namespace PcMieru.Core.Management;

/// <summary>What a process was like before efficiency mode was switched on: its priority class and power-throttling masks.</summary>
public sealed record EcoRecord(uint PriorityClass, uint Control, uint State, bool Known);

/// <summary>
/// Remembers the values efficiency mode replaced, so "off" puts back what the process had instead of a guess.
/// Kept in memory and in <c>%LOCALAPPDATA%\PCMieru\eco-state.json</c> (best effort: a file that cannot be written only
/// means "off" falls back to the system default, which is said in the docs). The FIRST record per process is kept:
/// switching "on" twice must not record the already-throttled values as the original.
/// A process that is no longer running is forgotten the next time anything is written.
/// </summary>
public sealed class EcoStateStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, EcoRecord>? _records;

    public EcoStateStore(string? path = null) =>
        _path = path ?? Path.Combine(ManagementFolder.Path, "eco-state.json");

    /// <summary>The record of that exact process, or null.</summary>
    public EcoRecord? Get(ProcessKey key)
    {
        lock (_gate)
        {
            return Load().TryGetValue(key.ToString(), out var record) ? record : null;
        }
    }

    /// <summary>Stores the record unless one exists already (the first one is the original).</summary>
    public void SetIfAbsent(ProcessKey key, EcoRecord record, Func<ProcessKey, bool>? isAlive = null)
    {
        lock (_gate)
        {
            var records = Load();
            if (records.ContainsKey(key.ToString()))
            {
                return;
            }

            records[key.ToString()] = record;
            Prune(records, isAlive);
            Save(records);
        }
    }

    public void Remove(ProcessKey key, Func<ProcessKey, bool>? isAlive = null)
    {
        lock (_gate)
        {
            var records = Load();
            if (records.Remove(key.ToString()))
            {
                Prune(records, isAlive);
                Save(records);
            }
        }
    }

    /// <summary>Reads the "prev-*" values of a successful "on" from the Helper or Wire result.</summary>
    public static EcoRecord? FromData(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null
            || !data.TryGetValue(HelperDataKeys.PreviousKnown, out var known)
            || !data.TryGetValue(HelperDataKeys.PreviousPriority, out var priority)
            || !uint.TryParse(priority, NumberStyles.None, CultureInfo.InvariantCulture, out var priorityClass))
        {
            return null;
        }

        var isKnown = known == "true";
        uint control = 0;
        uint state = 0;
        if (isKnown
            && (!data.TryGetValue(HelperDataKeys.PreviousControl, out var c)
                || !data.TryGetValue(HelperDataKeys.PreviousState, out var s)
                || !uint.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out control)
                || !uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out state)))
        {
            return null;
        }

        return new EcoRecord(priorityClass, control, state, isKnown);
    }

    /// <summary>What to restore for a record: nothing recorded (or not readable) means the system default.</summary>
    public static EfficiencyRestore? ToRestore(EcoRecord? record) =>
        record is { Known: true } r && HelperRequest.IsRestorablePriorityClass(r.PriorityClass) && r.Control <= 1 && r.State <= 1
            ? new EfficiencyRestore(r.PriorityClass, r.Control, r.State)
            : null;

    private Dictionary<string, EcoRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        _records = [];
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(_path), ManagementJsonContext.Default.DictionaryStringEcoRecord);
                if (loaded is not null)
                {
                    _records = new Dictionary<string, EcoRecord>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable file is the same as no file: "off" falls back to the system default.
        }

        return _records;
    }

    private static void Prune(Dictionary<string, EcoRecord> records, Func<ProcessKey, bool>? isAlive)
    {
        if (isAlive is null)
        {
            return;
        }

        foreach (var text in records.Keys.ToArray())
        {
            if (ProcessKey.TryParse(text, out var key) && !isAlive(key))
            {
                records.Remove(text);
            }
        }
    }

    private void Save(Dictionary<string, EcoRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records, ManagementJsonContext.Default.DictionaryStringEcoRecord));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the in-memory copy still serves this run.
        }
    }
}

/// <summary>Where PC Mieru keeps its own small state files (<c>%LOCALAPPDATA%\PCMieru</c>).</summary>
internal static class ManagementFolder
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru");
}
