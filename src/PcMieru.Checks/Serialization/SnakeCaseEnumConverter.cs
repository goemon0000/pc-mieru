using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Checks;

/// <summary>
/// The JSON names of an enum: the member name in lowercase snake_case, computed once ("EnergyMeter" -> "energy_meter",
/// "Win32" -> "win32", "DisabledCode22" -> "disabled_code22"). Used by the converter and by the hand-written
/// pcm-check/1 reader and writer, so the two can never disagree.
/// </summary>
internal static class SnakeCaseEnum<T> where T : struct, Enum
{
    private static readonly (Dictionary<T, string> Names, Dictionary<string, T> Values) Tables = Build();

    private static (Dictionary<T, string>, Dictionary<string, T>) Build()
    {
        var names = new Dictionary<T, string>();
        var values = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<T>())
        {
            var name = ToSnake(Enum.GetName(value) ?? throw new InvalidOperationException($"{typeof(T).Name} has an unnamed member."));
            names.Add(value, name);
            values.Add(name, value);
        }

        return (names, values);
    }

    internal static string ToSnake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                // A word boundary is an upper-case letter after a lower-case letter; digits do not start a word.
                if (i > 0 && char.IsLower(pascal[i - 1]))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string Name(T value) =>
        Tables.Names.TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a member of {typeof(T).Name}.");

    public static bool TryParse(string? text, out T value)
    {
        if (text is not null && Tables.Values.TryGetValue(text, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>All JSON names, in declaration order of the enum.</summary>
    public static IReadOnlyList<string> Names => Tables.Names.Values.ToArray();
}

/// <summary>Lowercase snake_case string enums, AOT-safe (put [JsonConverter] on each enum). Unknown strings and numbers are rejected.</summary>
public sealed class SnakeCaseEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(T).Name} must be a string.");
        }

        var text = reader.GetString();
        return SnakeCaseEnum<T>.TryParse(text, out var value)
            ? value
            : throw new JsonException($"Unknown {typeof(T).Name} '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SnakeCaseEnum<T>.Name(value));
}
