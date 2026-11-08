using PcMieru.Cli.Mcp;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

public sealed class McpCommand : ICliCommand
{
    public string Name => "mcp";

    public string Usage => "pcmi mcp [--no-redact]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("no-redact")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.mcp", "MCP サーバ（stdio・読み取り専用）。--no-redact は起動時だけの指定", "MCP server over stdio (read-only). --no-redact can only be given at start-up"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var services = context.Services(redact: !args.Has("no-redact"));
        var server = new McpServer(services.Insights, services.Redactor, VersionCommand.CurrentVersion);
        await server.RunAsync(context.In, context.Out, context.Ct).ConfigureAwait(false);
        return ExitCode.Success;
    }
}
