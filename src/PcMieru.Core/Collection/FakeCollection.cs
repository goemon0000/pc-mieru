namespace PcMieru.Core.Collection;

/// <summary>Deterministic sample data for the skeleton and for tests. Not a measurement: every source says "fake".</summary>
public static class FakeSnapshots
{
    private const string Src = "fake";

    private static readonly FakeProc[] Procs =
    [
        new("System Idle Process", 0, 0, 0, 0, 0, 0, 8, 0),
        new("System", 4, 0, 0, 1.0, 0.1, 0.1, 190, 4200),
        new("Registry", 124, 4, 0, 0.0, 60, 58, 4, 0),
        new("smss.exe", 580, 4, 0, 0.0, 1, 0.5, 2, 52),
        new("csrss.exe", 780, 580, 0, 0.3, 6, 2.1, 13, 850),
        new("wininit.exe", 860, 580, 0, 0.0, 5, 1.2, 2, 160),
        new("services.exe", 940, 860, 0, 0.1, 10, 5, 9, 640),
        new("lsass.exe", 960, 860, 0, 0.1, 22, 11, 11, 1100),
        new("svchost.exe", 1100, 940, 0, 0.4, 40, 18, 20, 1500),
        new("svchost.exe", 1180, 940, 0, 0.2, 24, 9, 12, 700),
        new("svchost.exe", 1420, 940, 0, 0.1, 18, 6, 8, 420),
        new("dwm.exe", 1700, 860, 1, 1.8, 210, 130, 22, 1900),
        new("explorer.exe", 5200, 5100, 1, 0.9, 240, 140, 70, 3400),
        new("chrome.exe", 7200, 5200, 1, 3.5, 380, 260, 40, 1300),
        new("chrome.exe", 7340, 7200, 1, 1.2, 210, 140, 17, 500),
        new("chrome.exe", 7388, 7200, 1, 2.4, 330, 220, 19, 540),
        new("chrome.exe", 7452, 7200, 1, 0.6, 160, 90, 14, 350),
        new("SkyrimVR.exe", 8800, 5200, 1, 9.5, 5200, 4400, 58, 2600),
        new("vrserver.exe", 8900, 8800, 1, 2.0, 380, 250, 36, 800),
        new("Code.exe", 9300, 5200, 1, 1.5, 420, 300, 33, 900),
        new("Discord.exe", 9600, 5200, 1, 0.8, 330, 210, 44, 780),
        new("Steam.exe", 9900, 5200, 1, 0.5, 190, 120, 38, 700),
        new("OBS64.exe", 10200, 5200, 1, 2.8, 260, 190, 41, 1100),
        new("OneDrive.exe", 10500, 5200, 1, 0.1, 120, 70, 26, 650),
        new("RuntimeBroker.exe", 10800, 1100, 1, 0.0, 30, 12, 6, 280),
        new("SearchHost.exe", 11100, 1100, 1, 0.0, 80, 40, 20, 520),
        new("python.exe", 11800, 9300, 1, 0.2, 90, 60, 5, 110),
        new("notepad.exe", 12400, 5200, 1, 0.0, 25, 9, 4, 210),
    ];

    private readonly record struct FakeProc(
        string Name,
        uint Pid,
        uint Parent,
        uint Session,
        double Cpu,
        double WsMb,
        double PrivMb,
        int Threads,
        int Handles);

