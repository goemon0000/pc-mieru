using System.Security.Cryptography;
using System.Text;
using PcMieru.Checks.Storage;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.Storage;

/// <summary>
/// The baseline file system of a test: the real folder underneath (a fresh temp folder), a log, and a hook per call that may make one
/// call fail. The history's own fault file system does the rest.
/// </summary>
internal sealed class FaultBaselineFiles(FaultFileSystem inner) : IBaselineFileSystem
{
    private readonly object _gate = new();
    private readonly List<string> _replaces = [];

    public FaultFileSystem Inner => inner;

    /// <summary>Called with (source, destination) before the real replace; the exception, when given, replaces it.</summary>
    public Func<string, string, Exception?>? ReplaceFault { get; set; }

    /// <summary>The destination file names of every replace, in order.</summary>
    public IReadOnlyList<string> Replaces
    {
        get
        {
            lock (_gate)
            {
                return [.. _replaces];
            }
        }
    }

    public IReadOnlyList<HistoryFileEntry> ListFiles(string directory) => inner.ListFiles(directory);

    public byte[] ReadAllBytes(string path, int maxBytes) => inner.ReadAllBytes(path, maxBytes);

    public bool Exists(string path) => inner.Exists(path);

    public void CreateDirectory(string directory) => inner.CreateDirectory(directory);

    public ICheckHistoryNewFile CreateNew(string path) => inner.CreateNew(path);

    public void Move(string source, string destination) => inner.Move(source, destination);

    public void Delete(string path) => inner.Delete(path);

    public void Replace(string source, string destination)
    {
        lock (_gate)
        {
            _replaces.Add(Path.GetFileName(destination));
        }

        if (ReplaceFault?.Invoke(source, destination) is { } fault)
        {
            throw fault;
        }

        PhysicalBaselineFileSystem.Instance.Replace(source, destination);
    }
}

/// <summary>The old side: the real folder, read only (the interface has no way to write), with hooks that make a listing or a read fail.</summary>
internal sealed class FaultReader : ICheckHistoryFileReader
{
    private readonly List<string> _reads = [];

    public IReadOnlyList<string> Reads => [.. _reads];

    public Func<string, Exception?>? ListFault { get; set; }

    public Func<string, Exception?>? ReadFault { get; set; }

    public IReadOnlyList<HistoryFileEntry> ListFiles(string directory)
    {
        if (ListFault?.Invoke(directory) is { } fault)
        {
            throw fault;
        }

        return PhysicalHistoryFileReader.Instance.ListFiles(directory);
    }

    public byte[] ReadAllBytes(string path, int maxBytes)
    {
        _reads.Add(Path.GetFileName(path));
        if (ReadFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        return PhysicalHistoryFileReader.Instance.ReadAllBytes(path, maxBytes);
    }

    public bool Exists(string path) => PhysicalHistoryFileReader.Instance.Exists(path);
}

/// <summary>A lock that counts and can be refused, and that runs a hook just before it is taken (so a test can make a second run happen "in between").</summary>
internal sealed class FakeBaselineLock : IBaselineLock
{
    private int _depth;

    public int Taken { get; private set; }

    public int Released { get; private set; }

    public int MaxDepth { get; private set; }

    /// <summary>The lock cannot be taken (timeout): <see cref="TryAcquire"/> returns null.</summary>
    public bool Refuse { get; set; }

    /// <summary>Called with the number of acquisitions so far (0 for the first one), before the lock is taken.</summary>
    public Action<int>? BeforeAcquire { get; set; }

    public bool IsHeld => _depth > 0;

    public IDisposable? TryAcquire(TimeSpan timeout)
    {
        BeforeAcquire?.Invoke(Taken);
        if (Refuse)
        {
            return null;
        }

        Taken++;
        _depth++;
        MaxDepth = Math.Max(MaxDepth, _depth);
        return new Release(this);
    }

    private sealed class Release(FakeBaselineLock owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            owner._depth--;
            owner.Released++;
        }
    }
}

