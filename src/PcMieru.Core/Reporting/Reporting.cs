using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Reporting;

/// <summary>
/// Skeleton rules (two of the section 6 rules) so the vertical slice has something to show. Owner E replaces the body.
/// Findings are evidence-backed and phrased as facts or candidates, never as verdicts.
/// </summary>
public sealed class FakeObservationEngine : IObservationEngine, IFakeImplementation
{
    public IReadOnlyList<Observation> Evaluate(ObservationInput input)
    {
        var found = new List<Observation>();
        var cpu = input.Current.System.Cpu;
        if (cpu.TotalPercent.TryGet(out var total) && total < 30)
        {
            for (var i = 0; i < cpu.PerLogicalPercent.Count; i++)
            {
                if (cpu.PerLogicalPercent[i].TryGet(out var one) && one >= 95)
                {
                    var top = input.Current.Processes
                        .Where(p => p.CpuPercent.IsOk && p.Key.Pid != 0)
                        .OrderByDescending(p => p.CpuPercent.GetOr(0))
                        .Take(3)
                        .ToArray();
                    found.Add(new Observation(
                        "cpu.singleCoreSaturated",
                        ObservationSeverity.Notice,
                        ObservationCertainty.Candidate,
                        "obs.cpu.singleCore",
                        new Dictionary<string, string>
                        {
                            ["cpu"] = i.ToString(CultureInfo.InvariantCulture),
                            ["total"] = total.ToString("0.#", CultureInfo.InvariantCulture),
                            ["top"] = string.Join(", ", top.Select(p => p.Name)),
                        },
                        [
                            new Evidence("cpu.total", total.ToString("0.#", CultureInfo.InvariantCulture), "%"),
                            new Evidence("cpu.logical" + i.ToString(CultureInfo.InvariantCulture), one.ToString("0.#", CultureInfo.InvariantCulture), "%"),
                        ],
                        top.Select(p => p.Key).ToArray()));
                    break;
                }
            }
        }

        var mem = input.Current.System.Memory;
        if (mem.CommitTotalBytes.TryGet(out var commit) && mem.CommitLimitBytes.TryGet(out var limit) && limit > 0 && commit * 10 > limit * 9)
        {
            var percent = 100.0 * commit / limit;
            found.Add(new Observation(
                "memory.commitHigh",
                ObservationSeverity.Warning,
                ObservationCertainty.Fact,
                "obs.memory.commitHigh",
                new Dictionary<string, string> { ["percent"] = percent.ToString("0", CultureInfo.InvariantCulture) },
                [new Evidence("memory.commitPercent", percent.ToString("0.#", CultureInfo.InvariantCulture), "%")],
                []));
        }

        return found;
    }
}