    /// <summary>tick 0 has no baseline, so CPU and rates are NotCollected, like the first real sample.</summary>
    public static Snapshot Create(DateTime takenAtUtc, int tick, int intervalMs = 1000)
    {
        takenAtUtc = DateTime.SpecifyKind(takenAtUtc, DateTimeKind.Utc);
        var baseline = tick > 0;
        var origin = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var keys = new Dictionary<uint, ProcessKey>();
        for (var i = 0; i < Procs.Length; i++)
        {
            var created = origin - ((i + 1) * 60L * 10_000_000L) - (Procs[i].Pid % 97);
            keys[Procs[i].Pid] = new ProcessKey(Procs[i].Pid, created);
        }

        var list = new List<ProcessInfo>(Procs.Length);
        for (var i = 0; i < Procs.Length; i++)
        {
            var p = Procs[i];
            var jitter = 0.6 + (0.8 * Noise(i, tick));
            var cpu = p.Pid == 0 ? 80.0 + (5.0 * Noise(i, tick)) : Math.Round(p.Cpu * jitter, 1);
            var ioRead = Math.Round(p.Handles * 40.0 * Noise(i, tick + 1000), 0);
            var ioWrite = Math.Round(p.Handles * 12.0 * Noise(i, tick + 2000), 0);

            Value<double> Rate(double v) => baseline ? Value.Ok(v, Src) : Value.NotCollected<double>(Src, "first sample has no baseline");

            Value<double> gpu = p.Name switch
            {
                "SkyrimVR.exe" => baseline ? Value.Ok(Math.Round(40 + (20 * Noise(i, tick)), 1), "fake:pdh-gpu-engine") : Value.NotCollected<double>("fake:pdh-gpu-engine", "first sample has no baseline"),
                "dwm.exe" => baseline ? Value.Ok(Math.Round(2 + Noise(i, tick), 1), "fake:pdh-gpu-engine") : Value.NotCollected<double>("fake:pdh-gpu-engine", "first sample has no baseline"),
                "csrss.exe" or "lsass.exe" => Value.Denied<double>("fake:pdh-gpu-engine", "no engine instance for this process"),
                _ => Value.NotCollected<double>("fake:pdh-gpu-engine", "no engine instance"),
            };

            Value<ProcessState> state = p.Name switch
            {
                "lsass.exe" => Value.Denied<ProcessState>("fake:thread-state", "protected process"),
                "notepad.exe" => Value.Ok(ProcessState.NotResponding, "fake:IsHungAppWindow"),
                _ => Value.Ok(ProcessState.Running, "fake:IsHungAppWindow"),
            };

            var key = keys[p.Pid];
            list.Add(new ProcessInfo(
                key,
                p.Name,
                p.Parent,
                keys.TryGetValue(p.Parent, out var parentKey) && p.Pid != 0 && p.Parent != p.Pid ? parentKey : null,
                p.Session,
                p.Threads,
                p.Handles,
                p.Pid == 0 ? 0 : 8,
                (long)(p.WsMb * 1024 * 1024),
                (long)(p.PrivMb * 1024 * 1024),
                (long)(p.PrivMb * 1.3 * 1024 * 1024),
                (ulong)p.Handles * 1_000_000UL * (ulong)(1 + (i % 7)) + ((ulong)tick * 4096),
                (ulong)p.Handles * 400_000UL * (ulong)(1 + (i % 5)) + ((ulong)tick * 1024),
                Rate(cpu),
                Rate(ioRead),
                Rate(ioWrite),
                gpu,
                state));
        }

        var logical = 32;
        var perLogical = new List<Value<double>>(logical);
        for (var c = 0; c < logical; c++)
        {
            var v = c == 5 ? 99.5 : Math.Round(2 + (14 * Noise(c, tick + 77)), 1);
            perLogical.Add(baseline ? Value.Ok(v, "fake:ntquery-cpu") : Value.NotCollected<double>("fake:ntquery-cpu", "first sample has no baseline"));
        }

        var total = baseline ? Value.Ok(Math.Round(perLogical.Average(x => x.Data), 1), "fake:ntquery-cpu") : Value.NotCollected<double>("fake:ntquery-cpu", "first sample has no baseline");

        const long gib = 1024L * 1024 * 1024;
        var memory = new MemoryInfo(
            64 * gib,
            (long)((38 + (2 * Noise(1, tick))) * gib),
            Value.Ok((long)((31 + Noise(2, tick)) * gib), "fake:GetPerformanceInfo"),
            Value.Ok(96 * gib, "fake:GetPerformanceInfo"),
            Value.Ok(9 * gib, "fake:GetPerformanceInfo"),
            Value.Ok(420L * 1024 * 1024, "fake:GetPerformanceInfo"),
            Value.Ok(380L * 1024 * 1024, "fake:GetPerformanceInfo"));

        Value<double> R(double v) => baseline ? Value.Ok(Math.Round(v, 1), "fake:pdh") : Value.NotCollected<double>("fake:pdh", "first sample has no baseline");

        var disks = new[]
        {
            new DiskInfo("0 C:", "Samsung SSD 980 PRO 2TB", R(6 + (10 * Noise(3, tick))), R(30e6 * Noise(4, tick)), R(12e6 * Noise(5, tick)), R(0.2 + Noise(6, tick))),
            new DiskInfo("1 D:", "ST4000DM004", R(1 + (3 * Noise(7, tick))), R(2e6 * Noise(8, tick)), R(1e6 * Noise(9, tick)), R(4 + (6 * Noise(10, tick)))),
        };

        var nics = new[]
        {
            new NicInfo("eth0", "Intel(R) Ethernet Controller I225-V", true, R(250_000 * Noise(11, tick)), R(1_800_000 * Noise(12, tick)), Value.Ok(2_500_000_000L, "fake:GetIfTable2")),
            new NicInfo("wlan0", "Wi-Fi", false, Value.NotCollected<double>("fake:GetIfTable2", "link down"), Value.NotCollected<double>("fake:GetIfTable2", "link down"), Value.NotCollected<long>("fake:GetIfTable2", "link down")),
        };

        var gpus = new[]
        {
            new GpuInfo(
                "luid_0x00000000_0x0000d1a2",
                "NVIDIA GeForce RTX 5070",
                baseline ? Value.Ok(Math.Round(42 + (20 * Noise(13, tick)), 1), "fake:pdh-gpu-engine") : Value.NotCollected<double>("fake:pdh-gpu-engine", "first sample has no baseline"),
                [
                    new GpuEngineInfo("3D", baseline ? Value.Ok(Math.Round(42 + (20 * Noise(13, tick)), 1), "fake:pdh-gpu-engine") : Value.NotCollected<double>("fake:pdh-gpu-engine")),
                    new GpuEngineInfo("Copy", baseline ? Value.Ok(Math.Round(3 * Noise(14, tick), 1), "fake:pdh-gpu-engine") : Value.NotCollected<double>("fake:pdh-gpu-engine")),
                    new GpuEngineInfo("VideoEncode", baseline ? Value.Ok(Math.Round(9 * Noise(15, tick), 1), "fake:pdh-gpu-engine") : Value.NotCollected<double>("fake:pdh-gpu-engine")),
                ],
                Value.Ok(5L * 1024 * 1024 * 1024, "fake:pdh-gpu-memory"),
                Value.Ok(12L * 1024 * 1024 * 1024, "fake:dxgi"),
                Value.Ok(600L * 1024 * 1024, "fake:pdh-gpu-memory"),
                Value.Ok(Math.Round(58 + (6 * Noise(16, tick)), 0), "fake:nvml"),
                Value.Ok(Math.Round(160 + (30 * Noise(17, tick)), 0), "fake:nvml"),
                Value.Unsupported<double>("fake:nvml", "this board reports no fan"),
                Value.Ok(2400 + Math.Round(200 * Noise(18, tick), 0), "fake:nvml")),
        };

        return new Snapshot(
            Snapshot.CurrentSchemaVersion,
            takenAtUtc,
            takenAtUtc.AddMilliseconds(-intervalMs),
            intervalMs,
            new SystemInfo(
                new CpuInfo(logical, total, perLogical, Value.Ok(4500.0 + Math.Round(400 * Noise(19, tick), 0), "fake:ntquery-power")),
                memory,
                disks,
                nics,
                gpus,
                93_784 + tick,
                Value.Ok(disks.Length, Src),
                Value.Ok(nics.Length, Src),
                Value.Ok(gpus.Length, Src)),
            list);
    }

