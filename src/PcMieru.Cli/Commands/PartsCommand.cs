using System.Text;
using PcMieru.Core;
using PcMieru.Core.Hardware.Share;

namespace PcMieru.Cli.Commands;

/// <summary>
/// <c>pcmi parts --share</c>: the model numbers of this PC as "pcm-parts/1" JSON (see PartsShare), for pasting into the web tool.
/// Model numbers only; the document never holds a serial number, MAC, IP address, PC or user name, path, date or version string.
/// </summary>
public sealed class PartsCommand : ICliCommand
{
    public string Name => "parts";

    public string Usage => "pcmi parts --share [--include-usb] [-o file]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("share"),
        new("include-usb"),
        new("output", 'o', TakesValue: true),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.parts", "型番だけの一覧（--share）。シリアル番号・MAC・PC の名前などは含まない", "Model numbers only (--share); no serial numbers, MAC addresses or PC name"),
        new("cli.parts.needShare", "型番の一覧を出すには --share を付けてください（pcmi parts --share）", "Add --share to print the model numbers (pcmi parts --share)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        if (!args.Has("share"))
        {
            throw new ArgException(context.Text("cli.parts.needShare"));
        }

        var insights = context.Services().Insights;
        var json = await PartsShare.CreateJsonAsync(
            (category, ct) => insights.GetHardwareAsync(category, ct),
            VersionCommand.CurrentVersion,
            args.Has("include-usb"),
            context.Ct).ConfigureAwait(false);

        var path = args.Get("output");
        if (path is null)
        {
            context.Out.WriteLine(json);
        }
        else
        {
            await File.WriteAllTextAsync(path, json + "\n", new UTF8Encoding(false), context.Ct).ConfigureAwait(false);
        }

        return ExitCode.Success;
    }
}
