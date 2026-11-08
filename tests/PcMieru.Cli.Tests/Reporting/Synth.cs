using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Tests.Reporting;

/// <summary>Synthetic snapshots for the rule tests: every number is chosen by the test, nothing comes from this PC.</summary>
internal static class Synth
{
    public const long GiB = 1024L * 1024 * 1024;

    public static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    public static Value<double> Pct(double value) => Value.Ok(value, "test");

    public static ProcessInfo Process(uint pid, string name, double cpu = 0.5, long privateBytes = 100_000_000, double io = 0) =>
        new(
            new ProcessKey(pid, 5000 + pid),
            name,
            4,
            new ProcessKey(4, 5004),
            1,
            4,
            10,
            8,
            privateBytes,
            privateBytes,
            privateBytes,
            0,
            0,
            Value.Ok(cpu, "test"),
            Value.Ok(io, "test"),
            Value.Ok(0.0, "test"),
            Value.NotCollected<double>("test"),
            Value.Ok(ProcessState.Running, "test"));

    public static DiskInfo Disk(string id, double active, double? responseMs = 5, double read = 1_000_000, double write = 500_000) =>
        new(
            id,
            "Disk " + id,
            Pct(active),
            Value.Ok(read, "test"),
            Value.Ok(write, "test"),
            responseMs is { } ms ? Value.Ok(ms, "test") : Value.NotCollected<double>("test"));

    public static GpuInfo Gpu(string id, double utilization, double? temperature = 60) =>
        new(
            id,
            "GPU " + id,
            Pct(utilization),
            [],
            Value.Ok(4 * GiB, "test"),
            Value.Ok(12 * GiB, "test"),
            Value.Ok(GiB, "test"),
            temperature is { } t ? Value.Ok(t, "test") : Value.NotCollected<double>("test"),
            Value.NotCollected<double>("test"),
            Value.NotCollected<double>("test"),
            Value.NotCollected<double>("test"));

    /// <summary>One observation. <paramref name="perCore"/> null means no per-core numbers; the total is what the test says.</summary>
    public static Snapshot Snap(
        int index,
        double total = 10,
        double[]? perCore = null,
        int intervalMs = 1000,
        long totalMem = 16 * GiB,
        long availableMem = 8 * GiB,
        double? commitPercent = 40,
        IReadOnlyList<DiskInfo>? disks = null,
        IReadOnlyList<GpuInfo>? gpus = null,
        IReadOnlyList<ProcessInfo>? processes = null)
    {
        perCore ??= [total, total, total, total];
        var limit = 32 * GiB;
        var taken = T0.AddMilliseconds(index * 1000.0);
        return new Snapshot(
            Snapshot.CurrentSchemaVersion,
            taken,
            taken.AddMilliseconds(-intervalMs),
            intervalMs,
            new SystemInfo(
                new CpuInfo(perCore.Length, Pct(total), perCore.Select(Pct).ToArray(), Value.Ok(3600.0, "test")),
                new MemoryInfo(
                    totalMem,
                    availableMem,
                    commitPercent is { } c ? Value.Ok((long)(limit * c / 100.0), "test") : Value.NotCollected<long>("test"),
                    Value.Ok(limit, "test"),
                    Value.NotCollected<long>("test"),
                    Value.NotCollected<long>("test"),
                    Value.NotCollected<long>("test")),
                disks ?? [],
                [],
                gpus ?? [],
                86_400,
                Value.Ok((disks ?? []).Count, "test"),
                Value.Ok(0, "test"),
                Value.Ok((gpus ?? []).Count, "test")),
            processes ?? [Process(100, "worker.exe", 5)]);
    }

    public static ObservationInput Input(Snapshot current, params Snapshot[] history) => new(current, history);

    public static DriveHealth Nvme(
        int number,
        string model = "Test NVMe 1TB",
        int? criticalWarning = 0,
        double? spare = 100,
        double? spareThreshold = 10,
        double? used = 3,
        StorageHealthKind kind = StorageHealthKind.Nvme)
    {
        var health = new StorageHealth(
            kind,
            Value.Ok(40.0, "test"),
            used is { } u ? Value.Ok(u, "test") : Value.NotCollected<double>("test"),
            spare is { } s ? Value.Ok(s, "test") : Value.NotCollected<double>("test"),
            spareThreshold is { } t ? Value.Ok(t, "test") : Value.NotCollected<double>("test"),
            criticalWarning is { } w ? Value.Ok(w, "test") : Value.NotCollected<int>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            Value.NotCollected<U128>("test"),
            []);
        return new DriveHealth(number, model, Value.Ok(health, "test"));
    }
}
