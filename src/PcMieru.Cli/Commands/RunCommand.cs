namespace PcMieru.Cli.Commands;

/// <summary>Owner D. Everything after the name is the command line (use -- before options of the program itself).</summary>
public sealed class RunCommand() : StubCommand(
    "run",
    "pcmi run <command line>",
    "新しいタスクを実行する",
    "Run a new task");
