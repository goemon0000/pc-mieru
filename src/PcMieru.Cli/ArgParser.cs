using System.Globalization;

namespace PcMieru.Cli;

/// <summary>A bad command line. The message goes to stderr and the exit code is BadArguments (2).</summary>
public sealed class ArgException(string message) : Exception(message);

/// <param name="Name">Long name without the dashes.</param>
/// <param name="Short">Optional one-letter alias (-o).</param>
/// <param name="TakesValue">False = a flag.</param>
public sealed record OptionSpec(string Name, char Short = '\0', bool TakesValue = false);

public sealed class ParsedArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    public IReadOnlyList<string> Positionals => _positionals;

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public static ParsedArgs Parse(IReadOnlyList<string> args, IReadOnlyList<OptionSpec> specs)
    {
        var parsed = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                parsed._positionals.AddRange(args.Skip(i + 1));
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                var name = eq < 0 ? arg[2..] : arg[2..eq];
                var spec = specs.FirstOrDefault(s => s.Name == name) ?? throw new ArgException($"unknown option '--{name}'");
                i = parsed.Store(spec, eq < 0 ? null : arg[(eq + 1)..], args, i);
            }
            else if (arg.Length == 2 && arg[0] == '-' && arg[1] != '-')
            {
                var spec = specs.FirstOrDefault(s => s.Short == arg[1]) ?? throw new ArgException($"unknown option '{arg}'");
                i = parsed.Store(spec, null, args, i);
            }
            else
            {
                parsed._positionals.Add(arg);
            }
        }

        return parsed;
    }

    private int Store(OptionSpec spec, string? inline, IReadOnlyList<string> args, int index)
    {
        if (!spec.TakesValue)
        {
            _options[spec.Name] = inline is null ? null : throw new ArgException($"option '--{spec.Name}' does not take a value");
            return index;
        }

        if (inline is not null)
        {
            _options[spec.Name] = inline;
            return index;
        }

        if (index + 1 >= args.Count)
        {
            throw new ArgException($"option '--{spec.Name}' needs a value");
        }

        _options[spec.Name] = args[index + 1];
        return index + 1;
    }

    public void RequirePositionals(int min, int max)
    {
        if (_positionals.Count < min)
        {
            throw new ArgException("missing argument");
        }

        if (_positionals.Count > max)
        {
            throw new ArgException($"unexpected argument '{_positionals[max]}'");
        }
    }

    public int? GetInt(string name, int min, int max)
    {
        var text = Get(name);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new ArgException($"option '--{name}' must be a whole number from {min} to {max}");
        }

        return value;
    }

    /// <summary>"500ms", "1s", "2m" or a bare number of seconds ("1.5").</summary>
    public TimeSpan? GetDuration(string name, TimeSpan min, TimeSpan max)
    {
        var text = Get(name);
        if (text is null)
        {
            return null;
        }

        var value = ParseDuration(text);
        if (value is null || value < min || value > max)
        {
            throw new ArgException($"option '--{name}' must be a duration like 500ms, 1s or 2m between {min.TotalSeconds:0.###}s and {max.TotalSeconds:0.###}s");
        }

        return value;
    }

    public static TimeSpan? ParseDuration(string text)
    {
        var unit = 1000.0;
        var number = text;
        if (text.EndsWith("ms", StringComparison.Ordinal))
        {
            unit = 1;
            number = text[..^2];
        }
        else if (text.EndsWith('s'))
        {
            number = text[..^1];
        }
        else if (text.EndsWith('m'))
        {
            unit = 60_000;
            number = text[..^1];
        }

        return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value >= 0 && double.IsFinite(value)
            ? TimeSpan.FromMilliseconds(value * unit)
            : null;
    }

    public T? GetEnum<T>(string name)
        where T : struct, Enum
    {
        var text = Get(name);
        return text is null ? null : ParseEnum<T>(text, $"option '--{name}'");
    }

    public static T ParseEnum<T>(string text, string what)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        var names = string.Join('|', Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));
        throw new ArgException($"{what} must be one of {names}");
    }
}
