using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PcMieru.Wire;

namespace PcMieru.Core;

/// <summary>
/// Why a value is (not) there. NotCollected is the zero value on purpose: default(Value&lt;T&gt;) must never
/// read as a successful measurement.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ValueStatus>))]
public enum ValueStatus
{
    NotCollected = 0,
    Ok = 1,
    Unsupported = 2,
    Denied = 3,
    Timeout = 4,
    Error = 5,
    Gone = 6,
}

/// <summary>
/// A measured or read value with its origin and state. JSON: { "value"?: T, "status": "...", "source": "...", "detail"?: "..." }.
/// "value" is written only when Status is Ok, so a missing value can never serialize as 0.
/// </summary>
[JsonConverter(typeof(ValueJsonConverterFactory))]
public readonly record struct Value<T>(T? Data, ValueStatus Status, string Source, string? Detail = null)
{
    public bool IsOk => Status == ValueStatus.Ok;

    public bool TryGet([MaybeNullWhen(false)] out T value)
    {
        value = Data;
        return Status == ValueStatus.Ok && Data is not null;
    }

    public T GetOr(T fallback) => Status == ValueStatus.Ok && Data is not null ? Data : fallback;

    public Value<TOut> Map<TOut>(Func<T, TOut> map) =>
        TryGet(out var v) ? Value.Ok(map(v), Source) : new Value<TOut>(default, Status, Source, Detail);
}

/// <summary>Factory helpers for <see cref="Value{T}"/>.</summary>
public static class Value
{
    public static Value<T> Ok<T>(T data, string source) =>
        new(data ?? throw new ArgumentNullException(nameof(data)), ValueStatus.Ok, source);

    public static Value<T> Missing<T>(ValueStatus status, string source, string? detail = null)
    {
        if (status == ValueStatus.Ok)
        {
            throw new ArgumentException("Use Value.Ok for a present value.", nameof(status));
        }

        return new(default, status, source, detail);
    }

    public static Value<T> NotCollected<T>(string source, string? detail = null) => Missing<T>(ValueStatus.NotCollected, source, detail);

    public static Value<T> Unsupported<T>(string source, string? detail = null) => Missing<T>(ValueStatus.Unsupported, source, detail);

    public static Value<T> Denied<T>(string source, string? detail = null) => Missing<T>(ValueStatus.Denied, source, detail);

    public static Value<T> Failed<T>(string source, string? detail = null) => Missing<T>(ValueStatus.Error, source, detail);

    public static Value<T> Gone<T>(string source, string? detail = null) => Missing<T>(ValueStatus.Gone, source, detail);
}

public sealed class ValueJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Value<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter?)Activator.CreateInstance(typeof(ValueJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]));
}

/// <summary>
/// T is serialized through the context's own type info, so every T used inside a Value&lt;T&gt; must be listed
/// with [JsonSerializable] in your area JSON context (a test enforces this).
/// </summary>
internal sealed class ValueJsonConverter<T> : JsonConverter<Value<T>>
{
    private static readonly string[] StatusNames = ["notCollected", "ok", "unsupported", "denied", "timeout", "error", "gone"];

    private static JsonTypeInfo<T> Info(JsonSerializerOptions options)
    {
        try
        {
            return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException(
                $"Value<{typeof(T).Name}> needs [JsonSerializable(typeof({typeof(T).Name}))] in an area JSON context (see PcMieruJson).", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, Value<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Status == ValueStatus.Ok && value.Data is not null)
        {
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, value.Data, Info(options));
        }

        var status = value.Status == ValueStatus.Ok && value.Data is null ? ValueStatus.Error : value.Status;
        writer.WriteString("status", StatusNames[(int)status]);
        writer.WriteString("source", value.Source ?? "");
        if (value.Detail is not null)
        {
            writer.WriteString("detail", value.Detail);
        }

        writer.WriteEndObject();
    }

    public override Value<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Value must be an object.");
        }

        T? data = default;
        var hasValue = false;
        var status = ValueStatus.NotCollected;
        string source = "";
        string? detail = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "value":
                    data = JsonSerializer.Deserialize(ref reader, Info(options));
                    hasValue = data is not null;
                    break;
                case "status":
                    var text = reader.GetString();
                    var index = Array.IndexOf(StatusNames, text);
                    status = index >= 0 ? (ValueStatus)index : throw new JsonException($"Unknown status '{text}'.");
                    break;
                case "source":
                    source = reader.GetString() ?? "";
                    break;
                case "detail":
                    detail = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (status == ValueStatus.Ok && !hasValue)
        {
            throw new JsonException("A value with status ok must carry a value.");
        }

        return new Value<T>(data, status, source, detail);
    }
}
