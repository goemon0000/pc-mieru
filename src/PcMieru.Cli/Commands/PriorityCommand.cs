namespace PcMieru.Cli.Commands;

/// <summary>Owner D. realtime is deliberately not offered.</summary>
public sealed class PriorityCommand() : StubCommand(
    "priority",
    "pcmi priority --pid N <idle|below|normal|above|high>",
    "優先度を変える（リアルタイムは出さない）",
    "Change the priority (realtime is not offered)",
    new OptionSpec("pid", TakesValue: true));
