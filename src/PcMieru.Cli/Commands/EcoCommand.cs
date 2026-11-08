using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli.Commands;

/// <summary>Efficiency mode (EcoQoS) for one process. It lowers the priority and asks for the power-saving cores.</summary>
public sealed class EcoCommand : ICliCommand
{
    public string Name => "eco";

    public string Usage => "pcmi eco --pid N [--started-at T] [--elevate] on|off";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("pid", TakesValue: true),
        new("started-at", TakesValue: true),
        new("elevate"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.eco", "効率モードの切り替え", "Turn efficiency mode on or off"),
        new("cli.eco.on", "効率モードをオンにしました", "Efficiency mode is on"),
        new("cli.eco.off", "効率モードをオフにしました", "Efficiency mode is off"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, 1);
        var enable = args.Positionals[0].Trim().ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgException("the setting must be on or off"),
        };

        var chosen = await ProcessTargets.ChooseAsync(context, Name, args).ConfigureAwait(false);
        if (chosen.Process is not { } process)
        {
            return chosen.Exit;
        }

        if (!chosen.Verdict.MayOperate)
        {
            return ProcessTargets.Refuse(context, process, chosen.Verdict);
        }

        var key = process.Key;
        var control = context.Services().ProcessControl;
        var result = await HelperElevation.WithElevationAsync(
            context,
            args,
            () => control.SetEfficiencyModeAsync(key, enable, context.Ct),
            (nonce, path) => new HelperRequest(HelperOperation.ProcessEfficiency, nonce, path) { Pid = key.Pid, CreateTime = key.CreateTime, Enable = enable })
            .ConfigureAwait(false);
        context.Out.WriteLine(ProcessTargets.Label(process) + ": " + (result.IsSuccess ? context.Text(enable ? "cli.eco.on" : "cli.eco.off") : OperationText.Line(context, result)));
        return ExitCodes.From(result.Code);
    }
}
