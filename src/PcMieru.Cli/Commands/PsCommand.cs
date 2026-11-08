using System.Text;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

public sealed class PsCommand : ICliCommand
{
    public string Name => "ps";

    public string Usage => "pcmi ps [--sort cpu|mem|io|gpu|name] [--top N] [--sample 1s] [--tree] [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("sort", TakesValue: true),
        new("top", TakesValue: true),
        new("sample", TakesValue: true),
        new("tree"),
        new("json"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.ps", "プロセスの一覧（CPU は --sample の間だけ測ってから出す）", "List processes (CPU needs a short sample, see --sample)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var query = new ProcessQuery(
            args.GetEnum<ProcessSort>("sort") ?? ProcessSort.Cpu,
            args.GetInt("top", 1, 100_000),
            null,
            args.GetDuration("sample", TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        var list = await context.Services().Insights.ListProcessesAsync(query, context.Ct).ConfigureAwait(false);
        if (args.Has("json"))
        {
            context.Out.WriteLine(PcMieruJson.Serialize(list));
            return ExitCode.Success;
        }

        Render(context, list, args.Has("tree"));
        return ExitCode.Success;
    }

    private static void Render(CliContext context, ProcessList list, bool tree)
    {
        var rows = tree ? Tree(list.Processes) : list.Processes.Select(p => (Process: p, Depth: 0)).ToList();
        string[] headers =
        [
            context.Text("cli.col.pid"),
            context.Text("cli.col.name"),
            context.Text("cli.col.cpu"),
            context.Text("cli.col.mem"),
            context.Text("cli.col.threads"),
            context.Text("cli.col.state"),
        ];

        var cells = rows.Select(r => new[]
        {
            r.Process.Key.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new string(' ', r.Depth * 2) + CliText.Printable(r.Process.Name),
            r.Process.CpuPercent.TryGet(out var cpu) ? CliText.Num(cpu) : context.Text("cli.value.notAvailable"),
            CliText.Num(r.Process.PrivateWorkingSetBytes / 1024.0 / 1024.0, "0.0"),
            r.Process.ThreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StateText(context, r.Process),
        }).ToList();

        var widths = headers.Select(CliText.Width).ToArray();
        foreach (var row in cells)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], CliText.Width(row[i]));
            }
        }

        if (context.ConsoleWidth > 0)
        {
            var fixedWidth = widths.Sum() - widths[1] + (widths.Length - 1) * 2;
            widths[1] = Math.Max(12, Math.Min(widths[1], context.ConsoleWidth - fixedWidth - 1));
        }

        var sb = new StringBuilder();
        AppendRow(sb, headers, widths);
        foreach (var row in cells)
        {
            AppendRow(sb, row, widths);
        }

        context.Out.Write(sb.ToString());
        var footer = Strings.Format(
            "cli.ps.footer",
            new Dictionary<string, string>
            {
                ["shown"] = list.Processes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["total"] = list.TotalProcesses.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sort"] = list.Sort.ToString().ToLowerInvariant(),
            },
            context.Language);
        context.Out.WriteLine(footer);
    }

    private static void AppendRow(StringBuilder sb, string[] row, int[] widths)
    {
        for (var i = 0; i < row.Length; i++)
        {
            var text = i == 1 ? CliText.Truncate(row[i], widths[i]) : row[i];
            sb.Append(i == row.Length - 1 ? text : i == 1 ? CliText.PadRight(text, widths[i]) : CliText.PadLeft(text, widths[i]));
            sb.Append(i < row.Length - 1 ? "  " : "\n");
        }
    }

    private static string StateText(CliContext context, ProcessInfo p) =>
        p.State.TryGet(out var state)
            ? context.Text("collection.state." + char.ToLowerInvariant(state.ToString()[0]) + state.ToString()[1..])
            : context.Text("cli.value.notAvailable");

    /// <summary>Depth-first by parent, keeping the incoming order among siblings. A parent outside the list makes a root.</summary>
    private static List<(ProcessInfo Process, int Depth)> Tree(IReadOnlyList<ProcessInfo> processes)
    {
        var present = processes.Select(p => p.Key).ToHashSet();
        var children = processes
            .Where(p => p.ParentKey is { } parent && present.Contains(parent) && parent != p.Key)
            .GroupBy(p => p.ParentKey!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var childKeys = children.Values.SelectMany(c => c).Select(c => c.Key).ToHashSet();
        var result = new List<(ProcessInfo, int)>();
        var seen = new HashSet<ProcessKey>();

        void Visit(ProcessInfo p, int depth)
        {
            if (!seen.Add(p.Key))
            {
                return;
            }

            result.Add((p, depth));
            if (children.TryGetValue(p.Key, out var kids))
            {
                foreach (var kid in kids)
                {
                    Visit(kid, depth + 1);
                }
            }
        }

        foreach (var root in processes.Where(p => !childKeys.Contains(p.Key)))
        {
            Visit(root, 0);
        }

        foreach (var orphan in processes)
        {
            Visit(orphan, 0);
        }

        return result;
    }
}
