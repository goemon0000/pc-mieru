using System.Reflection;
using System.Text.Json.Nodes;
using PcMieru.Cli.Mcp;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

public sealed class VersionCommand : ICliCommand
{
    public string Name => "version";

    public string Usage => "pcmi version [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.version", "版を表示", "Show the version"),
    ];

    public static string CurrentVersion
    {
        get
        {
            var text = typeof(VersionCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = text.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? text : text[..plus];
        }
    }

    public Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        if (args.Has("json"))
        {
            var node = new JsonObject
            {
                ["name"] = "pcmi",
                ["version"] = CurrentVersion,
                ["mcpProtocolVersions"] = new JsonArray(McpServer.SupportedVersions.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            };
            context.Out.WriteLine(node.ToJsonString());
        }
        else
        {
            context.Out.WriteLine("pcmi " + CurrentVersion);
        }

        return Task.FromResult(ExitCode.Success);
    }
}
