namespace PcMieru.Cli.Commands;

/// <summary>Owner D.</summary>
public sealed class ServicesCommand() : StubCommand(
    "services",
    "pcmi services list [--running] [--json] | start|stop|restart <name>",
    "サービスの一覧と開始・停止",
    "List services and start or stop them",
    new("running"),
    new("json"));
