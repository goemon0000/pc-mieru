using System.Text;
using PcMieru.Core;

namespace PcMieru.Cli.Commands;

/// <summary>
/// Starts a new task ("Run new task"). Everything after the name is the command line; put <c>--</c> before it when the
/// program has options of its own. <c>--admin</c> asks Windows for an administrator prompt (the caller's own, never the Helper).
/// </summary>
public sealed class RunCommand : ICliCommand
{
    public string Name => "run";

    public string Usage => "pcmi run [--admin] [--] <command line>";

    public IReadOnlyList<OptionSpec> Options { get; } = [new("admin")];

    public IReadOnlyList<StringEntry> Texts { get; } =
    [
        new("cli.help.run", "新しいタスクを実行する", "Run a new task"),
        new("cli.run.started", "起動しました: {command}", "Started: {command}"),
        new("cli.run.startedAdmin", "管理者として起動しました: {command}", "Started as administrator: {command}"),
    ];

    public Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        args.RequirePositionals(1, int.MaxValue);
        var commandLine = BuildCommandLine(args.Positionals);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgException("the command line is empty");
        }

        var admin = args.Has("admin");
        if (admin)
        {
            context.Err.WriteLine("pcmi: " + context.Text("cli.elevate.prompt"));
        }

        var result = context.Services().Tasks.Run(commandLine, admin);
        if (!result.IsSuccess)
        {
            context.Out.WriteLine(OperationText.Line(context, result));
            return Task.FromResult(ExitCodes.From(result.Code));
        }

        context.Out.WriteLine(context.Format(admin ? "cli.run.startedAdmin" : "cli.run.started", ("command", commandLine)));
        return Task.FromResult(ExitCode.Success);
    }

    /// <summary>One argument is the command line as typed. Several are joined, quoting the ones that have a space or a quote.</summary>
    internal static string BuildCommandLine(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0].Trim() : string.Join(' ', parts.Select(Quote));

    private static string Quote(string part)
    {
        if (part.Length > 0 && !part.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return part;
        }

        // CommandLineToArgvW rules: a backslash run before a quote is doubled, and the quote itself is escaped.
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in part)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
