using System.Reflection;
using PcMieru.Checks;
using PcMieru.Checks.Build;
using PcMieru.Core;

namespace PcMieru.Cli;

/// <summary>
/// One subcommand = one class in Commands/ (discovered by reflection, so adding a command touches no shared file).
/// Rules: data on <c>Out</c> only, diagnostics on <c>Err</c>, a bad command line throws <see cref="ArgException"/>.
/// The one-line summary shown by `pcmi --help` is the string "cli.help.&lt;Name&gt;", supplied through <see cref="Texts"/>.
/// </summary>
public interface ICliCommand
{
    string Name { get; }

    /// <summary>Syntax line, language neutral, shown by `pcmi &lt;name&gt; --help`.</summary>
    string Usage { get; }

    IReadOnlyList<OptionSpec> Options { get; }

    IReadOnlyList<StringEntry> Texts => [];

    Task<ExitCode> RunAsync(CliContext context, ParsedArgs args);
}

/// <summary>
/// Where the checks live and who runs them. Every part has a live default, made only when a command asks for it (building this record, or
/// the context, reads and creates nothing); a test gives a temporary folder and a fake runner, so nothing of the real machine is touched.
/// </summary>
/// <param name="DataRoot">
/// The data folder the checks folder is under (<c>&lt;DataRoot&gt;\checks</c>), as the app's window takes it. Null: the default folder
/// (<see cref="CheckHistoryStore.DefaultRoot"/>), and then the default legacy folder too.
/// </param>
/// <param name="LegacyDirectory">
/// The checks folder of v0.7.4, which the baseline only reads, once. With a given <paramref name="DataRoot"/> null means none (a test never
/// reads the real one); with the default data folder null means the library's own default.
/// </param>
/// <param name="RunnerFactory">Makes the runner of the check commands. Null: the live runner, built on the first check.</param>
public sealed record CliChecks(string? DataRoot = null, string? LegacyDirectory = null, Func<CliContext, ICheckRunner>? RunnerFactory = null);

/// <summary>Everything outside the process that the CLI touches, so tests can run it on in-memory streams.</summary>
/// <param name="Checks">Where the checks are kept and who runs them. Null: the live ones.</param>
/// <param name="BuildCodeProvider">The maker of the configuration code (PCB1) of <c>pcmi parts --code</c>. Null: the one that reads this PC.</param>
/// <param name="ErrorIsTerminal">True when stderr is a terminal: only then does a running check say what it is doing there.</param>
public sealed record CliEnvironment(
    TextReader In,
    TextWriter Out,
    TextWriter Err,
    Func<PcMieruOptions, PcMieruServices> ServicesFactory,
    int ConsoleWidth = 120,
    Language? Language = null,
    string? HelperDirectory = null,
    CliChecks? Checks = null,
    IBuildCodeProvider? BuildCodeProvider = null,
    bool ErrorIsTerminal = false)
{
    public static Func<PcMieruOptions, PcMieruServices> DefaultFactory { get; } = static o => PcMieruServices.Create(o);
}

public sealed class CliContext : IDisposable
{
    private readonly CliEnvironment _env;
    private readonly Dictionary<bool, PcMieruServices> _services = [];

    public CliContext(CliEnvironment env, Language language, CancellationToken ct)
    {
        _env = env;
        Language = language;
        Ct = ct;
    }

    public TextReader In => _env.In;

    public TextWriter Out => _env.Out;

    public TextWriter Err => _env.Err;

    public int ConsoleWidth => _env.ConsoleWidth;

    /// <summary>Where the Helper's result file is made (SPEC section 8). Tests point it at a temporary folder.</summary>
    public string HelperDirectory =>
        _env.HelperDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMieru", "helper");

    public Language Language { get; }

    public CancellationToken Ct { get; }

    /// <summary>True when stderr is a terminal (the only place a running check shows its progress).</summary>
    public bool ErrorIsTerminal => _env.ErrorIsTerminal;

    /// <summary>The checks folder (where the results are kept). Only computes the path; nothing is read or created.</summary>
    public string ChecksFolder => _env.Checks?.DataRoot is { } dataRoot ? Path.Combine(dataRoot, "checks") : CheckHistoryStore.DefaultRoot();

    /// <summary>
    /// The history, opened for reading only: opening reads and creates nothing, and a folder that is not there is an empty history. This is
    /// the only way the CLI hands a history to anything that does not save (the MCP server gets nothing else).
    /// </summary>
    public IReadOnlyCheckHistory OpenHistory() => CheckHistory.OpenReadOnly(ChecksFolder);

    /// <summary>The maker of the configuration code: the one a test gave, else the one that reads this PC (made now; making it reads nothing).</summary>
    public IBuildCodeProvider BuildCodeProvider => _env.BuildCodeProvider ?? BuildCodeProviders.Windows();

    /// <summary>
    /// The runner of the check commands, made on the first call (the real one reads the machine only when a check runs, and a check is what
    /// writes its baseline and its result). The real services are made as the app's window makes them (DESIGN-A 2).
    /// </summary>
    public ICheckRunner CreateCheckRunner()
    {
        if (_env.Checks?.RunnerFactory is { } factory)
        {
            return factory(this);
        }

        var given = _env.Checks;
        var checksFolder = ChecksFolder;
        var root = given?.DataRoot ?? Path.GetDirectoryName(checksFolder)!;
        var legacy = given?.DataRoot is null ? given?.LegacyDirectory ?? CheckHistoryStore.DefaultLegacyDirectory() : given.LegacyDirectory;
        var checkServices = new CheckServices(TimeProvider.System, root, Services().StorageInspection, legacy, new CheckHistoryStore(checksFolder), app: null);
        return new CheckRunner(checkServices);
    }

