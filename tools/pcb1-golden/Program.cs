// The OLD configuration code, run on material given by hand (or read from this machine), so the new code can be compared with it.
//
// This file is built against the old revision (parts-life 0.7.4 = pl-cloud commit 25e4571, project src/PartsLife.Checks) by
// build-harness.ps1; it is never part of this repository's solution. It calls the old BuildCollector.Collect and BuildCode.Make and
// writes what they gave, and nothing else.
//
//   Pcb1Golden golden  <materialDir> <outDir>     every <case>.material.json -> <case>.code.txt, <case>.json, <case>.notes.txt
//   Pcb1Golden replay  <materialFile> <codeFile>  one material file -> the code (UTF-8, no newline at the end)
//   Pcb1Golden windows <codeFile>                 the old Windows readers on this machine -> the code
//
// It prints no name, code or JSON: only a line of counts or an error, so that a log of it holds nothing of the machine.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PartsLife.Checks.Build;

internal static class Program
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8 = new(false);

    private static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["golden", var materialDir, var outDir]:
                    return Golden(materialDir, outDir);
                case ["replay", var materialFile, var codeFile]:
                    File.WriteAllText(codeFile, Make(Load(materialFile)).Code, Utf8);
                    return 0;
                case ["windows", var codeFile]:
                    File.WriteAllText(codeFile, BuildCode.Make(BuildCollector.Collect()).Code, Utf8);
                    return 0;
                default:
                    Console.Error.WriteLine("usage: Pcb1Golden golden <materialDir> <outDir> | replay <materialFile> <codeFile> | windows <codeFile>");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("failed: " + ex.GetType().Name);
            return 1;
        }
    }

    private static int Golden(string materialDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var file in Directory.GetFiles(materialDir, "*.material.json").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file)[..^".material.json".Length];
            var r = Make(Load(file));
            File.WriteAllText(Path.Combine(outDir, name + ".code.txt"), r.Code, Utf8);
            File.WriteAllText(Path.Combine(outDir, name + ".json"), r.Json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n", Utf8);
            File.WriteAllText(Path.Combine(outDir, name + ".notes.txt"), r.Notes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Utf8);
            n++;
        }

        Console.WriteLine($"cases: {n}");
        return 0;
    }

    private static Material Load(string path) =>
        JsonSerializer.Deserialize<Material>(File.ReadAllText(path, Utf8), Options) ?? throw new InvalidDataException("empty material");

    private static BuildCodeResult Make(Material m)
    {
        T[] Or<T>(T[]? list) => list ?? [];

        // A source named in "throws" throws instead of answering, to show that its category stays empty and nothing else is lost.
        bool Throws(string source) => m.Throws?.Contains(source, StringComparer.Ordinal) == true;
        T Guard<T>(string source, Func<T> read) => Throws(source) ? throw new InvalidOperationException("synthetic failure of " + source) : read();

        var sources = new BuildSources
        {
            Cpus = () => Guard("cpus", () => (IEnumerable<string>)Or(m.Cpus)),
            Board = () => Guard("board", () => (m.Board?.Manufacturer, m.Board?.Product)),
            Memory = () => Guard("memory", () => Or(m.Memory).Select(x => new RawMemory(x.PartNumber ?? "", x.Manufacturer ?? "", x.Capacity, x.Speed, x.SmbiosType, x.MemoryType, x.ConfiguredSpeed))),
            Gpus = () => Guard("gpus", () => Or(m.Gpus).Select(x => new RawGpu(x.Name ?? "", x.DedicatedBytes, x.OnPciBus))),
            Disks = () => Guard("disks", () => Or(m.Disks).Select(x => new RawDisk(x.Name ?? "", x.BusType, x.MediaType, x.Size))),
            Monitors = () => Guard("monitors", () => Or(m.Monitors).Select(x => new RawMonitor(x.Name ?? "", x.Active))),
            Opticals = () => Guard("opticals", () => Or(m.Opticals).Select(x => new RawOptical(x.Name ?? "", x.PnpDeviceId ?? ""))),
        };
        return BuildCode.Make(BuildCollector.Collect(sources));
    }

    // The material. The same file is read by the new tests (tests/PcMieru.Checks.Tests/Build/Pcb1Material.cs): keep the two in step.
    private sealed class Material
    {
        public string[]? Cpus { get; set; }
        public BoardRow? Board { get; set; }
        public MemoryRow[]? Memory { get; set; }
        public GpuRow[]? Gpus { get; set; }
        public DiskRow[]? Disks { get; set; }
        public MonitorRow[]? Monitors { get; set; }
        public OpticalRow[]? Opticals { get; set; }
        public string[]? Throws { get; set; }
    }

    private sealed class BoardRow { public string? Manufacturer { get; set; } public string? Product { get; set; } }

    private sealed class MemoryRow
    {
        public string? PartNumber { get; set; }
        public string? Manufacturer { get; set; }
        public ulong Capacity { get; set; }
        public uint Speed { get; set; }
        public int SmbiosType { get; set; }
        public int MemoryType { get; set; }
        public uint ConfiguredSpeed { get; set; }
    }

    private sealed class GpuRow { public string? Name { get; set; } public ulong DedicatedBytes { get; set; } public bool OnPciBus { get; set; } }

    private sealed class DiskRow { public string? Name { get; set; } public int BusType { get; set; } public int MediaType { get; set; } public ulong Size { get; set; } }

    private sealed class MonitorRow { public string? Name { get; set; } public bool Active { get; set; } }

    private sealed class OpticalRow { public string? Name { get; set; } public string? PnpDeviceId { get; set; } }
}
