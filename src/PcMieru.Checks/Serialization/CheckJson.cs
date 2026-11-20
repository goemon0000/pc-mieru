using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PcMieru.Checks;

/// <summary>
/// The entry points for pcm-check/1 JSON. The output is deterministic: the same result is always the same bytes
/// (fixed key order, "\n" line ends, the degree sign written as itself, no trailing newline; a file writer adds one).
/// Reading is strict and ends in <see cref="CheckResultValidator"/>; every failure is a <see cref="CheckFormatException"/>.
/// </summary>
public static class CheckJson
{
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = indented,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = CheckJsonContext.Default,
        };
        options.Converters.Add(new CheckUtcTimeJsonConverter());
        options.MakeReadOnly(populateMissingResolver: false);
        return options;
    }

    /// <summary>Writes a result. Throws <see cref="CheckFormatException"/> when the result breaks a rule (nothing is written then).</summary>
    public static string Serialize(CheckResult result, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var options = indented ? Indented : Compact;
        return JsonSerializer.Serialize(result, (JsonTypeInfo<CheckResult>)options.GetTypeInfo(typeof(CheckResult)));
    }

    public static byte[] SerializeToUtf8(CheckResult result, bool indented = false) => Encoding.UTF8.GetBytes(Serialize(result, indented));

    /// <summary>Reads a result strictly. Broken JSON, an unknown key, an unknown value and any broken rule are all <see cref="CheckFormatException"/>.</summary>
    public static CheckResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, (JsonTypeInfo<CheckResult>)Compact.GetTypeInfo(typeof(CheckResult)))
                ?? throw new CheckFormatException("A result must be a JSON object, not null.");
        }
        catch (JsonException ex)
        {
            throw new CheckFormatException($"Not valid JSON: {ex.Message}");
        }
    }

    public static CheckResult Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, (JsonTypeInfo<CheckResult>)Compact.GetTypeInfo(typeof(CheckResult)))
                ?? throw new CheckFormatException("A result must be a JSON object, not null.");
        }
        catch (JsonException ex)
        {
            throw new CheckFormatException($"Not valid JSON: {ex.Message}");
        }
    }

    /// <summary>The non-throwing read. On failure <paramref name="error"/> says why, and its <see cref="CheckFormatException.UnknownFormat"/> is set for a newer format.</summary>
    public static bool TryDeserialize(string json, out CheckResult? result, out CheckFormatException? error)
    {
        try
        {
            result = Deserialize(json);
            error = null;
            return true;
        }
        catch (CheckFormatException ex)
        {
            result = null;
            error = ex;
            return false;
        }
    }

    public static string SerializeHistory(CheckHistoryPage page, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        var options = indented ? Indented : Compact;
        return JsonSerializer.Serialize(page, (JsonTypeInfo<CheckHistoryPage>)options.GetTypeInfo(typeof(CheckHistoryPage)));
    }

    public static CheckHistoryPage DeserializeHistory(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, (JsonTypeInfo<CheckHistoryPage>)Compact.GetTypeInfo(typeof(CheckHistoryPage)))
                ?? throw new CheckFormatException("A history page must be a JSON object, not null.");
        }
        catch (JsonException ex)
        {
            throw new CheckFormatException($"Not valid JSON: {ex.Message}");
        }
    }
}
