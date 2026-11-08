using System.Globalization;
using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli.Commands;

/// <summary>
/// Lists Windows services and starts, stops or restarts one. A stop (or restart) shows the services that depend on it and
/// the state it was in, and needs <c>--yes</c> when something running depends on it.
/// </summary>
public sealed class ServicesCommand : ICliCommand
{
    public string Name => "services";

    public string Usage => "pcmi services list [--running] [--json] | start|stop|restart <name> [--yes] [--elevate]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("running"),
        new("json"),
        new("yes"),
        new("elevate"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.services", "サービスの一覧と開始・停止", "List services and start or stop them"),
        new("cli.col.service", "サービス名", "Service"),
        new("cli.col.display", "表示名", "Display name"),
        new("cli.col.startType", "開始の種類", "Start type"),
        new("cli.services.footer", "{shown} 件", "{shown} services"),
        new("cli.services.notFound", "その名前のサービスはありません", "No service with that name"),
        new("cli.services.before", "いまの状態: {state}／開始の種類: {startType}", "Current state: {state} / start type: {startType}"),
        new("cli.services.dependents", "このサービスに依存して動いているもの: {list}", "Running services that depend on this one: {list}"),
        new("cli.services.needsYes", "止めると上のサービスも止まります。進めるなら --yes を付けて、もう一度実行してください。何も変更していません", "Stopping this also stops the services listed above. Run it again with --yes to go ahead. Nothing was changed"),
        new("cli.services.started", "開始しました", "Started"),
        new("cli.services.stopped", "停止しました", "Stopped"),
        new("cli.services.restarted", "再起動しました", "Restarted"),
        new("cli.services.state.stopped", "停止中", "stopped"),
        new("cli.services.state.startPending", "開始中", "starting"),
        new("cli.services.state.stopPending", "停止中（処理中）", "stopping"),
        new("cli.services.state.running", "実行中", "running"),
        new("cli.services.state.continuePending", "再開中", "resuming"),
        new("cli.services.state.pausePending", "一時停止中（処理中）", "pausing"),
        new("cli.services.state.paused", "一時停止", "paused"),
        new("cli.services.state.unknown", "不明", "unknown"),
        new("cli.services.start.automatic", "自動", "automatic"),
        new("cli.services.start.automaticDelayed", "自動（遅延）", "automatic (delayed)"),
        new("cli.services.start.manual", "手動", "manual"),
        new("cli.services.start.disabled", "無効", "disabled"),
        new("cli.services.start.boot", "ブート", "boot"),
        new("cli.services.start.system", "システム", "system"),
        new("cli.services.start.unknown", "不明", "unknown"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, 2);
        var verb = args.Positionals[0].ToLowerInvariant();
        switch (verb)
        {
            case "list":
                args.RequirePositionals(1, 1);
                RejectChangeOptions(args);
                return await ListAsync(context, args).ConfigureAwait(false);
            case "start" or "stop" or "restart":
                if (args.Positionals.Count != 2)
                {
                    throw new ArgException("missing service name");
                }

                if (args.Has("running") || args.Has("json"))
                {
                    throw new ArgException("--running and --json belong to `services list`");
                }

                return await ChangeAsync(context, args, verb, args.Positionals[1]).ConfigureAwait(false);
            default:
                throw new ArgException("the action must be one of list|start|stop|restart");
        }
    }

    private static void RejectChangeOptions(ParsedArgs args)
    {
        if (args.Has("yes") || args.Has("elevate"))
        {
            throw new ArgException("--yes and --elevate belong to start|stop|restart");
        }
    }

