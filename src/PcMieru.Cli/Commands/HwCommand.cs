using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Cli.Commands;

public sealed class HwCommand : ICliCommand
{
    public string Name => "hw";

    public string Usage => "pcmi hw [all|cpu|memory|board|gpu|storage|network|display|audio|usb|battery|os] [--json]";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("json")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.hw", "PC の部品情報（種別を指定可）", "PC hardware information (optionally one category)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 1);
        var category = args.Positionals.Count == 0
            ? HardwareCategory.All
            : ParsedArgs.ParseEnum<HardwareCategory>(args.Positionals[0], "the category");
        var info = await context.Services().Insights.GetHardwareAsync(category, context.Ct).ConfigureAwait(false);
        if (args.Has("json"))
        {
            context.Out.WriteLine(PcMieruJson.Serialize(info));
        }
        else
        {
            context.Out.Write(Render(context, info));
        }

        return ExitCode.Success;
    }

    internal static string Render(CliContext context, HardwareInfo info)
    {
        var sb = new StringBuilder();
        foreach (var (key, node) in PcMieruJson.ToNode(info)!.AsObject())
        {
            if (node is not JsonObject section || !TreeText.IsValue(section) || section["status"]?.GetValue<string>() == "notCollected")
            {
                continue;
            }

            sb.Append('[').Append(context.Text("hw.category." + CategoryKey(key))).Append("]\n");
            TreeText.Write(sb, string.Empty, section, context.Language);
        }

        return sb.ToString();
    }

    private static string CategoryKey(string property) => property switch
    {
        "gpus" => "gpu",
        "displays" => "display",
        _ => property,
    };
}