public sealed class FakeKnownProcessCatalog : IKnownProcessCatalog, IFakeImplementation
{
    public KnownProcess? Match(string imageName, string? path, SignatureInfo? signature)
    {
        if (!imageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pathOk = string.Equals(path, @"C:\Windows\System32\svchost.exe", StringComparison.OrdinalIgnoreCase);
        var signerOk = signature?.Signer?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
        return new KnownProcess(
            "svchost",
            "Windows のサービスをまとめて動かす仕組みです。",
            "Hosts several Windows services in one process.",
            pathOk,
            signerOk);
    }
}

/// <summary>Who to hide in text: user name, PC name and the user profile folder.</summary>
public sealed record RedactionContext(string UserName, string MachineName, string UserProfile)
{
    public static RedactionContext FromEnvironment() =>
        new(Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>
/// Redacts by key name (command line, serials, MAC, IP, DNS, SSID) and by text content (profile folder, user name,
/// PC name, UNC server, MAC). Applied to the final JSON tree right before output; there is no per-call switch.
/// </summary>
public sealed partial class SimpleRedactor : IRedactor
{
    public const string Placeholder = "<redacted>";

    private static readonly HashSet<string> BlankKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "commandLine", "serialNumber", "macAddress", "ipAddresses", "dnsServers", "ssid",
    };

    private readonly RedactionContext _context;
    private readonly Regex? _userRegex;
    private readonly Regex? _hostRegex;

    public SimpleRedactor(RedactionContext context, bool enabled)
    {
        _context = context;
        Enabled = enabled;
        _userRegex = context.UserName.Length > 0 ? new Regex(@"\b" + Regex.Escape(context.UserName) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null;
        _hostRegex = context.MachineName.Length > 0 ? new Regex(@"\b" + Regex.Escape(context.MachineName) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null;
    }

    public bool Enabled { get; }

    public string RedactText(string text)
    {
        if (!Enabled || text.Length == 0)
        {
            return text;
        }

        if (_context.UserProfile.Length > 0)
        {
            text = text.Replace(_context.UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        text = UncRegex().Replace(text, @"\\<host>\");
        text = MacRegex().Replace(text, "<mac>");
        if (_hostRegex is not null)
        {
            text = _hostRegex.Replace(text, "<host>");
        }

        if (_userRegex is not null)
        {
            text = _userRegex.Replace(text, "<user>");
        }

        return text;
    }

    public JsonNode? RedactJson(JsonNode? node)
    {
        if (Enabled && node is not null)
        {
            Walk(node);
        }

        return node;
    }

    private void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    if (BlankKeys.Contains(name))
                    {
                        if (child is JsonObject valueObject && valueObject.ContainsKey("status"))
                        {
                            if (valueObject.ContainsKey("value"))
                            {
                                valueObject["value"] = Placeholder;
                            }
                        }
                        else
                        {
                            obj[name] = Placeholder;
                        }
                    }
                    else if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        obj[name] = RedactText(s);
                    }
                    else
                    {
                        Walk(child);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        array[i] = RedactText(s);
                    }
                    else if (child is not null)
                    {
                        Walk(child);
                    }
                }

                break;
        }
    }

    [GeneratedRegex(@"\\\\[^\\/\s""']+\\")]
    private static partial Regex UncRegex();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(\1[0-9A-Fa-f]{2}){4}\b")]
    private static partial Regex MacRegex();
}

/// <summary>Runs an observation window and evaluates it. Shared by Insights and the report builder.</summary>
internal static class ObservationRun
{
    public static async Task<(IReadOnlyList<Snapshot> History, SystemSummary Summary)> RunAsync(
        ISnapshotSource source,
        IObservationEngine engine,
        TimeSpan duration,
        IReadOnlyList<DriveHealth>? storage,
        CancellationToken ct)
    {
        var history = new List<Snapshot> { await source.SampleAsync(ct).ConfigureAwait(false) };
        var until = DateTime.UtcNow + duration;
        do
        {
            var step = until - DateTime.UtcNow;
            if (step > TimeSpan.FromSeconds(1))
            {
                step = TimeSpan.FromSeconds(1);
            }

            if (step > TimeSpan.Zero)
            {
                await Task.Delay(step, ct).ConfigureAwait(false);
            }

            history.Add(await source.SampleAsync(ct).ConfigureAwait(false));
        }
        while (DateTime.UtcNow < until);

        return (history, Summarize(history[^1], history, engine, storage));
    }

    public static SystemSummary Summarize(Snapshot current, IReadOnlyList<Snapshot> history, IObservationEngine engine, IReadOnlyList<DriveHealth>? storage) =>
        new(
            SystemSummary.CurrentSchemaVersion,
            current.TakenAtUtc,
            current.IntervalMs,
            current.System,
            engine.Evaluate(new ObservationInput(current, history, storage)),
            TopByCpu(current, 5));

    public static IReadOnlyList<ProcessInfo> TopByCpu(Snapshot snapshot, int count) =>
        snapshot.Processes
            .Where(p => p.Key.Pid != 0)
            .OrderByDescending(p => p.CpuPercent.GetOr(-1))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray();
}

/// <summary>The data side of the report: summary, hardware, storage health. Redaction happens at the output edge.</summary>
public sealed class ReportBuilder : IReportBuilder
{
    private readonly ISnapshotSource _snapshots;
    private readonly IObservationEngine _observations;
    private readonly IHardwareProvider _hardware;
    private readonly IStorageHealthProvider _storage;
    private readonly IRedactor _redactor;
    private readonly Func<DateTime> _clock;

    public ReportBuilder(
        ISnapshotSource snapshots,
        IObservationEngine observations,
        IHardwareProvider hardware,
        IStorageHealthProvider storage,
        IRedactor redactor,
        Func<DateTime> clock)
    {
        _snapshots = snapshots;
        _observations = observations;
        _hardware = hardware;
        _storage = storage;
        _redactor = redactor;
        _clock = clock;
    }

    public async Task<ReportDocument> BuildAsync(ReportOptions options, CancellationToken ct = default)
    {
        var health = options.IncludeStorageHealth ? await _storage.ReadAsync(false, ct).ConfigureAwait(false) : null;
        var (_, summary) = await ObservationRun.RunAsync(_snapshots, _observations, TimeSpan.FromSeconds(Math.Clamp(options.DurationSec, 0, 30)), health, ct).ConfigureAwait(false);
        var hardware = options.IncludeHardware ? await _hardware.GetAsync(HardwareCategory.All, ct).ConfigureAwait(false) : null;
        return new ReportDocument(
            ReportDocument.CurrentSchemaVersion,
            _clock(),
            _redactor.Enabled,
            options.DurationSec,
            summary,
            hardware,
            health is null ? null : new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, health));
    }
}

/// <summary>Markdown for pasting into an AI chat. Skeleton: sections and the numbers that exist; owner E refines it.</summary>
public static class ReportMarkdown
{
    public static string Render(ReportDocument doc, Language language)
    {
        var ja = language == Language.Ja;
        var sb = new StringBuilder();
        sb.Append("# ").Append(ja ? "PCミエル 診断レポート" : "PC Mieru diagnostic report").Append('\n');
        sb.Append(ja ? "- 作成: " : "- Generated: ").Append(doc.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(ja ? "- 観測: " : "- Observed: ").Append(doc.DurationSec.ToString(CultureInfo.InvariantCulture)).Append(ja ? " 秒" : " s").Append('\n');
        sb.Append(ja ? "- 伏せ字: " : "- Redacted: ").Append(doc.Redacted ? (ja ? "あり" : "yes") : (ja ? "なし" : "no")).Append("\n\n");

        sb.Append("## ").Append(ja ? "気づいたこと" : "Observations").Append('\n');
        if (doc.Summary.Observations.Count == 0)
        {
            sb.Append(ja ? "- 特にありません\n" : "- Nothing notable\n");
        }

        foreach (var o in doc.Summary.Observations)
        {
            sb.Append("- ").Append(Strings.Render(o, language)).Append('\n');
        }

        sb.Append("\n## ").Append(ja ? "いま重いプロセス" : "Top processes").Append('\n');
        sb.Append("| ").Append(ja ? "名前" : "Name").Append(" | CPU % | ").Append(ja ? "メモリ MB" : "Memory MB").Append(" |\n|---|---:|---:|\n");
        foreach (var p in doc.Summary.TopProcesses)
        {
            var cpu = p.CpuPercent.TryGet(out var c) ? c.ToString("0.0", CultureInfo.InvariantCulture) : "-";
            var mem = (p.PrivateWorkingSetBytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture);
            sb.Append("| ").Append(p.Name.Replace("|", "\\|", StringComparison.Ordinal)).Append(" | ").Append(cpu).Append(" | ").Append(mem).Append(" |\n");
        }

        if (doc.Hardware?.Cpu.TryGet(out var cpuHw) == true && cpuHw.Name.TryGet(out var cpuName))
        {
            sb.Append("\n## ").Append(ja ? "部品" : "Hardware").Append("\n- CPU: ").Append(cpuName).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>Read-only facade over the other services. The MCP server sees only this.</summary>
public sealed class Insights : IReadOnlyInsights
{
    private readonly ISnapshotSource _snapshots;
    private readonly IProcessDetailsProvider _details;
    private readonly IHardwareProvider _hardware;
    private readonly IStorageHealthProvider _storage;
    private readonly IObservationEngine _observations;
    private readonly IReportBuilder _reports;
    private readonly TimeSpan _defaultWindow;
    private readonly SemaphoreSlim _observeGate = new(1, 1);

    public Insights(
        ISnapshotSource snapshots,
        IProcessDetailsProvider details,
        IHardwareProvider hardware,
        IStorageHealthProvider storage,
        IObservationEngine observations,
        IReportBuilder reports,
        TimeSpan defaultWindow)
    {
        _snapshots = snapshots;
        _details = details;
        _hardware = hardware;
        _storage = storage;
        _observations = observations;
        _reports = reports;
        _defaultWindow = defaultWindow;
    }

    public async Task<SystemSummary> GetSystemSummaryAsync(CancellationToken ct = default)
    {
        await _observeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await SnapshotSampling.MeasureAsync(_snapshots, _defaultWindow, ct).ConfigureAwait(false);
            return ObservationRun.Summarize(snapshot, [snapshot], _observations, null);
        }
        finally
        {
            _observeGate.Release();
        }
    }

    public async Task<ProcessList> ListProcessesAsync(ProcessQuery query, CancellationToken ct = default)
    {
        await _observeGate.WaitAsync(ct).ConfigureAwait(false);
        Snapshot snapshot;
        try
        {
            snapshot = await SnapshotSampling.MeasureAsync(_snapshots, query.Sample ?? _defaultWindow, ct).ConfigureAwait(false);
        }
        finally
        {
            _observeGate.Release();
        }

        IEnumerable<ProcessInfo> rows = snapshot.Processes;
        if (!string.IsNullOrEmpty(query.NameContains))
        {
            rows = rows.Where(p => p.Name.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = rows.ToArray();
        IEnumerable<ProcessInfo> sorted = query.Sort switch
        {
            ProcessSort.Mem => filtered.OrderByDescending(p => p.PrivateWorkingSetBytes),
            ProcessSort.Io => filtered.OrderByDescending(p => p.IoReadBytesPerSec.GetOr(0) + p.IoWriteBytesPerSec.GetOr(0)),
            ProcessSort.Gpu => filtered.OrderByDescending(p => p.GpuPercent.GetOr(-1)),
            ProcessSort.Name => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(CpuKey),
        };

        if (query.Sort != ProcessSort.Name)
        {
            sorted = ((IOrderedEnumerable<ProcessInfo>)sorted).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        }

        var list = (query.Top is { } top and > 0 ? sorted.Take(top) : sorted).ToArray();
        return new ProcessList(ProcessList.CurrentSchemaVersion, snapshot.TakenAtUtc, snapshot.IntervalMs, query.Sort, filtered.Length, list);
    }

    /// <summary>The idle pseudo-process (PID 0) reports the idle share; it must not top a "heaviest first" list.</summary>
    private static double CpuKey(ProcessInfo p) => p.Key.Pid == 0 ? -2 : p.CpuPercent.GetOr(-1);

    public async Task<ProcessDetail?> GetProcessAsync(uint pid, CancellationToken ct = default)
    {
        Snapshot snapshot;
        await _observeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = await SnapshotSampling.MeasureAsync(_snapshots, _defaultWindow, ct).ConfigureAwait(false);
        }
        finally
        {
            _observeGate.Release();
        }

        var process = snapshot.Processes.FirstOrDefault(p => p.Key.Pid == pid);
        return process is null ? null : new ProcessDetail(process, await _details.GetAsync(process.Key, ct).ConfigureAwait(false));
    }

    public Task<HardwareInfo> GetHardwareAsync(HardwareCategory category, CancellationToken ct = default) =>
        _hardware.GetAsync(category, ct);

    public async Task<StorageHealthReport> GetStorageHealthAsync(CancellationToken ct = default) =>
        new(StorageHealthReport.CurrentSchemaVersion, await _storage.ReadAsync(false, ct).ConfigureAwait(false));

    public async Task<DiagnoseResult> DiagnoseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (!await _observeGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("another observation is already running");
        }

        try
        {
            var health = await _storage.ReadAsync(false, ct).ConfigureAwait(false);
            var seconds = (int)Math.Round(duration.TotalSeconds);
            var (_, summary) = await ObservationRun.RunAsync(_snapshots, _observations, duration, health, ct).ConfigureAwait(false);
            return new DiagnoseResult(DiagnoseResult.CurrentSchemaVersion, seconds, summary);
        }
        finally
        {
            _observeGate.Release();
        }
    }

    public Task<ReportDocument> GetReportAsync(ReportOptions options, CancellationToken ct = default) =>
        _reports.BuildAsync(options, ct);
}

public static class ReportingModule
{
    /// <summary>Owner E: replace the Fakes with the real engine/catalog here. Runs last because it composes the other areas.</summary>
    public static void Register(ServiceRegistry registry, PcMieruOptions options)
    {
        Strings.Register(ReportingStrings.All);
        var engine = new FakeObservationEngine();
        var redactor = new SimpleRedactor(RedactionContext.FromEnvironment(), options.Redact);
        registry.Set<IObservationEngine>(engine);
        registry.Set<IKnownProcessCatalog>(new FakeKnownProcessCatalog());
        registry.Set<IRedactor>(redactor);

        var snapshots = registry.Get<ISnapshotSource>();
        var hardware = registry.Get<IHardwareProvider>();
        var storage = registry.Get<IStorageHealthProvider>();
        var reports = new ReportBuilder(snapshots, engine, hardware, storage, redactor, options.Clock);
        registry.Set<IReportBuilder>(reports);
        registry.Set<IReadOnlyInsights>(new Insights(
            snapshots,
            registry.Get<IProcessDetailsProvider>(),
            hardware,
            storage,
            engine,
            reports,
            options.DefaultSampleWindow));
    }
}

public static class ReportingStrings
{
    public static readonly StringEntry[] All =
    [
        new(
            "obs.cpu.singleCore",
            "CPU 全体は {total}% ですが、論理 CPU {cpu} が 100% 近くです。1 つのスレッドで詰まっている可能性があります（候補）。上位: {top}",
            "Overall CPU is {total}% but logical CPU {cpu} is near 100%. A single thread may be the bottleneck (candidate). Top: {top}"),
        new(
            "obs.memory.commitHigh",
            "コミットが上限の {percent}% に達しています。メモリ不足になりやすい状態です。",
            "Commit is at {percent}% of the limit. Allocations may start to fail."),
        new("report.title", "PCミエル 診断レポート", "PC Mieru diagnostic report"),
        new("report.section.observations", "気づいたこと", "Observations"),
    ];
}
