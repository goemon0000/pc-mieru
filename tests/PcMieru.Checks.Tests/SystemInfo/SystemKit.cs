using PcMieru.Checks.SystemInfo;
using PcMieru.Core;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>What a scripted WMI question answers: the rows (they go through the mapper of the question, as the real reader does), or the reason there are none.</summary>
internal sealed record WmiScript(IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows, CheckReason? Reason)
{
    public static WmiScript Of(IEnumerable<IReadOnlyDictionary<string, object?>> rows) => new([.. rows], null);

    public static WmiScript Missing(CheckReason reason) => new(null, reason);
}

/// <summary>One question that was asked of the fake WMI, with the deadline it was asked under.</summary>
internal sealed record WmiCall(string Class, string Scope, QueryDeadline Deadline);

/// <summary>
/// A WMI reader that never calls Windows. The script is by the WMI class the query is about (the word after FROM): the rows of the answer are
/// run through the query's own mapper, so the mappers are exercised, and a mapper that throws is an error as with the real reader. A class that has
/// no script is an error. Every call is recorded.
/// </summary>
internal sealed class FakeWmiReader : IWmiReader
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<QueryDeadline, CancellationToken, Task<WmiScript>>> _script = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WmiCall> _calls = [];

    public IReadOnlyList<WmiCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<string> Asked => [.. Calls.Select(static c => c.Class)];

    public FakeWmiReader Rows(string wmiClass, params IReadOnlyDictionary<string, object?>[] rows) => Then(wmiClass, (_, _) => Task.FromResult(WmiScript.Of(rows)));

    public FakeWmiReader Fails(string wmiClass, CheckReason reason) => Then(wmiClass, (_, _) => Task.FromResult(WmiScript.Missing(reason)));

    public FakeWmiReader Throws(string wmiClass, Exception exception) => Then(wmiClass, (_, _) => Task.FromException<WmiScript>(exception));

    public FakeWmiReader Then(string wmiClass, Func<QueryDeadline, CancellationToken, Task<WmiScript>> answer)
    {
        lock (_gate)
        {
            _script[wmiClass] = answer;
        }

        return this;
    }

    public async Task<CheckReading<IReadOnlyList<T>>> ReadAsync<T>(WmiQuery<T> query, QueryDeadline deadline, CancellationToken ct)
    {
        var name = ClassOf(query.Text);
        Func<QueryDeadline, CancellationToken, Task<WmiScript>>? answer;
        lock (_gate)
        {
            _calls.Add(new WmiCall(name, query.Scope, deadline));
            _script.TryGetValue(name, out answer);
        }

        if (answer is null)
        {
            return CheckReading<IReadOnlyList<T>>.Failed();
        }

        var script = await answer(deadline, ct).ConfigureAwait(false);
        if (script.Reason is { } reason)
        {
            return CheckReading<IReadOnlyList<T>>.Missing(reason);
        }

        try
        {
            return CheckReading<IReadOnlyList<T>>.Ok([.. script.Rows!.Select(row => query.Map(new FakeRow(row)))]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CheckReading<IReadOnlyList<T>>.Failed();
        }
    }

    /// <summary>The healthy answers of a typical machine for the classes the system check asks, with the disks as given.</summary>
    public static FakeWmiReader Typical(params IReadOnlyDictionary<string, object?>[] disks) =>
        new FakeWmiReader()
            .Rows("MSFT_PhysicalDisk", disks)
            .Rows("Win32_PageFileUsage", Row(("Name", @"C:\pagefile.sys"), ("AllocatedBaseSize", 4096u), ("CurrentUsage", 100u), ("PeakUsage", 500u)))
            .Rows("Win32_ComputerSystem", Row(("AutomaticManagedPagefile", true)))
            .Rows("Win32_VideoController", Row(("PNPDeviceID", @"PCI\VEN_10DE&DEV_2704&SUBSYS_00000000&REV_A1\4&1&0&0019"), ("ConfigManagerErrorCode", 0u)));

    /// <summary>The disks the fake machine has (0 and 1), with the serials Core reads for them, healthy.</summary>
    public static IReadOnlyDictionary<string, object?>[] MachineDisks() =>
        [Disk("0", Hw.SerialOfDisk0), Disk("1", Hw.SerialOfDisk1)];

    /// <summary>Ten healthy disks, 0 to 9, that do not say a serial (the storage tests' drives have their own serials, which would not match).</summary>
    public static IReadOnlyDictionary<string, object?>[] AnyDisks() =>
        [.. Enumerable.Range(0, 10).Select(static n => Disk(n.ToString(System.Globalization.CultureInfo.InvariantCulture), null))];

    public static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(static v => v.Name, static v => v.Value);

    /// <summary>A row of MSFT_PhysicalDisk as the real property types give it (HealthStatus and OperationalStatus are uint16).</summary>
    public static IReadOnlyDictionary<string, object?> Disk(string deviceId, string? serial, int health = 0, params int[] operational) =>
        Row(
            ("DeviceId", deviceId),
            ("SerialNumber", serial),
            ("HealthStatus", (ushort)health),
            ("OperationalStatus", operational.Length == 0 ? new ushort[] { 2 } : operational.Select(static c => (ushort)c).ToArray()));

    private static string ClassOf(string text)
    {
        var from = text.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
        var rest = text[(from + 6)..].Trim();
        var end = rest.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }
}

