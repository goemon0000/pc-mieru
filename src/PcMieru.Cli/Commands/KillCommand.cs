using System.Globalization;
using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli.Commands;

/// <summary>
/// Terminates processes (SPEC sections 8 and 9). The targets are shown first, every one goes through the protection
/// policy (an unknown answer counts as protected), and the action is on the ProcessKey, never a bare PID.
/// </summary>
public sealed class KillCommand : ICliCommand
{
    public string Name => "kill";

    public string Usage =>
        "pcmi kill --pid N [--started-at T] [--tree] [--dry-run] [--elevate] | --name X [--all] [--tree] [--dry-run]";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        new("pid", TakesValue: true),
        new("started-at", TakesValue: true),
        new("tree"),
        new("name", TakesValue: true),
        new("all"),
        new("dry-run"),
        new("elevate"),
    ];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.kill", "プロセスを終了する（保護されたものは終了しない）", "Terminate a process (protected ones are refused)"),
        new("cli.kill.targets", "終了する対象:", "Will terminate:"),
        new("cli.kill.treeTargets", "ツリーごと終了する対象（いまの子孫だけ。あとから生まれた子は追いません）:", "Will terminate the tree (current descendants only; later children are not followed):"),
        new("cli.kill.skippedProtected", "保護されているため残すもの: {list}", "Left alone because they are protected: {list}"),
        new("cli.kill.dryRun", "--dry-run のため、何も終了していません", "--dry-run: nothing was terminated"),
        new("cli.kill.ok", "終了しました", "Terminated"),
        new("cli.kill.treeOk", "ツリーごと終了しました（{count} 個）", "Terminated the tree ({count} processes)"),
        new("cli.kill.multiple", "「{name}」に一致するプロセスが {count} 個あります。何も終了していません。全部終了するなら --all を付けてください（--dry-run なら対象を見るだけです）", "{count} processes match '{name}'. Nothing was terminated. Add --all to terminate all of them (--dry-run only lists them)"),
        new("cli.kill.summary", "{ok} 件を終了、{protected} 件を保護のため残し、{failed} 件は失敗", "{ok} terminated, {protected} left alone (protected), {failed} failed"),
        new("cli.notFound.name", "その名前のプロセスはありません", "No process with that name"),
    ];

    public async Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(0, 0);
        var byPid = args.Has("pid");
        var byName = args.Has("name");
        if (byPid == byName)
        {
            throw new ArgException("give exactly one of --pid or --name");
        }

        if (args.Has("started-at") && !byPid)
        {
            throw new ArgException("--started-at goes with --pid");
        }

        if (args.Has("all") && !byName)
        {
            throw new ArgException("--all goes with --name");
        }

        if (args.Has("elevate") && (!byPid || args.Has("tree")))
        {
            throw new ArgException("--elevate works with --pid only, without --tree (each process needs its own administrator prompt)");
        }

        var pid = byPid ? ProcessTargets.ParsePid(args) : 0;
        var startedAt = args.Get("started-at") is { } text ? StartedAt.Parse(text) : (StartedAt?)null;
        var name = args.Get("name");
        if (byName && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgException("option '--name' needs a process name");
        }

        var services = context.Services();
        var processes = await ProcessTargets.ListAsync(context).ConfigureAwait(false);
        List<ProcessInfo> matches;
        if (byPid)
        {
            var resolved = ProcessTargets.ResolvePid(context, "kill", processes, pid, startedAt);
            if (resolved.Process is null)
            {
                return resolved.Exit;
            }

            matches = [resolved.Process];
        }
        else
        {
            matches = MatchName(processes, name!.Trim());
            if (matches.Count == 0)
            {
                context.Err.WriteLine("pcmi kill: " + context.Text("cli.notFound.name"));
                return ExitCode.NotFound;
            }
        }

        var targets = matches.Select(p => (Process: p, Verdict: services.Protection.Evaluate(p.Key))).ToList();
        if (byName && targets.Count > 1 && !args.Has("all"))
        {
            context.Out.Write(ProcessTargets.Table(context, targets));
            context.Err.WriteLine("pcmi kill: " + context.Format("cli.kill.multiple", ("name", name!), ("count", targets.Count.ToString(CultureInfo.InvariantCulture))));
            return ExitCode.MultipleMatches;
        }

        return args.Has("tree")
            ? await KillTreesAsync(context, args, processes, targets).ConfigureAwait(false)
            : await KillEachAsync(context, args, targets).ConfigureAwait(false);
    }

    /// <summary>The image name, ignoring case. "chrome" also finds "chrome.exe". Never a substring: a kill must not hit "rchrome".</summary>
    private static List<ProcessInfo> MatchName(IReadOnlyList<ProcessInfo> processes, string name)
    {
        var withExe = name.Contains('.', StringComparison.Ordinal) ? name : name + ".exe";
        return processes
            .Where(p => p.Key.Pid != 0 && (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(withExe, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Key.Pid)
            .ToList();
    }

    private static async Task<ExitCode> KillEachAsync(CliContext context, ParsedArgs args, List<(ProcessInfo Process, ProtectionVerdict Verdict)> targets)
    {
        var control = context.Services().ProcessControl;
        context.Out.WriteLine(context.Text("cli.kill.targets"));
        context.Out.Write(ProcessTargets.Table(context, targets));
        if (args.Has("dry-run"))
        {
            context.Out.WriteLine(context.Text("cli.kill.dryRun"));
            return targets.All(t => !t.Verdict.MayOperate) ? ExitCode.Protected : ExitCode.Success;
        }

        var codes = new List<OperationCode>();
        foreach (var (process, verdict) in targets)
        {
            OperationResult result;
            if (!verdict.MayOperate)
            {
                // Not even asked: a protected or unconfirmed process is never offered to the terminate call.
                result = OperationResult.Fail(OperationCode.Protected, string.Empty);
            }
            else
            {
                var key = process.Key;
                result = await HelperElevation.WithElevationAsync(
                    context,
                    args,
                    () => control.TerminateAsync(key, context.Ct),
                    (nonce, path) => new HelperRequest(HelperOperation.ProcessTerminate, nonce, path) { Pid = key.Pid, CreateTime = key.CreateTime })
                    .ConfigureAwait(false);
            }

            codes.Add(result.Code);
            context.Out.WriteLine(ProcessTargets.Label(process) + ": " + (result.IsSuccess ? context.Text("cli.kill.ok") : OperationText.Line(context, result)));
        }

        if (targets.Count > 1)
        {
            context.Out.WriteLine(context.Format(
                "cli.kill.summary",
                ("ok", codes.Count(c => c == OperationCode.Ok).ToString(CultureInfo.InvariantCulture)),
                ("protected", codes.Count(c => c == OperationCode.Protected).ToString(CultureInfo.InvariantCulture)),
                ("failed", codes.Count(c => c is not (OperationCode.Ok or OperationCode.Protected)).ToString(CultureInfo.InvariantCulture))));
        }

        return OperationText.Combine(codes);
    }

    private static async Task<ExitCode> KillTreesAsync(
        CliContext context,
        ParsedArgs args,
        IReadOnlyList<ProcessInfo> processes,
        List<(ProcessInfo Process, ProtectionVerdict Verdict)> targets)
    {
        var control = context.Services().ProcessControl;
        var byKey = processes.ToDictionary(p => p.Key);
        var matched = targets.Select(t => t.Process.Key).ToHashSet();
        var codes = new List<OperationCode>();
        foreach (var (root, verdict) in targets)
        {
            // A match inside another match's tree is ended with that tree; planning it again would only fail on a vanished PID.
            if (HasMatchedAncestor(root, byKey, matched))
            {
                continue;
            }

            if (!verdict.MayOperate)
            {
                context.Out.WriteLine(ProcessTargets.Label(root) + ": " + OperationText.Describe(context, OperationCode.Protected) + " (" + ProcessTargets.ProtectionText(context, verdict) + ")");
                codes.Add(OperationCode.Protected);
                continue;
            }

            var plan = await control.PlanTerminateTreeAsync(root.Key, context.Ct).ConfigureAwait(false);
            context.Out.WriteLine(context.Text("cli.kill.treeTargets"));
            context.Out.Write(ProcessTargets.Table(context, plan.Targets.Select(k => (Process: Describe(byKey, k), Verdict: new ProtectionVerdict(ProtectionKind.None, null))).ToList()));
            if (plan.SkippedProtected.Count > 0)
            {
                context.Out.WriteLine(context.Format("cli.kill.skippedProtected", ("list", string.Join(", ", plan.SkippedProtected.Select(k => ProcessTargets.Label(Describe(byKey, k)))))));
            }

            if (args.Has("dry-run"))
            {
                context.Out.WriteLine(context.Text("cli.kill.dryRun"));
                codes.Add(OperationCode.Ok);
                continue;
            }

            var result = await control.TerminateTreeAsync(plan, context.Ct).ConfigureAwait(false);
            codes.Add(result.Code);
            context.Out.WriteLine(
                ProcessTargets.Label(root) + ": "
                + (result.IsSuccess
                    ? context.Format("cli.kill.treeOk", ("count", plan.Targets.Count.ToString(CultureInfo.InvariantCulture)))
                    : OperationText.Line(context, result)));
            foreach (var detail in result.Details ?? [])
            {
                if (detail.Code != OperationCode.Ok)
                {
                    context.Out.WriteLine("  " + CliText.Printable(detail.Target) + ": " + OperationText.Describe(context, detail.Code) + (string.IsNullOrWhiteSpace(detail.Message) ? string.Empty : " (" + CliText.Printable(detail.Message.Trim()) + ")"));
                }
            }
        }

        return OperationText.Combine(codes);
    }

    private static ProcessInfo Describe(Dictionary<ProcessKey, ProcessInfo> byKey, ProcessKey key) =>
        byKey.TryGetValue(key, out var found) ? found : ProcessTargets.Placeholder(key);

    private static bool HasMatchedAncestor(ProcessInfo process, Dictionary<ProcessKey, ProcessInfo> byKey, HashSet<ProcessKey> matched)
    {
        var seen = new HashSet<ProcessKey> { process.Key };
        var parent = process.ParentKey;
        while (parent is { } key && seen.Add(key))
        {
            if (matched.Contains(key))
            {
                return true;
            }

            parent = byKey.TryGetValue(key, out var up) ? up.ParentKey : null;
        }

        return false;
    }
}
