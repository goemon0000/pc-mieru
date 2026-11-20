using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PcMieru.Checks.Build;

namespace PcMieru.Checks.Tests.Build;

/// <summary>
/// What the sources of the configuration code gave, as one file: the input of the PCB1 goldens (<c>Fixtures/pcb1/*.material.json</c>) and
/// of the real-machine comparison. The old code reads the same file (<c>tools/pcb1-golden/Program.cs</c> has the same schema, camelCase):
/// keep the two in step. A name in <see cref="Throws"/> ("cpus", "board", "memory", "gpus", "disks", "monitors", "opticals") is a source
/// that throws instead of answering.
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

    public static Pcb1Material Load(string path) =>
        JsonSerializer.Deserialize<Pcb1Material>(File.ReadAllText(path, Encoding.UTF8), Options) ?? throw new InvalidDataException("empty material");

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n", new UTF8Encoding(false));

    /// <summary>Reads each source of the Windows readers once and keeps the answers (nothing is filtered: the rules run later).</summary>
    public static async Task<Pcb1Material> ReadAsync(WindowsBuildReaders readers, CancellationToken ct = default)
    {
        var m = new Pcb1Material();
        m.Cpus.AddRange(await readers.CpusAsync(ct));
        var (manufacturer, product) = await readers.BoardAsync(ct);
        m.Board = new BoardRow { Manufacturer = manufacturer, Product = product };
        m.Memory.AddRange(await readers.MemoryAsync(ct));
        m.Gpus.AddRange(await readers.GpusAsync(ct));
        m.Disks.AddRange(await readers.DisksAsync(ct));
        m.Monitors.AddRange(await readers.MonitorsAsync(ct));
        m.Opticals.AddRange(await readers.OpticalsAsync(ct));
        return m;
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
