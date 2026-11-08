using System.Text;
using PcMieru.Core;
using PcMieru.Core.Reporting;

namespace PcMieru.Cli.Commands;

public sealed class ReportCommand : ICliCommand
{
    public string Name => "report";

    public string Usage => "pcmi report [--duration 10s] [--format markdown|json] [--no-redact] [-o file]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("duration", TakesValue: true),
        new("format", TakesValue: true),
        new("no-redact"),
        new("output", 'o', TakesValue: true),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.report", "診断レポート（AI に貼れる形。既定で個人情報を伏せる）", "Diagnostic report to paste into an AI chat (personal data is redacted by default)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var duration = args.GetDuration("duration", TimeSpan.Zero, TimeSpan.FromSeconds(30)) ?? TimeSpan.FromSeconds(10);
        var format = args.Get("format")?.ToLowerInvariant() ?? "markdown";
        if (format is not ("markdown" or "json"))
        {
            throw new ArgException("option '--format' must be one of markdown|json");
        }

        var services = context.Services(redact: !args.Has("no-redact"));
        var document = await services.Insights.GetReportAsync(new ReportOptions((int)Math.Round(duration.TotalSeconds)), context.Ct).ConfigureAwait(false);
        string text;
        if (format == "json")
        {
            text = services.Reports.RenderJson(document, redact: true).ToJsonString(PcMieruJson.Indented) + "\n";
        }
        else
        {
            text = services.Reports.RenderMarkdown(document, context.Language, redact: true, VersionCommand.CurrentVersion);
        }

        var path = args.Get("output");
        if (path is null)
        {
            context.Out.Write(text);
        }
        else
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), context.Ct).ConfigureAwait(false);
        }

        return ExitCode.Success;
    }
}