    private static double Noise(int a, int b)
    {
        unchecked
        {
            var h = (uint)(a * 73856093) ^ (uint)(b * 19349663) ^ 0x9E3779B9u;
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }
}

public sealed class FakeSnapshotSource : ISnapshotSource, IFakeImplementation
{
    private readonly Func<DateTime> _clock;
    private int _tick;

    public FakeSnapshotSource(Func<DateTime>? clock = null) => _clock = clock ?? (static () => DateTime.UtcNow);

    public Task<Snapshot> SampleAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(FakeSnapshots.Create(_clock(), _tick++));
    }

    public void ResetBaseline() => _tick = 0;

    public void Dispose()
    {
    }
}

public sealed class FakeProcessDetailsProvider : IProcessDetailsProvider, IFakeImplementation
{
    public Task<ProcessDetails> GetAsync(ProcessKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = FakeSnapshots.Create(DateTime.UtcNow, 1);
        var process = snapshot.Processes.FirstOrDefault(p => p.Key == key);
        if (process is null)
        {
            return Task.FromResult(new ProcessDetails(
                key,
                Value.Gone<string>("fake"),
                Value.Gone<string>("fake"),
                Value.Gone<string>("fake"),
                Value.Gone<string>("fake"),
                Value.Gone<string>("fake"),
                Value.Gone<string>("fake"),
                Value.Gone<string>("fake"),
                Value.Gone<bool>("fake"),
                Value.Gone<SignatureInfo>("fake"),
                [],
                null));
        }

        var isWindows = process.SessionId == 0 || process.Name is "explorer.exe" or "dwm.exe";
        var path = isWindows ? $@"C:\Windows\System32\{process.Name}" : $@"C:\Program Files\Fake\{process.Name}";
        return Task.FromResult(new ProcessDetails(
            key,
            Value.Ok(path, "fake"),
            Value.Ok($"\"{path}\" --sample", "fake"),
            Value.Ok(process.SessionId == 0 ? @"NT AUTHORITY\SYSTEM" : @"FAKE-PC\user", "fake"),
            Value.Ok(Path.GetFileNameWithoutExtension(process.Name), "fake"),
            Value.Ok("Sample process description", "fake"),
            Value.Ok(isWindows ? "Microsoft Corporation" : "Fake Software Inc.", "fake"),
            Value.Ok("x64", "fake"),
            Value.Ok(false, "fake"),
            Value.Ok(new SignatureInfo(SignatureState.SignedEmbedded, isWindows ? "Microsoft Windows" : "Fake Software Inc."), "fake"),
            process.Name == "svchost.exe" ? ["Dnscache", "NlaSvc"] : [],
            null));
    }
}

/// <summary>Takes two samples <paramref name="window"/> apart and returns the second, whose rates are measured over that window.</summary>
public static class SnapshotSampling
{
    public static async Task<Snapshot> MeasureAsync(ISnapshotSource source, TimeSpan window, CancellationToken ct = default)
    {
        await source.SampleAsync(ct).ConfigureAwait(false);
        if (window > TimeSpan.Zero)
        {
            await Task.Delay(window, ct).ConfigureAwait(false);
        }

        return await source.SampleAsync(ct).ConfigureAwait(false);
    }
}
