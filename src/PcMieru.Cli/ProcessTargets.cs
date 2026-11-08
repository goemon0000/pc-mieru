using System.Globalization;
using PcMieru.Core;
using PcMieru.Wire;

namespace PcMieru.Cli;

/// <summary>How the user named the start time of a process: the FILETIME number, or a UTC date and time.</summary>
public readonly record struct StartedAt(long? FileTime, DateTime? Utc)
{
    public bool Matches(ProcessKey key)
    {
        if (FileTime is { } ft)
        {
            return key.CreateTime == ft;
        }

        // The date form has whole seconds only, so it names a one second window.
        return Utc is { } utc && key.CreateTime > 0 && Math.Abs((key.StartedAtUtc - utc).TotalSeconds) < 1;
    }

    public static StartedAt Parse(string text)
    {
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ft) && ft > 0)
        {
            return new StartedAt(ft, null);
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            return new StartedAt(null, utc);
        }

        throw new ArgException("option '--started-at' must be the creation time number from `pcmi ps --json` (the part after the colon) or a UTC time like 2026-11-08T12:00:05Z");
    }
}

/// <summary>Finding the process a command is about, the same way for kill, priority and eco (SPEC section 8).</summary>
public static class ProcessTargets
{
    public static string StartedText(ProcessKey key) =>
        key.CreateTime > 0 ? key.StartedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-";

    public static uint ParsePid(ParsedArgs args)
    {
        var text = args.Get("pid") ?? throw new ArgException("option '--pid' is required");
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : throw new ArgException("option '--pid' must be a whole number");
    }

    /// <summary>One observation is enough to know who is running: names and keys do not need a baseline.</summary>
    public static async Task<IReadOnlyList<ProcessInfo>> ListAsync(CliContext context)
    {
        var snapshot = await context.Services().Snapshots.SampleAsync(context.Ct).ConfigureAwait(false);
        return snapshot.Processes;
    }

    public sealed record Resolved(ProcessInfo? Process, ExitCode Exit);

    /// <summary>
    /// <c>--pid</c> with an optional <c>--started-at</c>. Without it the process that has the PID right now is the target, and
    /// the command says so. A different start time means the PID now belongs to another process: nothing is done.
    /// </summary>
    public static Resolved ResolvePid(CliContext context, string command, IReadOnlyList<ProcessInfo> processes, uint pid, StartedAt? startedAt)
    {
        var found = processes.FirstOrDefault(p => p.Key.Pid == pid);
        if (found is null)
        {
            context.Err.WriteLine($"pcmi {command}: " + context.Text("cli.notFound.pid"));
            return new Resolved(null, ExitCode.NotFound);
        }

        if (startedAt is { } wanted)
        {
            if (!wanted.Matches(found.Key))
            {
                context.Err.WriteLine($"pcmi {command}: " + context.Format("cli.target.changed", ("pid", pid.ToString(CultureInfo.InvariantCulture))));
                return new Resolved(null, ExitCode.NotFound);
            }
        }
        else
        {
            context.Err.WriteLine(
                $"pcmi {command}: "
                + context.Format("cli.target.noStartedAt", ("pid", pid.ToString(CultureInfo.InvariantCulture)), ("started", StartedText(found.Key))));
        }

        return new Resolved(found, ExitCode.Success);
    }

    /// <summary>The words printed for what the protection policy said. An unknown answer is never "allowed".</summary>
    public static string ProtectionText(CliContext context, ProtectionVerdict verdict)
    {
        switch (verdict.Kind)
        {
            case ProtectionKind.None:
                return "-";
            case ProtectionKind.Protected:
                return context.Format("cli.protection.protected", ("reason", Reason(context, verdict, "mgmt.protected.reason")));
            default:
                return context.Format("cli.protection.unknown", ("reason", Reason(context, verdict, "mgmt.protected.unknown")));
        }
    }

    private static string Reason(CliContext context, ProtectionVerdict verdict, string fallbackKey) =>
        string.IsNullOrWhiteSpace(verdict.Reason) ? context.Text(fallbackKey) : verdict.Reason.Trim();

    /// <summary>The table printed before anything is done, so the user sees exactly who is about to be touched.</summary>
    public static string Table(CliContext context, IReadOnlyList<(ProcessInfo Process, ProtectionVerdict Verdict)> targets) =>
        CliTable.Render(
            [
                context.Text("cli.col.pid"),
                context.Text("cli.col.started"),
                context.Text("cli.col.name"),
                context.Text("cli.col.mem"),
                context.Text("cli.col.protection"),
            ],
            targets.Select(t => (IReadOnlyList<string>)
            [
                t.Process.Key.Pid.ToString(CultureInfo.InvariantCulture),
                StartedText(t.Process.Key),
                t.Process.Name,
                CliText.Num(t.Process.PrivateWorkingSetBytes / 1024.0 / 1024.0),
                ProtectionText(context, t.Verdict),
            ]).ToList(),
            new HashSet<int> { 0, 3 },
            flexColumn: 2,
            consoleWidth: context.ConsoleWidth);

    public static string Label(ProcessInfo process) =>
        process.Key.Pid.ToString(CultureInfo.InvariantCulture) + " " + CliText.Printable(process.Name);

    /// <summary>A process that already left the list is still named by its PID.</summary>
    public static ProcessInfo Placeholder(ProcessKey key) =>
        new(
            key, "?", 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            Value.NotCollected<double>("cli"), Value.NotCollected<double>("cli"), Value.NotCollected<double>("cli"), Value.NotCollected<double>("cli"),
            Value.NotCollected<ProcessState>("cli"));

    public sealed record Chosen(ProcessInfo? Process, ProtectionVerdict Verdict, ExitCode Exit);

    /// <summary>
    /// The single process named by <c>--pid</c> (and <c>--started-at</c>), with the protection policy's answer. Used by the
    /// commands that change one process (priority, eco). A missing or changed target is already reported on stderr.
    /// </summary>
    public static async Task<Chosen> ChooseAsync(CliContext context, string command, ParsedArgs args)
    {
        var pid = ParsePid(args);
        var startedAt = args.Get("started-at") is { } text ? StartedAt.Parse(text) : (StartedAt?)null;
        var processes = await ListAsync(context).ConfigureAwait(false);
        var resolved = ResolvePid(context, command, processes, pid, startedAt);
        if (resolved.Process is null)
        {
            return new Chosen(null, new ProtectionVerdict(ProtectionKind.Unknown, null), resolved.Exit);
        }

        return new Chosen(resolved.Process, context.Services().Protection.Evaluate(resolved.Process.Key), ExitCode.Success);
    }

    /// <summary>The line printed when the policy says no. The operation is never offered to the control service.</summary>
    public static ExitCode Refuse(CliContext context, ProcessInfo process, ProtectionVerdict verdict)
    {
        context.Out.WriteLine(Label(process) + ": " + OperationText.Describe(context, OperationCode.Protected) + " (" + ProtectionText(context, verdict) + ")");
        return ExitCode.Protected;
    }
}
