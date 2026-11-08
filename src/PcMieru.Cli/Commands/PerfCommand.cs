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

        var sb = new StringBuilder(TreeText.Render(PcMieruJson.ToNode(summary with { Observations = [] }), context.Language));
        foreach (var observation in summary.Observations)
        {
            sb.Append("  ! ").Append(CliText.Printable(Strings.Render(observation, context.Language))).Append('\n');
        }

        context.Out.Write(sb.ToString());
        return ExitCode.Success;
    }
}
