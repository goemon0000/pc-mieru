using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli.Commands;

/// <summary>
/// Lists startup items and turns one on or off. Nothing is deleted, so a change can be undone with the opposite command.
/// An item that is already in the wanted state is left alone: writing it again could overwrite what was remembered for undo.
/// </summary>
public sealed class StartupCommand : ICliCommand
{
    public string Name => "startup";

    public string Usage => "pcmi startup list [--json] | enable <id> | disable <id> [--elevate]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("json"),
        new("elevate"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.startup", "スタートアップの一覧と有効・無効", "List startup items and enable or disable them"),
        new("cli.col.id", "ID", "ID"),
        new("cli.col.command", "実行するもの", "Command"),
        new("cli.col.note", "備考", "Note"),
        new("cli.startup.state.enabled", "有効", "enabled"),
        new("cli.startup.state.disabled", "無効", "disabled"),
        new("cli.startup.state.unknown", "不明", "unknown"),
        new("cli.startup.note.listOnly", "一覧のみ（切り替えられません）", "list only (cannot be switched)"),
        new("cli.startup.note.needsAdmin", "切り替えに管理者の許可が要ります", "needs administrator approval to switch"),
        new("cli.startup.footer", "{shown} 件", "{shown} items"),
        new("cli.startup.notFound", "その ID のスタートアップ項目はありません（pcmi startup list で ID を確かめられます）", "No startup item with that ID (see the ID column of `pcmi startup list`)"),
        new("cli.startup.listOnly", "この項目は一覧のみで、切り替えられません", "This item is list-only and cannot be switched"),
        new("cli.startup.already.enabled", "すでに有効です。何も変更していません", "Already enabled. Nothing was changed"),
        new("cli.startup.already.disabled", "すでに無効です。何も変更していません", "Already disabled. Nothing was changed"),
        new("cli.startup.enabled", "有効にしました（消してはいません。無効に戻せます）", "Enabled (nothing was deleted; you can turn it off again)"),
        new("cli.startup.disabled", "無効にしました（消してはいません。有効に戻せます）", "Disabled (nothing was deleted; you can turn it on again)"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, 2);
        var verb = args.Positionals[0].ToLowerInvariant();
        switch (verb)
        {
            case "list":
                args.RequirePositionals(1, 1);
                if (args.Has("elevate"))
                {
                    throw new ArgException("--elevate belongs to enable|disable");
                }

                return await ListAsync(context, args).ConfigureAwait(false);
            case "enable" or "disable":
                if (args.Positionals.Count != 2)
                {
                    throw new ArgException("missing startup item ID");
                }

                if (args.Has("json"))
                {
                    throw new ArgException("--json belongs to `startup list`");
                }

                return await ChangeAsync(context, args, verb == "enable", args.Positionals[1]).ConfigureAwait(false);
            default:
                throw new ArgException("the action must be one of list|enable|disable");
        }
    }

    private static async Task<ExitCode> ListAsync(CliContext context, ParsedArgs args)
    {
        var items = await context.Services().Startup.ListAsync(context.Ct).ConfigureAwait(false);
        if (args.Has("json"))
        {
            context.Out.WriteLine(PcMieruJson.Serialize<IReadOnlyList<StartupItem>>(items));
            return ExitCode.Success;
        }

        context.Out.Write(CliTable.Render(
            [
                context.Text("cli.col.id"),
                context.Text("cli.col.state"),
                context.Text("cli.col.command"),
                context.Text("cli.col.note"),
            ],
            items.Select(i => (IReadOnlyList<string>)
            [
                i.Id,
                StateText(context, i.State),
                i.Command,
                NoteText(context, i),
            ]).ToList(),
            flexColumn: 2,
            consoleWidth: context.ConsoleWidth));
        context.Out.WriteLine(context.Format("cli.startup.footer", ("shown", items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return ExitCode.Success;
    }

    private static async Task<ExitCode> ChangeAsync(CliContext context, ParsedArgs args, bool enable, string id)
    {
        var manager = context.Services().Startup;
        var items = await manager.ListAsync(context.Ct).ConfigureAwait(false);
        var item = items.FirstOrDefault(i => i.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            context.Err.WriteLine("pcmi startup: " + context.Text("cli.startup.notFound"));
            return ExitCode.NotFound;
        }

        var label = CliText.Printable(item.Id);
        if (!item.CanToggle)
        {
            context.Out.WriteLine(label + ": " + context.Text("cli.startup.listOnly"));
            return ExitCode.Failure;
        }

        var wanted = enable ? StartupState.Enabled : StartupState.Disabled;
        if (item.State == wanted)
        {
            context.Out.WriteLine(label + ": " + context.Text(enable ? "cli.startup.already.enabled" : "cli.startup.already.disabled"));
            return ExitCode.Success;
        }

        OperationResult result;
        if (item.NeedsHelper)
        {
            var provider = item.Provider;
            var valueName = item.Name;
            result = await HelperElevation.WithElevationAsync(
                context,
                args,
                () => manager.SetEnabledAsync(item.Id, enable, context.Ct),
                (nonce, path) => new HelperRequest(HelperOperation.StartupHklmSet, nonce, path) { Provider = provider, ValueName = valueName, Enable = enable })
                .ConfigureAwait(false);
        }
        else
        {
            // A per-user item never needs administrator rights, so a refusal is reported as it is instead of raising a prompt.
            result = await manager.SetEnabledAsync(item.Id, enable, context.Ct).ConfigureAwait(false);
        }

        context.Out.WriteLine(label + ": " + (result.IsSuccess ? context.Text(enable ? "cli.startup.enabled" : "cli.startup.disabled") : OperationText.Line(context, result)));
        return ExitCodes.From(result.Code);
    }

    private static string StateText(CliContext context, StartupState state) =>
        context.Text("cli.startup.state." + char.ToLowerInvariant(state.ToString()[0]) + state.ToString()[1..]);

    private static string NoteText(CliContext context, StartupItem item)
    {
        if (!item.CanToggle)
        {
            return context.Text("cli.startup.note.listOnly");
        }

        return item.NeedsHelper ? context.Text("cli.startup.note.needsAdmin") : string.Empty;
    }
}
