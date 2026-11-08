using System.Globalization;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

public sealed class ProcCommand : ICliCommand
{
    public string Name => "proc";

    public string Usage => "pcmi proc <pid> [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.proc", "1 つのプロセスの詳細（パス・署名・コマンドライン）", "Details of one process (path, signature, command line)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, 1);
        if (!uint.TryParse(args.Positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            throw new ArgException("the PID must be a whole number");
        }

        var detail = await context.Services().Insights.GetProcessAsync(pid, context.Ct).ConfigureAwait(false);
        if (detail is null)
        {
            context.Err.WriteLine("pcmi proc: " + context.Text("cli.notFound.pid"));
            return ExitCode.NotFound;
        }

        context.Out.Write(args.Has("json") ? PcMieruJson.Serialize(detail) + "\n" : TreeText.Render(PcMieruJson.ToNode(detail), context.Language));
        return ExitCode.Success;
    }
}
