using PcMieru.Core;

namespace PcMieru.Cli.Commands;

public sealed class HealthCommand : ICliCommand
{
    public string Name => "health";

    public string Usage => "pcmi health [--json] [--elevate]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json"), new("elevate")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.health", "ストレージの健康状態（--elevate で管理者の Helper を使う）", "Storage health (--elevate uses the administrator Helper)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var drives = await context.Services().StorageHealth.ReadAsync(args.Has("elevate"), context.Ct).ConfigureAwait(false);
        var report = new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, drives);
        context.Out.Write(args.Has("json") ? PcMieruJson.Serialize(report) + "\n" : TreeText.Render(PcMieruJson.ToNode(report), context.Language));
        return ExitCode.Success;
    }
}
