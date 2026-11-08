namespace PcMieru.Cli.Commands;

/// <summary>Owner D.</summary>
public sealed class StartupCommand() : StubCommand(
    "startup",
    "pcmi startup list|enable <id>|disable <id> [--json]",
    "スタートアップの一覧と有効・無効",
    "List startup items and enable or disable them",
    new OptionSpec("json"));