/// <summary>Builders of the inspected drives: NVMe and SATA drives with the counters v0.7.4 stored, and slots that could not be read.</summary>
internal static class Drives
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, 123, TimeSpan.Zero);

    public const string NvmeKey = "v2|sn=S6B1NJ0R123456|uid=eui.0025385b21a01234|model=Samsung SSD 980 PRO 2TB|size=2000398934016|bus=Nvme";

    public const string SataKey = "v2|sn=WD-WX12A|uid=|model=WDC WD40EZRZ-00GXCB0|size=4000787030016|bus=Sata";

    private static Value<U128> Wide(ulong value) => Value.Ok(new U128(value), "test");

    public static StorageInspectionDrive Nvme(
        string serial = "S6B1NJ0R123456",
        DateTimeOffset? at = null,
        ulong media = 0,
        ulong poh = 100,
        ulong written = 1000,
        ulong unsafeShutdowns = 5,
        ulong errorLog = 0,
        double used = 3,
        double spare = 100,
        int critical = 0,
        string location = "",
        string uniqueId = "eui.0025385b21a01234",
        string model = "Samsung SSD 980 PRO 2TB",
        long size = 2000398934016,
        int number = 0,
        StorageIdentityConfirmation confirmation = StorageIdentityConfirmation.Confirmed)
    {
        var health = new StorageHealth(
            StorageHealthKind.Nvme,
            Value.Ok(41.0, "test"),
            Value.Ok(used, "test"),
            Value.Ok(spare, "test"),
            Value.Ok(10.0, "test"),
            Value.Ok(critical, "test"),
            Wide(7),
            Wide(7),
            Wide(poh),
            Wide(11),
            Wide(unsafeShutdowns),
            Wide(media),
            []);
        var counters = new StorageInspectionCounters(StorageHealthKind.Nvme, new U128(errorLog), new U128(written), null, false, [], null, false);
        return new StorageInspectionDrive(number, StorageRecognition.Recognized, model, serial, "FW1", 17, size, uniqueId, location, at ?? T0, Value.Ok(health, "test"), confirmation, counters);
    }

    public static StorageInspectionDrive Sata(
        string serial = "WD-WX12A",
        DateTimeOffset? at = null,
        ulong hours = 1000,
        ulong reallocated = 0,
        ulong pending = 0,
        ulong crc = 0,
        string location = "",
        string uniqueId = "",
        string model = "WDC WD40EZRZ-00GXCB0",
        long size = 4000787030016,
        int number = 1,
        StorageIdentityConfirmation confirmation = StorageIdentityConfirmation.Confirmed)
    {
        SmartAttribute Attribute(int id, string name, ulong raw) => new(id, name, 100, 100, 0, new U128(raw), true);

        var health = new StorageHealth(
            StorageHealthKind.Sata,
            Value.Ok(33.0, "test"),
            Value.NotCollected<double>("test"),
            Value.NotCollected<double>("test"),
            Value.NotCollected<double>("test"),
            Value.NotCollected<int>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            [Attribute(5, "Reallocated", reallocated), Attribute(9, "PowerOnHours", hours), Attribute(194, "Temperature", 33), Attribute(197, "Pending", pending), Attribute(199, "UDMA CRC", crc)]);
        return new StorageInspectionDrive(number, StorageRecognition.Recognized, model, serial, "FW2", 11, size, uniqueId, location, at ?? T0, Value.Ok(health, "test"), confirmation, null);
    }

    /// <summary>A slot that could not be opened: no descriptor, no key, no health.</summary>
    public static StorageInspectionDrive Unreadable(int number = 7, DateTimeOffset? at = null) =>
        new(number, StorageRecognition.Unreadable, "", "", "", 0, 0, "", "", at ?? T0, Value.NotCollected<StorageHealth>("test"), StorageIdentityConfirmation.NoHealth, null);

    /// <summary>A recognized drive whose health could not be read.</summary>
    public static StorageInspectionDrive NoHealth(string serial = "S6B1NJ0R123456", DateTimeOffset? at = null) =>
        Nvme(serial, at) with { Health = Value.Failed<StorageHealth>("test"), Counters = null, Confirmation = StorageIdentityConfirmation.NoHealth };

    /// <summary>The same drive with one NVMe counter set to a 128-bit number (the engine keeps ulong values only).</summary>
    public static StorageInspectionDrive WithWideMedia(StorageInspectionDrive drive, UInt128 media) =>
        drive with { Health = Value.Ok(drive.Health.Data! with { MediaErrors = Value.Ok(new U128(media), "test") }, "test") };

    /// <summary>The same drive with the data units written given as a 128-bit number and flagged saturated by the reader.</summary>
    public static StorageInspectionDrive WithWrittenUnits(StorageInspectionDrive drive, UInt128 written, bool unrepresentable) =>
        drive with
        {
            Counters = drive.Counters! with
            {
                DataUnitsWritten = new U128(written),
                UnrepresentableCounters = unrepresentable ? [StorageLegacyCounterKeys.DataUnitsWritten] : [],
            },
        };

    /// <summary>
    /// One inspection. The unreadable slots are those among the drives unless <paramref name="unreadableSlots"/> says otherwise (a slot that
    /// could not even be listed has no entry at all).
    /// </summary>
    public static StorageInspection Inspect(DateTimeOffset at, IEnumerable<StorageInspectionDrive> drives, int? unreadableSlots = null)
    {
        var list = drives.ToList();
        return new StorageInspection(at, unreadableSlots ?? list.Count(static d => d.Recognition == StorageRecognition.Unreadable), list);
    }

    public static StorageInspection Inspect(params StorageInspectionDrive[] drives) => Inspect(T0, drives);

    /// <summary>The file name of a key, built here with plain SHA-256 and string work, not with the code under test.</summary>
    public static string FileNameOf(string key, string label)
    {
        var chars = label.Trim().Select(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var prefix = new string(chars).Trim('_');
        if (prefix.Length == 0)
        {
            prefix = "unknown";
        }

        if (prefix.Length > 40)
        {
            prefix = prefix[..40];
        }

        return $"disk-{prefix}-{HashOf(key)}.json";
    }

    public static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8).ToLowerInvariant();
}

