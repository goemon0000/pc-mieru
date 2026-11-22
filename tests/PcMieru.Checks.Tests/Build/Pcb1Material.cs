using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PcMieru.Checks.Build;

namespace PcMieru.Checks.Tests.Build;

/// <summary>
/// What the sources of the configuration code gave, as one file: the input of the PCB1 goldens (<c>Fixtures/pcb1/*.material.json</c>) and
/// of the real-machine comparison. The old code reads the same file (<c>tools/pcb1-golden/Program.cs</c> has the same schema, camelCase):
/// keep the two in step. A name in <see cref="Throws"/> ("cpus", "board", "memory", "gpus", "disks", "monitors", "opticals") is a source
/// that throws instead of answering. **A null is read as the harness reads it**: a null list is an empty list, a null text of a row is "".
/// </summary>
internal sealed class Pcb1Material
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<string> Cpus { get; set; } = [];

    public BoardRow? Board { get; set; }

    public List<RawMemory> Memory { get; set; } = [];

    public List<RawGpu> Gpus { get; set; } = [];

    public List<RawDisk> Disks { get; set; } = [];

    public List<RawMonitor> Monitors { get; set; } = [];

    public List<RawOptical> Opticals { get; set; } = [];

    public List<string> Throws { get; set; } = [];

    public sealed class BoardRow
    {
        public string? Manufacturer { get; set; }

        public string? Product { get; set; }
    }

    public static Pcb1Material Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>
    /// The material of a JSON text. The same mapping as <c>tools/pcb1-golden/Program.cs</c> (Make): a null list is empty, a null text of a
    /// row is "" (the rules take no null), and the board may stay null. A row that is itself null is refused.
    /// </summary>
    public static Pcb1Material Parse(string json)
    {
        var m = JsonSerializer.Deserialize<Pcb1Material>(json, Options) ?? throw new InvalidDataException("empty material");
        m.Cpus = [.. (m.Cpus ?? []).Select(c => c ?? "")];
        m.Memory = [.. Rows(m.Memory).Select(x => x with { PartNumber = x.PartNumber ?? "", Manufacturer = x.Manufacturer ?? "" })];
        m.Gpus = [.. Rows(m.Gpus).Select(x => x with { Name = x.Name ?? "" })];
        m.Disks = [.. Rows(m.Disks).Select(x => x with { Name = x.Name ?? "" })];
        m.Monitors = [.. Rows(m.Monitors).Select(x => x with { Name = x.Name ?? "" })];
        m.Opticals = [.. Rows(m.Opticals).Select(x => x with { Name = x.Name ?? "", PnpDeviceId = x.PnpDeviceId ?? "" })];
        m.Throws = [.. m.Throws ?? []];
        return m;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options) + "\n";

    public void Save(string path) => File.WriteAllText(path, ToJson(), new UTF8Encoding(false));

    private static IEnumerable<T> Rows<T>(List<T>? rows) where T : class =>
        (rows ?? []).Select(x => x ?? throw new InvalidDataException("a row of the material is null"));

    /// <summary>
    /// The optical drive's PNPDeviceID, reduced to what <c>BuildRules.Opticals</c> reads of it: whether it starts with "USB" and whether it
    /// contains "MSFT" (both not case sensitive). The rest of the id can end in the serial number of a USB device, and the material goes to
    /// a file: that must not be there. The reduced id gives the same code as the full one (a test proves it).
    /// </summary>
    internal static string ReduceOpticalId(string? pnpDeviceId)
    {
        var id = pnpDeviceId ?? "";
        return (id.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : "")
               + (id.Contains("MSFT", StringComparison.OrdinalIgnoreCase) ? "MSFT" : "");
    }

    /// <summary>
    /// Reads each source of the Windows readers once and keeps the answers (nothing is filtered: the rules run later). A category that could
    /// not be read stays empty, as in the collector. Names and part numbers are kept as they are (the rules clean them); the optical
    /// drive's id is reduced (<see cref="ReduceOpticalId"/>).
    /// </summary>
    public static async Task<Pcb1Material> ReadAsync(WindowsBuildReaders readers, CancellationToken ct = default)
    {
        var m = new Pcb1Material();
        m.Cpus.AddRange(await OrNothing(() => readers.CpusAsync(ct)));
        try
        {
            var (manufacturer, product) = await readers.BoardAsync(ct);
            m.Board = new BoardRow { Manufacturer = manufacturer, Product = product };
        }
        catch (BuildCategoryUnreadException)
        {
            m.Board = new BoardRow();
        }

        m.Memory.AddRange(await OrNothing(() => readers.MemoryAsync(ct)));
        m.Gpus.AddRange(await OrNothing(() => readers.GpusAsync(ct)));
        m.Disks.AddRange(await OrNothing(() => readers.DisksAsync(ct)));
        m.Monitors.AddRange(await OrNothing(() => readers.MonitorsAsync(ct)));
        m.Opticals.AddRange((await OrNothing(() => readers.OpticalsAsync(ct))).Select(o => o with { PnpDeviceId = ReduceOpticalId(o.PnpDeviceId) }));
        return m;
    }

    private static async Task<IReadOnlyList<T>> OrNothing<T>(Func<Task<IReadOnlyList<T>>> read)
    {
        try
        {
            return await read();
        }
        catch (BuildCategoryUnreadException)
        {
            return [];
        }
    }

    /// <summary>The sources that answer with this material. A source named in <see cref="Throws"/> gives a failed task.</summary>
    public BuildSources ToSources()
    {
        Func<CancellationToken, Task<T>> Source<T>(string name, Func<T> read) => _ =>
            Throws.Contains(name, StringComparer.Ordinal)
                ? Task.FromException<T>(new InvalidOperationException("synthetic failure of " + name))
                : Task.FromResult(read());

        return new BuildSources
        {
            Cpus = Source<IReadOnlyList<string>>("cpus", () => [.. Cpus]),
            Board = Source<(string? Manufacturer, string? Product)>("board", () => (Board?.Manufacturer, Board?.Product)),
            Memory = Source<IReadOnlyList<RawMemory>>("memory", () => [.. Memory]),
            Gpus = Source<IReadOnlyList<RawGpu>>("gpus", () => [.. Gpus]),
            Disks = Source<IReadOnlyList<RawDisk>>("disks", () => [.. Disks]),
            Monitors = Source<IReadOnlyList<RawMonitor>>("monitors", () => [.. Monitors]),
            Opticals = Source<IReadOnlyList<RawOptical>>("opticals", () => [.. Opticals]),
        };
    }
}