    /// <summary>Created on first use. Says on stderr when any area still serves sample data.</summary>
    public PcMieruServices Services(bool redact = true)
    {
        if (_services.TryGetValue(redact, out var existing))
        {
            return existing;
        }

        var services = _env.ServicesFactory(new PcMieruOptions { Redact = redact });
        _services[redact] = services;
        if (services.UsesFakeData)
        {
            Err.WriteLine("pcmi: " + Strings.Get("core.fake.notice", Language));
        }

        return services;
    }

    public string Text(string key) => Strings.Get(key, Language);

    /// <summary>Fills {name} placeholders. Values are made printable, so a process name cannot carry a control character.</summary>
    public string Format(string key, params (string Name, string Value)[] values) =>
        Strings.Format(key, values.ToDictionary(v => v.Name, v => CliText.Printable(v.Value)), Language);

    public void Dispose()
    {
        foreach (var services in _services.Values)
        {
            services.Dispose();
        }
    }
}

public static class CliApp
{
    public static IReadOnlyList<ICliCommand> Commands { get; } = Discover();

    private static List<ICliCommand> Discover()
    {
        var commands = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ICliCommand).IsAssignableFrom(t))
            .Select(t => (ICliCommand)Activator.CreateInstance(t)!)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        Strings.Register(CliStrings.All);
        foreach (var command in commands)
        {
            Strings.Register(command.Texts);
        }

        return commands;
    }

    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken ct = default)
    {
        var commands = Commands;
        var language = env.Language ?? Strings.DefaultFromCulture();
        List<string> rest;
        try
        {
            (language, rest) = ExtractLanguage(args, language);
        }
        catch (ArgException ex)
        {
            env.Err.WriteLine("pcmi: " + ex.Message);
            return (int)ExitCode.BadArguments;
        }

        Strings.Current = language;
        if (rest.Count == 0)
        {
            env.Err.WriteLine(Help(language));
            return (int)ExitCode.BadArguments;
        }

        if (rest[0] is "-h" or "--help" or "help")
        {
            env.Out.WriteLine(Help(language));
            return (int)ExitCode.Success;
        }

        if (rest[0] is "-V" or "--version")
        {
            rest[0] = "version";
        }

        var command = commands.FirstOrDefault(c => c.Name.Equals(rest[0], StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            env.Err.WriteLine($"pcmi: {Strings.Get("cli.unknownCommand", language)} '{CliText.Printable(rest[0])}'");
            env.Err.WriteLine(Strings.Get("cli.tryHelp", language));
            return (int)ExitCode.BadArguments;
        }

        var commandArgs = rest.Skip(1).ToArray();
        if (commandArgs.Any(a => a is "-h" or "--help"))
        {
            env.Out.WriteLine(command.Usage);
            return (int)ExitCode.Success;
        }

        using var context = new CliContext(env, language, ct);
        try
        {
            var parsed = ParsedArgs.Parse(commandArgs, command.Options);
            return (int)await command.RunAsync(context, parsed).ConfigureAwait(false);
        }
        catch (ArgException ex)
        {
            env.Err.WriteLine($"pcmi {command.Name}: {ex.Message}");
            env.Err.WriteLine(command.Usage);
            return (int)ExitCode.BadArguments;
        }
        catch (OperationCanceledException)
        {
            return (int)ExitCode.Failure;
        }
        catch (UnauthorizedAccessException ex)
        {
            env.Err.WriteLine($"pcmi {command.Name}: {CliText.Printable(ex.Message)}");
            return (int)ExitCode.Denied;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            env.Err.WriteLine($"pcmi {command.Name}: {CliText.Printable(ex.Message)}");
            return (int)ExitCode.Failure;
        }
    }

    /// <summary>--lang ja|en is accepted anywhere before a literal "--" and removed from the arguments.</summary>
    private static (Language Language, List<string> Remaining) ExtractLanguage(string[] args, Language language)
    {
        var rest = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i] == "--")
            {
                rest.AddRange(args.Skip(i));
                break;
            }

            if (args[i] == "--lang")
            {
                value = i + 1 < args.Length ? args[++i] : throw new ArgException("option '--lang' needs a value");
            }
            else if (args[i].StartsWith("--lang=", StringComparison.Ordinal))
            {
                value = args[i]["--lang=".Length..];
            }
            else
            {
                rest.Add(args[i]);
                continue;
            }

            language = value.ToLowerInvariant() switch
            {
                "ja" => Language.Ja,
                "en" => Language.En,
                _ => throw new ArgException("option '--lang' must be ja or en"),
            };
        }

        return (language, rest);
    }

    public static string Help(Language language)
    {
        var width = Commands.Max(c => c.Name.Length) + 2;
        var lines = new List<string>
        {
            "pcmi - " + Strings.Get("core.app.name", language),
            Strings.Get("cli.usage", language),
            string.Empty,
        };
        lines.AddRange(Commands.Select(c => "  " + c.Name.PadRight(width) + Strings.Get("cli.help." + c.Name, language)));
        lines.Add(string.Empty);
        lines.Add(Strings.Get("cli.help.globals", language));
        return string.Join('\n', lines);
    }
}
