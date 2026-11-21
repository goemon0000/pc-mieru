// New in PC Mieru (no old counterpart): the old app called BuildCollector and BuildCode directly from the window. The way in from outside is
// now this interface and the two factories (DESIGN-A 2); the collector, the rules and Make are internal.
namespace PcMieru.Checks.Build;

/// <summary>
/// Makes the configuration code of a machine. The screen and the MCP server take this, so a test or a preview shot can replace it (a fake
/// PCB1 is another implementation of this, not a change to the collector).
/// </summary>
public interface IBuildCodeProvider
{
    /// <summary>
    /// Reads the parts and makes the code. A category that could not be read does not stop it: it is listed in
    /// <see cref="BuildCodeResult.Unread"/>. Only the caller's cancellation is thrown. It is slow (WMI): not on the UI thread.
    /// </summary>
    Task<BuildCodeResult> CreateAsync(CancellationToken ct);
}

/// <summary>The implementations of <see cref="IBuildCodeProvider"/>.</summary>
public static class BuildCodeProviders
{
    /// <summary>This machine: WMI with a deadline of 5 s per category, and DXGI. Only reads; no administrator needed.</summary>
    public static IBuildCodeProvider Windows() => new SourcesBuildCodeProvider(static () => BuildSources.Windows());

    /// <summary>
    /// A fixed, synthetic machine (no real model of anyone, no serial number) passed through the same rules and the same code as a real one,
    /// for <c>--fakes</c> and the preview shots. Every category is read, so <see cref="BuildCodeResult.Unread"/> is empty.
    /// </summary>
    public static IBuildCodeProvider Sample() => new SourcesBuildCodeProvider(SampleSources);

    private static BuildSources SampleSources() => new()
    {
        Cpus = static _ => Rows<string>(["AMD Ryzen 7 5800X 8-Core Processor"]),
        Board = static _ => Task.FromResult<(string?, string?)>(("ASUSTeK COMPUTER INC.", "TUF GAMING X670E-PLUS WIFI")),
        Memory = static _ => Rows<RawMemory>(
        [
            new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368, 5600, 34, 0, 5600),
            new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368, 5600, 34, 0, 5600),
        ]),
        Gpus = static _ => Rows<RawGpu>([new RawGpu("NVIDIA GeForce RTX 5070", 12884901888, true)]),
        Disks = static _ => Rows<RawDisk>(
        [
            new RawDisk("Samsung SSD 980 PRO 2TB", 17, 4, 2000398934016),
            new RawDisk("TOSHIBA MG04ACA200E", 11, 3, 2000398934016),
        ]),
        Monitors = static _ => Rows<RawMonitor>([new RawMonitor("UG27PJ", true)]),
        Opticals = static _ => Rows<RawOptical>([new RawOptical("HL-DT-ST BD-RE WH16NS60", @"SCSI\CDROM&VEN_HL-DT-ST&PROD_BD-RE_WH16NS60\4&1")]),
    };

    private static Task<IReadOnlyList<T>> Rows<T>(IReadOnlyList<T> rows) => Task.FromResult(rows);
}

/// <summary>Collects from the sources of a machine and makes the code. The sources are made at each call, so a provider can be kept.</summary>
internal sealed class SourcesBuildCodeProvider(Func<BuildSources> sources) : IBuildCodeProvider
{
    public async Task<BuildCodeResult> CreateAsync(CancellationToken ct)
    {
        var collection = await BuildCollector.CollectWithUnreadAsync(sources(), ct).ConfigureAwait(false);
        return BuildCode.Make(collection.Config, collection.Unread);
    }
}
