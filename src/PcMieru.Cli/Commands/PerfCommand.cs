using System.Globalization;
using System.Text;
using PcMieru.Core;
using PcMieru.Core.Collection;

namespace PcMieru.Cli.Commands;

public sealed class PerfCommand : ICliCommand
{
    public string Name => "perf";

    public string Usage => "pcmi perf [--sample 1s] [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("sample", TakesValue: true), new("json")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.perf", "PC 全体の今の値と気づいたこと", "Whole-machine numbers and observations"),
        new("cli.perf.label.cpu", "CPU", "CPU"),
        new("cli.perf.label.memory", "メモリ", "Memory"),
        new("cli.perf.label.gpu", "GPU", "GPU"),
        new("cli.perf.label.uptime", "起動してから", "Uptime"),
        new("cli.perf.label.window", "測定した時間", "Measured over"),
        new("cli.perf.cpu.processors", "論理プロセッサ {count} 個", "{count} logical processors"),
        new("cli.perf.cpu.clock", "いま {mhz}", "now {mhz}"),
        new("cli.perf.memory.inUse", "使用中 {used} / {total}（{percent}）、空き {free}", "{used} of {total} in use ({percent}), {free} free"),
        new("cli.perf.memory.commit", "コミット {used} / {limit}", "commit {used} of {limit}"),
        new("cli.perf.gpu.usage", "使用率 {value}", "usage {value}"),
        new("cli.perf.gpu.vram", "専用メモリ {used} / {total}", "dedicated memory {used} of {total}"),
        new("cli.perf.gpu.temperature", "温度 {value}", "temperature {value}"),
        new("cli.perf.gpu.none", "見つかりません", "none found"),
        new("cli.perf.window", "{seconds} 秒", "{seconds} s"),
        new("cli.perf.separator", "、", ", "),
        new("cli.perf.col.disk", "ディスク", "Disk"),
        new("cli.perf.col.active", "使用率%", "Active%"),
        new("cli.perf.col.read", "読み取り/秒", "Read/s"),
        new("cli.perf.col.write", "書き込み/秒", "Write/s"),
        new("cli.perf.col.response", "応答 ms", "Response ms"),
        new("cli.perf.col.nic", "ネットワーク", "Network"),
        new("cli.perf.col.link", "リンク", "Link"),
        new("cli.perf.col.send", "送信/秒", "Send/s"),
        new("cli.perf.col.receive", "受信/秒", "Receive/s"),
        new("cli.perf.nic.up", "接続中", "Up"),
        new("cli.perf.nic.down", "切断", "Down"),
        new("cli.perf.observations", "気づいたこと", "Observations"),
        new("cli.perf.observations.none", "今回の観測では、該当するものはありませんでした", "Nothing matched in this observation"),
        new("cli.perf.top", "CPU を多く使っているプロセス（上位 {count} 件）", "Processes using the most CPU (top {count})"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var sample = args.GetDuration("sample", TimeSpan.Zero, TimeSpan.FromSeconds(30));
        var services = context.Services();
        var snapshot = await SnapshotSampling.MeasureAsync(services.Snapshots, sample ?? services.Options.DefaultSampleWindow, context.Ct).ConfigureAwait(false);
        var top = snapshot.Processes
            .Where(p => p.Key.Pid != 0)
            .OrderByDescending(p => p.CpuPercent.GetOr(-1))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var summary = new SystemSummary(
            SystemSummary.CurrentSchemaVersion,
            snapshot.TakenAtUtc,
            snapshot.IntervalMs,
            snapshot.System,
            services.Observations.Evaluate(new ObservationInput(snapshot, [snapshot])),
            top);
        if (args.Has("json"))
        {
            context.Out.WriteLine(PcMieruJson.Serialize(summary));
            return ExitCode.Success;
        }

        context.Out.Write(Render(context, summary));
        return ExitCode.Success;
    }

    /// <summary>
    /// The summary for people: a few labelled lines, then disks, network, observations and the heaviest processes.
    /// The per-core CPU values stay in --json only.
    /// </summary>
    internal static string Render(CliContext context, SystemSummary summary)
    {
        var sb = new StringBuilder();
        sb.Append(CliFormat.LabelColumn(Lines(context, summary)));

        var system = summary.System;
        if (system.Disks.Count > 0)
        {
            sb.Append('\n').Append(DiskTable(context, system.Disks));
        }
        else if (ListStatus.ExplainsEmptyList(0, system.DisksState))
        {
            sb.Append('\n').Append(NotRead(context, "cli.perf.col.disk", system.DisksState));
        }

        if (system.Nics.Count > 0)
        {
            sb.Append('\n').Append(NicTable(context, system.Nics));
        }
        else if (ListStatus.ExplainsEmptyList(0, system.NicsState))
        {
            sb.Append('\n').Append(NotRead(context, "cli.perf.col.nic", system.NicsState));
        }

        sb.Append('\n').Append(context.Text("cli.perf.observations")).Append('\n');
        if (summary.Observations.Count == 0)
        {
            sb.Append("  ").Append(context.Text("cli.perf.observations.none")).Append('\n');
        }

        foreach (var observation in summary.Observations)
        {
            sb.Append("  ! ").Append(CliText.Printable(Strings.Render(observation, context.Language))).Append('\n');
        }

        if (summary.TopProcesses.Count > 0)
        {
            sb.Append('\n')
                .Append(context.Format("cli.perf.top", ("count", summary.TopProcesses.Count.ToString(CultureInfo.InvariantCulture))))
                .Append('\n')
                .Append(PsCommand.RenderTable(context, summary.TopProcesses.Select(p => (p, 0)).ToList()));
        }

        return sb.ToString();
    }

    private static List<(string Label, string Value)> Lines(CliContext context, SystemSummary summary)
    {
        var separator = context.Text("cli.perf.separator");
        var system = summary.System;
        var lines = new List<(string, string)>
        {
            (context.Text("cli.perf.label.cpu"), CpuText(context, system.Cpu, separator)),
            (context.Text("cli.perf.label.memory"), MemoryText(context, system.Memory, separator)),
        };

        if (system.Gpus.Count == 0)
        {
            lines.Add((
                context.Text("cli.perf.label.gpu"),
                ListStatus.ExplainsEmptyList(0, system.GpusState)
                    ? ListStatus.Describe(system.GpusState, context.Language)
                    : context.Text("cli.perf.gpu.none")));
        }

        for (var i = 0; i < system.Gpus.Count; i++)
        {
            lines.Add((i == 0 ? context.Text("cli.perf.label.gpu") : string.Empty, GpuText(context, system.Gpus[i], separator)));
        }

        lines.Add((context.Text("cli.perf.label.uptime"), CliFormat.Duration(context, system.UptimeSec)));
        lines.Add((
            context.Text("cli.perf.label.window"),
            summary.IntervalMs > 0
                ? context.Format("cli.perf.window", ("seconds", CliText.Num(summary.IntervalMs / 1000.0)))
                : context.Text("cli.value.notAvailable")));
        return lines;
    }

    private static string CpuText(CliContext context, CpuInfo cpu, string separator)
    {
        var parts = new List<string>
        {
            context.Format("cli.perf.cpu.processors", ("count", cpu.LogicalProcessors.ToString(CultureInfo.InvariantCulture))),
        };
        if (cpu.CurrentMhz.TryGet(out var mhz))
        {
            parts.Add(context.Format("cli.perf.cpu.clock", ("mhz", CliFormat.Mhz(mhz))));
        }

        return CliFormat.OrStatus(context, cpu.TotalPercent, CliFormat.Percent) + context.Format("cli.fmt.paren", ("text", string.Join(separator, parts)));
    }

    private static string MemoryText(CliContext context, MemoryInfo memory, string separator)
    {
        if (memory.TotalPhysicalBytes <= 0)
        {
            return Strings.Status(ValueStatus.NotCollected, context.Language);
        }

        var used = memory.TotalPhysicalBytes - memory.AvailablePhysicalBytes;
        var text = context.Format(
            "cli.perf.memory.inUse",
            ("used", CliFormat.Bytes(used)),
            ("total", CliFormat.Bytes(memory.TotalPhysicalBytes)),
            ("percent", CliFormat.Percent(used * 100.0 / memory.TotalPhysicalBytes)),
            ("free", CliFormat.Bytes(memory.AvailablePhysicalBytes)));
        if (memory.CommitTotalBytes.TryGet(out var commit) && memory.CommitLimitBytes.TryGet(out var limit))
        {
            text += separator + context.Format("cli.perf.memory.commit", ("used", CliFormat.Bytes(commit)), ("limit", CliFormat.Bytes(limit)));
        }

        return text;
    }

    private static string GpuText(CliContext context, GpuInfo gpu, string separator)
    {
        var parts = new List<string>
        {
            context.Format("cli.perf.gpu.usage", ("value", CliFormat.OrStatus(context, gpu.UtilizationPercent, CliFormat.Percent))),
        };
        if (gpu.DedicatedUsedBytes.IsOk || gpu.DedicatedTotalBytes.IsOk)
        {
            parts.Add(context.Format(
                "cli.perf.gpu.vram",
                ("used", CliFormat.OrDash(gpu.DedicatedUsedBytes, b => CliFormat.Bytes(b))),
                ("total", CliFormat.OrDash(gpu.DedicatedTotalBytes, b => CliFormat.Bytes(b)))));
        }

        if (gpu.TemperatureC.TryGet(out var temperature))
        {
            parts.Add(context.Format("cli.perf.gpu.temperature", ("value", CliText.Num(temperature, "0") + " °C")));
        }

        return CliText.Printable(gpu.Name) + context.Format("cli.fmt.paren", ("text", string.Join(separator, parts)));
    }

    /// <summary>A list that is empty because it could not be read says so, in place of its table (an empty table would read as "none").</summary>
    private static string NotRead(CliContext context, string labelKey, Value<int> state) =>
        CliFormat.LabelColumn([(context.Text(labelKey), ListStatus.Describe(state, context.Language))]);

    private static string DiskTable(CliContext context, IReadOnlyList<DiskInfo> disks)
    {
        string[] headers =
        [
            context.Text("cli.perf.col.disk"),
            context.Text("cli.perf.col.active"),
            context.Text("cli.perf.col.read"),
            context.Text("cli.perf.col.write"),
            context.Text("cli.perf.col.response"),
        ];
        var rows = disks.Select(d => (IReadOnlyList<string>)new[]
        {
            d.Name,
            CliFormat.OrDash(d.ActivePercent, v => CliText.Num(v)),
            CliFormat.OrDash(d.ReadBytesPerSec, CliFormat.Rate),
            CliFormat.OrDash(d.WriteBytesPerSec, CliFormat.Rate),
            CliFormat.OrDash(d.ResponseTimeMs, v => CliText.Num(v)),
        }).ToList();
        return CliTable.Render(headers, rows, new HashSet<int> { 1, 2, 3, 4 }, flexColumn: 0, consoleWidth: context.ConsoleWidth);
    }

    private static string NicTable(CliContext context, IReadOnlyList<NicInfo> nics)
    {
        string[] headers =
        [
            context.Text("cli.perf.col.nic"),
            context.Text("cli.col.state"),
            context.Text("cli.perf.col.send"),
            context.Text("cli.perf.col.receive"),
            context.Text("cli.perf.col.link"),
        ];
        var rows = nics.Select(n => (IReadOnlyList<string>)new[]
        {
            n.Name,
            context.Text(n.IsUp ? "cli.perf.nic.up" : "cli.perf.nic.down"),
            CliFormat.OrDash(n.SendBytesPerSec, CliFormat.Rate),
            CliFormat.OrDash(n.ReceiveBytesPerSec, CliFormat.Rate),
            CliFormat.OrDash(n.LinkSpeedBitsPerSec, CliFormat.LinkSpeed),
        }).ToList();
        return CliTable.Render(headers, rows, new HashSet<int> { 2, 3, 4 }, flexColumn: 0, consoleWidth: context.ConsoleWidth);
    }
}
