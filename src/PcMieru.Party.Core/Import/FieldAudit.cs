using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcMieru.Party.Game;

namespace PcMieru.Party.Import;

/// <summary>
/// Lists the known fields of a saved state that cannot be read as written.
///
/// <see cref="PartyState.FromJson(string, out bool)"/> already survives such fields (it falls back to
/// <see cref="LenientJson"/>), but it does so quietly, and its tolerant enum converters swallow unknown names without
/// even raising the flag. An importer must not claim success in either case, so this walks the same properties
/// (reflection, like LenientJson, so a field added to PartyState/AdventureState is covered without editing this file)
/// and reports every place where the JSON does not have the shape the C# type asks for.
///
/// Absent fields are fine (defaults). Out-of-range numbers are fine too (AdventureState.Sanitize clamps them when the
/// adventure starts, exactly as before); only a wrong KIND of value counts as broken.
/// </summary>
internal static class FieldAudit
{
    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Audits the properties of <paramref name="type"/> inside <paramref name="obj"/>; <paramref name="skip"/> names properties to leave out.</summary>
    public static void AuditObject(JsonElement obj, Type type, string prefix, JsonSerializerOptions opts,
                                   ISet<string>? skip, List<string> broken)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Same filter as LenientJson.Populate: only what the serializer would bind.
            if (p.IsDefined(typeof(JsonExtensionDataAttribute))) continue;
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || p.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            if (skip is not null && skip.Contains(p.Name)) continue;
            if (!obj.TryGetProperty(p.Name, out var node)) continue;   // case-sensitive, like the serializer

            string path = prefix + p.Name;
            if (node.ValueKind == JsonValueKind.Null)
            {
                // null is fine where the field is declared nullable (WindowLeft, Adventure); anywhere else it is a hole.
                if (!AllowsNull(p)) broken.Add(path);
                continue;
            }
            AuditValue(node, p.PropertyType, path, opts, broken);
        }
    }

    private static bool AllowsNull(PropertyInfo p) =>
        Nullable.GetUnderlyingType(p.PropertyType) is not null
        || (!p.PropertyType.IsValueType && Nullability.Create(p).WriteState == NullabilityState.Nullable);

    private static void AuditValue(JsonElement node, Type type, string path, JsonSerializerOptions opts, List<string> broken)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsEnum)
        {
            if (!IsDefinedEnumValue(node, type)) broken.Add(path);
        }
        else if (IsStringKeyedDictionary(type, out var valueType))
        {
            if (node.ValueKind != JsonValueKind.Object) { broken.Add(path); return; }
            foreach (var m in node.EnumerateObject())
                AuditElement(m.Value, valueType, $"{path}[{m.Name}]", opts, broken);
        }
        else if (IsList(type, out var itemType) || (type.IsArray && (itemType = type.GetElementType()) is not null))
        {
            if (node.ValueKind != JsonValueKind.Array) { broken.Add(path); return; }
            int i = 0;
            foreach (var child in node.EnumerateArray())
                AuditElement(child, itemType!, $"{path}[{i++}]", opts, broken);
        }
        else if (type.IsClass && type != typeof(string) && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            if (node.ValueKind != JsonValueKind.Object) { broken.Add(path); return; }
            AuditObject(node, type, path + ".", opts, null, broken);
        }
        else
        {
            try
            {
                if (node.Deserialize(type, opts) is null) broken.Add(path);
            }
            catch (Exception e) when (LenientJson.IsReadError(e))
            {
                broken.Add(path);
            }
        }
    }

    /// <summary>An element of a list, array or dictionary. null is never meaningful there (LenientJson drops it).</summary>
    private static void AuditElement(JsonElement node, Type type, string path, JsonSerializerOptions opts, List<string> broken)
    {
        if (node.ValueKind == JsonValueKind.Null) broken.Add(path);
        else AuditValue(node, type, path, opts, broken);
    }

    /// <summary>The same test TolerantEnumConverter uses to accept a value rather than fall back.</summary>
    private static bool IsDefinedEnumValue(JsonElement node, Type enumType)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                return Enum.TryParse(enumType, node.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(enumType, named!);
            case JsonValueKind.Number:
                return node.TryGetInt32(out int n) && Enum.IsDefined(enumType, Enum.ToObject(enumType, n));
            default:
                return false;
        }
    }

    private static bool IsStringKeyedDictionary(Type type, out Type valueType)
    {
        valueType = typeof(object);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>)) return false;
        var args = type.GetGenericArguments();
        if (args[0] != typeof(string)) return false;
        valueType = args[1];
        return true;
    }

    private static bool IsList(Type type, out Type itemType)
    {
        itemType = typeof(object);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) return false;
        itemType = type.GetGenericArguments()[0];
        return true;
    }
}