/// <summary>
/// An event counter that never calls Windows. The script is by item id (<c>system.eventlog.disk7d</c>); an id with no script counts 0. Every
/// query, with the deadline it was asked under, is recorded in the order it was asked.
/// </summary>
internal sealed class FakeEventCounter : IEventCounter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<EventCountQuery, QueryDeadline, CancellationToken, Task<CheckReading<long>>>> _script = new(StringComparer.Ordinal);
    private readonly List<(EventCountQuery Query, QueryDeadline Deadline)> _calls = [];

    public IReadOnlyList<(EventCountQuery Query, QueryDeadline Deadline)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<string> Asked => [.. Calls.Select(static c => c.Query.ItemId)];

    public FakeEventCounter Count(string itemId, long count) => Then(itemId, (_, _, _) => Task.FromResult(CheckReading<long>.Ok(count)));

    public FakeEventCounter Fails(string itemId, CheckReason reason) => Then(itemId, (_, _, _) => Task.FromResult(CheckReading<long>.Missing(reason)));

    public FakeEventCounter Throws(string itemId, Exception exception) => Then(itemId, (_, _, _) => Task.FromException<CheckReading<long>>(exception));

    public FakeEventCounter Then(string itemId, Func<EventCountQuery, QueryDeadline, CancellationToken, Task<CheckReading<long>>> answer)
    {
        lock (_gate)
        {
            _script[itemId] = answer;
        }

        return this;
    }

    public Task<CheckReading<long>> CountAsync(EventCountQuery query, QueryDeadline deadline, CancellationToken ct)
    {
        Func<EventCountQuery, QueryDeadline, CancellationToken, Task<CheckReading<long>>>? answer;
        lock (_gate)
        {
            _calls.Add((query, deadline));
            _script.TryGetValue(query.ItemId, out answer);
        }

        return answer is null ? Task.FromResult(CheckReading<long>.Ok(0)) : answer(query, deadline, ct);
    }
}

/// <summary>
/// A hardware provider that never reads the machine: it answers with <see cref="Machine"/> (every category of it) unless a category has its own
/// script (a delay, a failure, a late answer). It counts the calls and the <see cref="IHardwareProvider.Invalidate"/> calls.
/// </summary>
internal sealed class FakeHardware(HardwareInfo? machine = null) : IHardwareProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<HardwareCategory, Func<CancellationToken, Task<HardwareInfo>>> _script = [];
    private readonly List<HardwareCategory> _asked = [];
    private int _invalidations;

    public HardwareInfo Machine { get; set; } = machine ?? Hw.Machine();

    public int Invalidations => Volatile.Read(ref _invalidations);

    public IReadOnlyList<HardwareCategory> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public FakeHardware Then(HardwareCategory category, Func<CancellationToken, Task<HardwareInfo>> answer)
    {
        lock (_gate)
        {
            _script[category] = answer;
        }

        return this;
    }

    public FakeHardware Throws(HardwareCategory category, Exception exception) => Then(category, _ => Task.FromException<HardwareInfo>(exception));

    public Task<HardwareInfo> GetAsync(HardwareCategory category, CancellationToken ct = default)
    {
        Func<CancellationToken, Task<HardwareInfo>>? answer;
        lock (_gate)
        {
            _asked.Add(category);
            _script.TryGetValue(category, out answer);
        }

        return answer is null ? Task.FromResult(Machine) : answer(ct);
    }

    public void Invalidate() => Interlocked.Increment(ref _invalidations);
}

