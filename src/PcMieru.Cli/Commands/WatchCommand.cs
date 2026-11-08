using System.Globalization;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

/// <summary>
/// Streams the machine's numbers and the busiest processes at every interval until Ctrl-C (or <c>--count</c> ticks).
/// <c>--ndjson</c> writes one <see cref="SystemSummary"/> per line, the same shape as <c>perf --json</c>, so another program can follow it.
/// </summary>
public sealed class WatchCommand : ICliCommand
{
    private const int HistoryLength = 30;

    public string Name => "watch";

    public string Usage => "pcmi watch [--interval 1s] [--top N] [--count N] [--ndjson]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("interval", TakesValue: true),
        new("top", TakesValue: true),
        new("count", TakesValue: true),
        new("ndjson"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.watch", "上位プロセスを間隔ごとに流し続ける（Ctrl-C で止める）", "Stream the top processes every interval (stop with Ctrl-C)"),
        new("cli.watch.header", "{time}  CPU {cpu}  メモリ使用 {memory}", "{time}  CPU {cpu}  memory used {memory}"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var interval = args.GetDuration("interval", TimeSpan.FromMilliseconds(250), TimeSpan.FromHours(1)) ?? TimeSpan.FromSeconds(1);
        var top = args.GetInt("top", 1, 100) ?? 5;
        var count = args.GetInt("count", 1, 1_000_000);
        var ndjson = args.Has("ndjson");
        var services = context.Services();
        var history = new List<Snapshot>();
        try
        {
            // The first sample only sets the baseline: CPU is a difference between two samples.
            history.Add(await services.Snapshots.SampleAsync(context.Ct).ConfigureAwait(false));
            for (var tick = 0; count is null || tick < count; tick++)
            {
                await Task.Delay(interval, context.Ct).ConfigureAwait(false);
                var snapshot = await services.Snapshots.SampleAsync(context.Ct).ConfigureAwait(false);
                history.Add(snapshot);
                if (history.Count > HistoryLength)
                {
                    history.RemoveAt(0);
                }

                var topProcesses = Top(snapshot, top);
                if (ndjson)
                {
                    var summary = new SystemSummary(
                        SystemSummary.CurrentSchemaVersion,
                        snapshot.TakenAtUtc,
                        snapshot.IntervalMs,
                        snapshot.System,
                        services.Observations.Evaluate(new ObservationInput(snapshot, history)),
                        topProcesses);
                    context.Out.WriteLine(PcMieruJson.Serialize(summary));
                }
                else
                {
                    context.Out.Write(Block(context, snapshot, topProcesses));
                }

                // stdout is buffered when it is a pipe; a follower must see each tick when it happens.
                await context.Out.FlushAsync(context.Ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.Ct.IsCancellationRequested)
        {
            // Ctrl-C is how watch is meant to end.
        }

        return ExitCode.Success;
    }

    private static List<ProcessInfo> Top(Snapshot snapshot, int count) =>
        snapshot.Processes
            .Where(p => p.Key.Pid != 0)
            .OrderByDescending(p => p.CpuPercent.GetOr(-1))
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();

    private static string Block(CliContext context, Snapshot snapshot, IReadOnlyList<ProcessInfo> top)
    {
        var memory = snapshot.System.Memory;
        var used = memory.TotalPhysicalBytes > 0
            ? (memory.TotalPhysicalBytes - memory.AvailablePhysicalBytes) * 100.0 / memory.TotalPhysicalBytes
            : (double?)null;
        var header = context.Format(
            "cli.watch.header",
            ("time", snapshot.TakenAtUtc.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            ("cpu", snapshot.System.Cpu.TotalPercent.TryGet(out var cpu) ? CliText.Num(cpu) + "%" : context.Text("cli.value.notAvailable")),
            ("memory", used is { } percent ? CliText.Num(percent) + "%" : context.Text("cli.value.notAvailable")));
        var table = CliTable.Render(
            [context.Text("cli.col.pid"), context.Text("cli.col.name"), context.Text("cli.col.cpu"), context.Text("cli.col.mem")],
            top.Select(p => (IReadOnlyList<string>)
            [
                p.Key.Pid.ToString(CultureInfo.InvariantCulture),
                p.Name,
                p.CpuPercent.TryGet(out var processCpu) ? CliText.Num(processCpu) : context.Text("cli.value.notAvailable"),
                CliText.Num(p.PrivateWorkingSetBytes / 1024.0 / 1024.0),
            ]).ToList(),
            new HashSet<int> { 0, 2, 3 },
            flexColumn: 1,
            consoleWidth: context.ConsoleWidth);
        return header + "\n" + table + "\n";
    }
}