    private static async Task<ExitCode> ListAsync(CliContext context, ParsedArgs args)
    {
        var all = await context.Services().Services.ListAsync(context.Ct).ConfigureAwait(false);
        var shown = args.Has("running") ? all.Where(s => s.State == ServiceState.Running).ToList() : all.ToList();
        if (args.Has("json"))
        {
            context.Out.WriteLine(PcMieruJson.Serialize<IReadOnlyList<ServiceInfo>>(shown));
            return ExitCode.Success;
        }

        context.Out.Write(CliTable.Render(
            [
                context.Text("cli.col.service"),
                context.Text("cli.col.display"),
                context.Text("cli.col.state"),
                context.Text("cli.col.startType"),
                context.Text("cli.col.pid"),
            ],
            shown.Select(s => (IReadOnlyList<string>)
            [
                s.Name,
                s.DisplayName,
                StateText(context, s.State),
                StartTypeText(context, s.StartType),
                s.Pid?.ToString(CultureInfo.InvariantCulture) ?? context.Text("cli.value.notAvailable"),
            ]).ToList(),
            new HashSet<int> { 4 },
            flexColumn: 1,
            consoleWidth: context.ConsoleWidth));
        context.Out.WriteLine(context.Format("cli.services.footer", ("shown", shown.Count.ToString(CultureInfo.InvariantCulture))));
        return ExitCode.Success;
    }

    private static async Task<ExitCode> ChangeAsync(CliContext context, ParsedArgs args, string verb, string name)
    {
        var manager = context.Services().Services;
        var all = await manager.ListAsync(context.Ct).ConfigureAwait(false);
        var service = all.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            // Not found here means nothing is sent anywhere: the Helper only ever sees names that exist.
            context.Err.WriteLine("pcmi services: " + context.Text("cli.services.notFound"));
            return ExitCode.NotFound;
        }

        var serviceName = service.Name;
        if (verb != "start")
        {
            var running = RunningDependents(service, all);
            context.Out.WriteLine(serviceName + ": " + context.Format("cli.services.before", ("state", StateText(context, service.State)), ("startType", StartTypeText(context, service.StartType))));
            if (running.Count > 0)
            {
                context.Out.WriteLine(context.Format("cli.services.dependents", ("list", string.Join(", ", running))));
                if (!args.Has("yes"))
                {
                    context.Err.WriteLine("pcmi services: " + context.Text("cli.services.needsYes"));
                    return ExitCode.Failure;
                }
            }
        }

        Func<Task<OperationResult>> direct;
        HelperOperation helperOp;
        string doneKey;
        switch (verb)
        {
            case "start":
                direct = () => manager.StartAsync(serviceName, context.Ct);
                helperOp = HelperOperation.ServiceStart;
                doneKey = "cli.services.started";
                break;
            case "stop":
                direct = () => manager.StopAsync(serviceName, context.Ct);
                helperOp = HelperOperation.ServiceStop;
                doneKey = "cli.services.stopped";
                break;
            default:
                direct = () => manager.RestartAsync(serviceName, context.Ct);
                helperOp = HelperOperation.ServiceRestart;
                doneKey = "cli.services.restarted";
                break;
        }

        var result = await HelperElevation.WithElevationAsync(
            context,
            args,
            direct,
            (nonce, path) => new HelperRequest(helperOp, nonce, path) { ServiceName = serviceName })
            .ConfigureAwait(false);
        context.Out.WriteLine(CliText.Printable(serviceName) + ": " + (result.IsSuccess ? context.Text(doneKey) : OperationText.Line(context, result)));
        return ExitCodes.From(result.Code);
    }

    /// <summary>Services that depend on this one and are not already stopped. A dependent whose state is not known counts.</summary>
    private static List<string> RunningDependents(ServiceInfo service, IReadOnlyList<ServiceInfo> all)
    {
        var byName = all.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { service.Name };
        var queue = new Queue<string>(service.Dependents);
        var running = new List<string>();
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (!seen.Add(next))
            {
                continue;
            }

            if (byName.TryGetValue(next, out var dependent))
            {
                if (dependent.State == ServiceState.Stopped)
                {
                    continue;
                }

                running.Add(dependent.Name);
                foreach (var further in dependent.Dependents)
                {
                    queue.Enqueue(further);
                }
            }
            else
            {
                running.Add(next);
            }
        }

        return running;
    }

    private static string StateText(CliContext context, ServiceState state) =>
        context.Text("cli.services.state." + Camel(state.ToString()));

    private static string StartTypeText(CliContext context, ServiceStartType type) =>
        context.Text("cli.services.start." + Camel(type.ToString()));

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
