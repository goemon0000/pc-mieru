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

/// <summary>Which synthetic machine <see cref="BuildCodeProviders.Sample(SampleVariant)"/> makes.</summary>
public enum SampleVariant
{
    /// <summary>A small machine that was read in full: nothing was left out and nothing is unread (what <see cref="BuildCodeProviders.Sample()"/> gives).</summary>
    Plain,

    /// <summary>
    /// A large machine that goes through every way of leaving something out: more than <see cref="BuildCode.MaxPerCategory"/> kinds of SSD,
    /// the optical drive, the HDD and all but the first kind of memory dropped for length, and three categories that could not be read
    /// (each for another reason). Made by the real rules from the sources, so what it says was left out is what they did.
    /// </summary>
    Trimmed,
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
    public static IBuildCodeProvider Sample() => Sample(SampleVariant.Plain);

    /// <summary>
    /// A fixed, synthetic machine of the given kind. <see cref="SampleVariant.Plain"/> is <see cref="Sample()"/>.
    /// <see cref="SampleVariant.Trimmed"/> is the one that shows every note and every reason: its code is cut to the limit by the real rules,
    /// and its <see cref="BuildCodeResult.Notes"/> and <see cref="BuildCodeResult.Unread"/> say what they really did (nothing is added to them).
    /// </summary>
    public static IBuildCodeProvider Sample(SampleVariant variant) => variant switch
    {
        SampleVariant.Plain => new SourcesBuildCodeProvider(SampleSources),
        SampleVariant.Trimmed => new SourcesBuildCodeProvider(TrimmedSampleSources),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown sample"),
    };

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

    /// <summary>The text that makes a made-up name long ("a made-up part name, made long"). Hiragana and kanji only (no white space, nothing the JSON escapes).</summary>
    private const string LongTail = "架空の見本部品の名前を長くした文字列";

    /// <summary>
    /// A made-up name of the longest length the code keeps: a short stem, then the long tail. **It has to be this long**: ASCII names of 80
    /// characters never reach the length limit past the HDD (eight kinds of every category are about 6,500 bytes, and the three categories
    /// that go first are about 2,400 of them), and the JSON counts bytes, three for each of these characters.
    /// </summary>
    private static string LongName(string stem, int max = 80) => BuildRules.CleanName(stem + " " + string.Concat(Enumerable.Repeat(LongTail, 6)), max);

    /// <summary>
    /// The large synthetic machine of <see cref="SampleVariant.Trimmed"/>, in the order the rules will leave things out:
    /// <list type="bullet">
    /// <item>SSD: ten kinds, so two are left out (more than <see cref="BuildCode.MaxPerCategory"/>).</item>
    /// <item>Optical drive, then HDD: too long for the limit, dropped in that order (the monitor, between them, was not read, so it is not there to drop).</item>
    /// <item>Memory: eight kinds, long ones after the first: still too long, so only the first kind stays.</item>
    /// <item>GPU timed out, the motherboard was denied, the monitor is not supported: those three have no parts.</item>
    /// </list>
    /// </summary>
    private static BuildSources TrimmedSampleSources() => new()
    {
        Cpus = static _ => Rows<string>([LongName("CPU 01")]),
        Board = static _ => Task.FromException<(string?, string?)>(new BuildCategoryUnreadException(CheckReason.Denied)),
        Memory = static _ => Rows<RawMemory>(Enumerable.Range(1, 8)
            .Select(static i => new RawMemory(LongName($"RAM {i:00}"), i == 1 ? "Example" : LongName("Example", 40), 34359738368, 5600, 34, 0, 5600))
            .ToList()),
        Gpus = static _ => Task.FromException<IReadOnlyList<RawGpu>>(new BuildCategoryUnreadException(CheckReason.Timeout)),
        Disks = static _ => Rows<RawDisk>(
        [
            .. Enumerable.Range(1, 10).Select(static i => new RawDisk(LongName($"SSD {i:00}"), i % 2 == 0 ? 11 : 17, 4, 2000398934016)),
            .. Enumerable.Range(1, 8).Select(static i => new RawDisk(LongName($"HDD {i:00}"), 11, 3, 4000787030016)),
        ]),
        Monitors = static _ => Task.FromException<IReadOnlyList<RawMonitor>>(new BuildCategoryUnreadException(CheckReason.Unsupported)),
        Opticals = static _ => Rows<RawOptical>(Enumerable.Range(1, 4)
            .Select(static i => new RawOptical(LongName($"BD {i:00}"), @"SCSI\CDROM&VEN_EXAMPLE&PROD_BD_" + i.ToString("00") + @"\4&1"))
            .ToList()),
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
