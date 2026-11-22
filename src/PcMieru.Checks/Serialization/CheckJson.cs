using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PcMieru.Checks;

/// <summary>
/// The entry points for pcm-check/1 JSON. The output is deterministic: the same result is always the same bytes
/// (fixed key order, "\n" line ends, the degree sign written as itself, no trailing newline; a file writer adds one).
/// Reading is strict by default (<see cref="Deserialize(string)"/>) and tolerant on request (<see cref="Read(string, CheckReadMode)"/>,
/// <see cref="DeserializeHistory"/>); both end in <see cref="CheckResultValidator"/>, and every failure is a <see cref="CheckFormatException"/>.
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
    public static CheckResult Deserialize(string json) => Read(json, CheckReadMode.Strict).Result;

    public static CheckResult Deserialize(ReadOnlySpan<byte> utf8) => Read(utf8, CheckReadMode.Strict).Result;

    /// <summary>
    /// Reads a result in the given mode. Tolerant is for the history reader (DESIGN-A §3): it ignores unknown keys and skips
    /// the rows a newer version added (<see cref="CheckReading.SkippedItems"/>), and a "kind" it does not know is
    /// <see cref="CheckFormatException.UnknownKind"/>. Strict is <see cref="Deserialize(string)"/>.
    /// </summary>
    public static CheckReading Read(string json, CheckReadMode mode)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return CheckResultReader.Read(document.RootElement, mode);
        }
        catch (JsonException ex)
        {
            throw new CheckFormatException($"Not valid JSON: {ex.Message}");
        }
    }

    public static CheckReading Read(ReadOnlySpan<byte> utf8, CheckReadMode mode)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            return CheckResultReader.Read(document.RootElement, mode);
        }
        catch (JsonException ex)
        {
            throw new CheckFormatException($"Not valid JSON: {ex.Message}");
        }
    }

    /// <summary>The non-throwing strict read. On failure <paramref name="error"/> says why, and its <see cref="CheckFormatException.UnknownFormat"/> is set for a newer format.</summary>
    public static bool TryDeserialize(string json, out CheckResult? result, out CheckFormatException? error)
    {
        if (TryRead(json, CheckReadMode.Strict, out var reading, out error))
        {
            result = reading!.Result;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>The non-throwing <see cref="Read(string, CheckReadMode)"/>.</summary>
    public static bool TryRead(string json, CheckReadMode mode, out CheckReading? reading, out CheckFormatException? error)
    {
        try
        {
            reading = Read(json, mode);
            error = null;
            return true;
        }
        catch (CheckFormatException ex)
        {
            reading = null;
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

    /// <summary>
    /// Reads a history page tolerantly (see <see cref="CheckHistoryReader"/>): an entry of a kind a later version added is
    /// left out and counted in a <see cref="CheckHistoryWarningCodes.FutureFormat"/> warning instead of failing the page.
    /// A missing required field, a wrong type or an unknown verdict, outcome or warning code is a <see cref="CheckFormatException"/>.
    /// </summary>
    public static CheckHistoryPage DeserializeHistory(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return CheckHistoryReader.Read(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CheckFormatException($"Not valid JSON: {ex.Message}");
        }
    }
}
