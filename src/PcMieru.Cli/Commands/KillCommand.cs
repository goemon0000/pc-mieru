namespace PcMieru.Cli.Commands;

/// <summary>Owner D. Must go through IProtectionPolicy and the ProcessKey (pid plus start time), never a bare PID.</summary>
public sealed class KillCommand() : StubCommand(
    "kill",
    "pcmi kill --pid N [--started-at T] [--tree] | --name X [--all]",
    "プロセスを終了する（保護されたものは終了しない）",
    "Terminate a process (protected ones are refused)",
    new("pid", TakesValue: true),
    new("started-at", TakesValue: true),
    new("tree"),
    new("name", TakesValue: true),
    new("all"));
