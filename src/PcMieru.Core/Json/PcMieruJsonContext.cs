using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PcMieru.Core;

/// <summary>
/// JSON for every Core shape: camelCase, string enums, 64-bit big values as strings, nulls omitted.
/// The source generator does not support one context split over several files (it emits every type once per part and
/// fails), so each work area owns its own context and <see cref="PcMieruJson"/> chains them:
///   PcMieruJsonContext (this file)  shared leaf types (integration owner)
///   CollectionJsonContext           A
///   HardwareJsonContext             C
///   ManagementJsonContext           D
///   ReportingJsonContext            E
/// Register a type in your own area's context. A type may appear in more than one (the first in the chain wins; the
/// metadata is identical). Every T used inside a Value&lt;T&gt; must be resolvable from the chain (a test enforces it).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(U128))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class PcMieruJsonContext : JsonSerializerContext;

/// <summary>Entry points for JSON output. Compact for pipes and MCP, indented for humans and fixtures.</summary>
public static class PcMieruJson
{
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                PcMieruJsonContext.Default,
                CollectionJsonContext.Default,
                HardwareJsonContext.Default,
                ManagementJsonContext.Default,
                ReportingJsonContext.Default),
        };
        options.MakeReadOnly(populateMissingResolver: false);
        return options;
    }

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Compact.GetTypeInfo(typeof(T));

    /// <summary>Throws NotSupportedException when the type is not registered in any context.</summary>
    public static JsonTypeInfo GetTypeInfo(Type type, bool indented = false) =>
        (indented ? Indented : Compact).GetTypeInfo(type);

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)GetTypeInfo(typeof(T), indented));

    public static JsonNode? ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Info<T>());

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, Info<T>());
}
