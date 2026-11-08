using PcMieru.Core;

namespace PcMieru.Cli.Commands;

/// <summary>
/// Placeholder for a command whose body belongs to a work area that has not landed yet. It already owns the final
/// name, syntax and help text, so the argument errors (exit 2) are right from day one. The owner replaces the
/// base class with ICliCommand and writes RunAsync; nothing else changes.
/// </summary>
public abstract class StubCommand(string name, string usage, string jaHelp, string enHelp, params OptionSpec[] options) : ICliCommand
{
    public string Name { get; } = name;

    public string Usage { get; } = usage;

    public IReadOnlyList<OptionSpec> Options { get; } = options;

    public IReadOnlyList<StringEntry> Texts { get; } = [new("cli.help." + name, jaHelp, enHelp)];

    public Task<ExitCode> RunAsync(CliContext context, ParsedArgs args)
    {
        context.Err.WriteLine($"pcmi {Name}: {context.Text("cli.notImplemented")}");
        return Task.FromResult(ExitCode.Failure);
    }
}