/// <summary>One engine on a fresh folder, with every seam in the hand of the test.</summary>
internal sealed class BaselineRig : IDisposable
{
    public BaselineRig(bool withLegacy = true)
    {
        Temp = new TempRoot();
        Root = Path.Combine(Temp.Path, "root");
        Legacy = Path.Combine(Temp.Path, "legacy");
        Faults = new FaultFileSystem();
        Files = new FaultBaselineFiles(Faults);
        OldSide = new FaultReader();
        Lock = new FakeBaselineLock();
        WithLegacy = withLegacy;
        Engine = NewEngine(Lock);
    }

    public TempRoot Temp { get; }

    public string Root { get; }

    public string Baseline => Path.Combine(Root, "baseline");

    public string Legacy { get; }

    public bool WithLegacy { get; }

    public FakeTime Time { get; } = new(Drives.T0);

    public FaultFileSystem Faults { get; }

    public FaultBaselineFiles Files { get; }

    public FaultReader OldSide { get; }

    public FakeBaselineLock Lock { get; }

    public BaselineEngine Engine { get; private set; }

    /// <summary>Another engine on the same folder (another process of the same user): its own lock object, the same files.</summary>
    public BaselineEngine NewEngine(FakeBaselineLock? baselineLock = null) =>
        new(Root, WithLegacy ? Legacy : null, Files, OldSide, baselineLock ?? new FakeBaselineLock(), Time, static _ => { });

    public BaselineRunResult Run(StorageInspection inspection) => Run(Engine, inspection);

    public static BaselineRunResult Run(BaselineEngine engine, StorageInspection inspection) => engine.Complete(engine.BeginRun(), inspection);

    public string PathOf(string name) => Path.Combine(Baseline, name);

    public bool Has(string name) => File.Exists(PathOf(name));

    public string Read(string name) => File.ReadAllText(PathOf(name));

    public IReadOnlyList<string> Names() =>
        Directory.Exists(Baseline) ? Directory.GetFiles(Baseline).Select(static p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToList() : [];

    public IReadOnlyList<string> DiskFiles() =>
        Names().Where(static n => n.StartsWith("disk-", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal) && n != "disk-ordinals.json").ToList();

    public void Dispose() => Temp.Dispose();
}

internal static class BaselineAssert
{
    public static BaselineCounterOutcome Counter(BaselineDiskOutcome disk, string name) =>
        disk.Counters.Single(c => c.CounterId == "storage." + name);

    /// <summary>The rows of every disk pass the strict validator as one storage result.</summary>
    public static IReadOnlyList<CheckItem> ValidRows(BaselineRunResult result, params string[] models)
    {
        var items = new List<CheckItem>();
        foreach (var disk in result.Disks)
        {
            items.AddRange(BaselineDeltaRows.Build(disk, new CheckSubject(disk.DiskId, models.Length > disk.DriveIndex ? models[disk.DriveIndex] : null)));
        }

        var check = CheckResult.Create(Samples.App, Samples.StorageRun, CheckKind.Storage, Samples.Start, Samples.Start.AddSeconds(1), CheckOutcome.Completed, items);
        var problems = CheckResultValidator.Validate(check);
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        return items;
    }

    public static CheckItem Row(IEnumerable<CheckItem> rows, string id) => rows.Single(r => r.Id == id);
}
