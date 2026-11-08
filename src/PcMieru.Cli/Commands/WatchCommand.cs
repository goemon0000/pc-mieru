namespace PcMieru.Cli.Commands;

/// <summary>Owner E.</summary>
public sealed class WatchCommand() : StubCommand(
    "watch",
    "pcmi watch [--interval 1s] [--top N] [--ndjson]",
    "上位プロセスを間隔ごとに流し続ける",
    "Stream the top processes every interval",
    new("interval", TakesValue: true),
    new("top", TakesValue: true),
    new("ndjson"));
