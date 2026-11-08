namespace PcMieru.Cli.Commands;

/// <summary>Owner D.</summary>
public sealed class EcoCommand() : StubCommand(
    "eco",
    "pcmi eco --pid N on|off",
    "効率モードの切り替え",
    "Turn efficiency mode on or off",
    new OptionSpec("pid", TakesValue: true));
