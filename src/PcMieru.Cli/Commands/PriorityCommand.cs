using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli.Commands;

/// <summary>Changes the priority class of one process. Realtime is deliberately not offered: it can freeze the whole PC.</summary>
public sealed class PriorityCommand : ICliCommand
{
    public string Name => "priority";

    public string Usage => "pcmi priority --pid N [--started-at T] [--elevate] <idle|below|normal|above|high>";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("pid", TakesValue: true),
        new("started-at", TakesValue: true),
        new("elevate"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.priority", "優先度を変える（リアルタイムは出さない）", "Change the priority (realtime is not offered)"),
        new("cli.priority.set", "優先度を「{level}」にしました", "Priority set to '{level}'"),
        new("cli.priority.level.idle", "低（アイドル）", "idle"),
        new("cli.priority.level.below", "通常以下", "below normal"),
        new("cli.priority.level.normal", "通常", "normal"),
        new("cli.priority.level.above", "通常以上", "above normal"),
        new("cli.priority.level.high", "高", "high"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, 1);
        var level = ParseLevel(args.Positionals[0]);
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
            () => control.SetPriorityAsync(key, level, context.Ct),
            (nonce, path) => new HelperRequest(HelperOperation.ProcessPriority, nonce, path) { Pid = key.Pid, CreateTime = key.CreateTime, Priority = level })
            .ConfigureAwait(false);
        context.Out.WriteLine(
            ProcessTargets.Label(process) + ": "
            + (result.IsSuccess ? context.Format("cli.priority.set", ("level", context.Text("cli.priority.level." + Short(level)))) : OperationText.Line(context, result)));
        return ExitCodes.From(result.Code);
    }

    /// <summary>"below" and "belownormal" both work. "realtime" gets its own message so nobody wonders where it went.</summary>
    internal static SettablePriority ParseLevel(string text)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "idle":
                return SettablePriority.Idle;
            case "below" or "belownormal":
                return SettablePriority.BelowNormal;
            case "normal":
                return SettablePriority.Normal;
            case "above" or "abovenormal":
                return SettablePriority.AboveNormal;
            case "high":
                return SettablePriority.High;
            case "realtime":
                throw new ArgException("realtime priority is not offered (it can freeze the whole PC); use idle|below|normal|above|high");
            default:
                throw new ArgException("priority must be one of idle|below|normal|above|high");
        }
    }

    private static string Short(SettablePriority level) => level switch
    {
        SettablePriority.Idle => "idle",
        SettablePriority.BelowNormal => "below",
        SettablePriority.AboveNormal => "above",
        SettablePriority.High => "high",
        _ => "normal",
    };
}