/// <summary>A machine written by hand: every value is Core's own type, with the values the tests need and a serial, a label and a drive letter that must never reach a result.</summary>
internal static class Hw
{
    public const string SerialOfDisk0 = "S6B0NG0R000000X";

    public const string SerialOfDisk1 = "WD-WCC4E0000000";

    public const string LabelOfC = "OwnersPrivateLabel";

    public const string NvidiaPci = "10DE:2704";

    /// <summary>The moment the fake system check runs at.</summary>
    public static readonly DateTime Now = new(2026, 11, 21, 3, 0, 0, DateTimeKind.Utc);

    private const long Gib = 1024L * 1024 * 1024;

    private const long Tb = 1000L * 1000 * 1000 * 1000;

    public static Value<T> V<T>(T value)
        where T : notnull => Value.Ok(value, "test");

    public static OsHardware Os(string name = "Windows 11 Pro", string build = "10.0.26300", long uptimeSec = 3600, string? displayVersion = "25H2", int ubr = 7171) =>
        new(
            V(name),
            V("10.0"),
            V(build),
            V("x64"),
            V(uptimeSec),
            V("Balanced"),
            V(true),
            V(true),
            new OsDetails(
                V("Pro"),
                displayVersion is null ? Value.Unsupported<string>("test") : V(displayVersion),
                V(ubr),
                V("2026-11-20T00:00:00Z"),
                V("ja-JP")));

    public static CpuHardware Cpu() => FakeHardwareData.Create(Now, HardwareCategory.Cpu).Cpu.Data!;

    public static MemoryModule Module(long gib = 32, int rated = 5600, int configured = 5600) =>
        new(V("DIMM_A2"), V(gib * Gib), V(MemoryType.Ddr5), V(rated), V(configured), V("Maker"), V("PART"), V("00000000"), V(2), V(1100), V("DIMM"));

    public static MemoryHardware Memory(params MemoryModule[] modules) => new(V(64 * Gib), V(4), V(modules.Length), V(256 * Gib), modules);

    public static GpuHardware Gpu(string name = "NVIDIA GeForce RTX 5070", string driverDate = "2026-10-01", string pci = NvidiaPci, string version = "32.0.15.9999") =>
        FakeHardwareData.Create(Now, HardwareCategory.Gpu).Gpus.Data![0] with { Name = V(name), DriverDate = V(driverDate), PciId = V(pci), DriverVersion = V(version) };

    public static StorageVolume Volume(string letter, long total, long free, string label = LabelOfC) => new(letter, label, "NTFS", V(total), V(free));

    public static StorageDevice Disk(int number, string model, string serial, params StorageVolume[] volumes) =>
        new(number, V(model), V(serial), V("FW01"), V(StorageBus.Nvme), V(2 * Tb), V(true), V("GPT"), volumes);

    /// <summary>A slot Core could not look at: nothing about it was read.</summary>
    public static StorageDevice UnreadableDisk(int number) =>
        new(
            number,
            Value.Failed<string>("test"),
            Value.Failed<string>("test"),
            Value.Failed<string>("test"),
            Value.Failed<StorageBus>("test"),
            Value.Failed<long>("test"),
            Value.Failed<bool>("test"),
            Value.Failed<string>("test"),
            []);

    public static StorageDevice[] TwoDisks() =>
    [
        Disk(0, "Samsung SSD 980 PRO 2TB", SerialOfDisk0, Volume("C:", 2 * Tb, Tb)),
        Disk(1, "WDC WD40EZRZ-00GXCB0", SerialOfDisk1, Volume("D:", 4 * Tb, 2 * Tb, "Data")),
    ];

    /// <summary>A healthy typical machine: two 32 GiB modules at their rated speed, one GPU with a young driver, two disks with a volume each.</summary>
    public static HardwareInfo Machine(
        OsHardware? os = null,
        CpuHardware? cpu = null,
        MemoryHardware? memory = null,
        IReadOnlyList<GpuHardware>? gpus = null,
        IReadOnlyList<StorageDevice>? storage = null) =>
        FakeHardwareData.Create(Now) with
        {
            Os = V(os ?? Os()),
            Cpu = V(cpu ?? Cpu()),
            Memory = V(memory ?? Memory(Module(), Module())),
            Gpus = V<IReadOnlyList<GpuHardware>>(gpus ?? [Gpu()]),
            Storage = V<IReadOnlyList<StorageDevice>>(storage ?? TwoDisks()),
        };
}

/// <summary>
/// The readings of a healthy machine as the judge takes them (<see cref="SystemInputs"/>), for the tests of the verdict rules: a test
/// changes the one reading it is about with <c>with</c> and the others stay as they are.
/// </summary>
internal static class SI
{
    public static readonly IReadOnlyDictionary<string, CheckReading<long>> NoEvents = EventCountQuery.AllKinds
        .ToDictionary(static k => EventCountQuery.Create(k, DateTime.UnixEpoch).ItemId, static _ => CheckReading<long>.Ok(0), StringComparer.Ordinal);

    public static RamModuleInput Module(long gib = 32, int rated = 5600, int configured = 5600) =>
        new(CheckReading<long>.Ok(gib * 1024L * 1024 * 1024), CheckReading<int>.Ok(rated), CheckReading<int>.Ok(configured));

    public static GpuInput Gpu(string name = "NVIDIA GeForce RTX 5070", DateOnly? driverDate = null, int status = 0) =>
        new(
            CheckReading<string>.Ok(name),
            CheckReading<string>.Ok("32.0.15.9999"),
            CheckReading<DateOnly>.Ok(driverDate ?? new DateOnly(2026, 10, 1)),
            CheckReading<int>.Ok(status));

    public static VolumeInput Volume(long total, long free, int? disk = 1) => new(CheckReading<long>.Ok(total), CheckReading<long>.Ok(free), disk);

    public static SystemInputs Healthy() =>
        new(
            CheckReading<OsVersionValue>.Ok(new OsVersionValue("Windows 11 Pro", "25H2", 26300, 7171)),
            CheckReading<double>.Ok(3600),
            CheckReading<string>.Ok("AMD Ryzen 9 7950X 16-Core Processor"),
            CheckReading<int>.Ok(16),
            CheckReading<int>.Ok(32),
            CheckReading<double>.Ok(4500),
            CheckReading<IReadOnlyList<RamModuleInput>>.Ok([Module(), Module()]),
            CheckReading<IReadOnlyList<GpuInput>>.Ok([Gpu()]),
            CheckReading<IReadOnlyList<DiskInput>>.Ok([new DiskInput("Samsung SSD 980 PRO 2TB", DiskHealthVerdict.Healthy)]),
            CheckReading<IReadOnlyList<VolumeInput>>.Ok([Volume(2_000_000_000_000, 1_000_000_000_000)]),
            CheckReading<IReadOnlyList<PageFileInput>>.Ok([new PageFileInput(4096, 100, 500)]),
            CheckReading<bool>.Ok(true),
            NoEvents);

    /// <summary>The rows of the inputs by id (and subject), for a test of one row. Several rows of one id on different subjects are told apart by the subject.</summary>
    public static CheckItem Row(IEnumerable<CheckItem> items, string id, string? subject = null) =>
        items.Single(i => i.Id == id && i.Subject?.Id == subject);

    public static IReadOnlyList<CheckItem> Judge(SystemInputs inputs) => SystemJudge.Judge(inputs, Hw.Now);

    /// <summary>The rows pass the strict validator as the items of one system result.</summary>
    public static CheckResult Result(IReadOnlyList<CheckItem> items) =>
        CheckResult.Create(Samples.App, Samples.SystemRun, CheckKind.System, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, items);

    public static void AssertValid(IReadOnlyList<CheckItem> items)
    {
        var problems = CheckResultValidator.Validate(Result(items));
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }
}
