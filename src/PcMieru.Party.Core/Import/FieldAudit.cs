using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcMieru.Party.Game;

namespace PcMieru.Party.Import;

/// <summary>
/// Where a value sits in the source, kept as its parts so that the text of a path is only built when somebody needs it.
/// (A hostile source can have hundreds of thousands of broken elements under one very long key; building and keeping a path
/// for each of them would cost memory in proportion to key length times element count.)
/// </summary>
internal readonly struct At(string parent, string? key, int index)
{
    private const int MaxKeyShown = 48;

    public string Build() =>
        key is not null ? $"{parent}[{Shorten(key)}]" : index >= 0 ? $"{parent}[{index}]" : parent;

    /// <summary>Long keys are cut (and marked) so that a path stays short whatever the source holds.</summary>
    public static string Shorten(string s)
    {
        if (s.Length <= MaxKeyShown) return s;
        int n = MaxKeyShown;
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n] + "...";
    }
}

/// <summary>Collects the broken paths. Counts all of them, keeps the text of the first <see cref="MaxListed"/> only.</summary>
internal sealed class RepairSink
{
    public const int MaxListed = 100;

    private readonly List<string> _listed = new();

    /// <summary>How many broken places were found, listed or not.</summary>
    public int Count { get; private set; }

    public void Add(in At at)
    {
        Count++;
        if (_listed.Count < MaxListed) _listed.Add(at.Build());
    }

    public void Add(string path) => Add(new At(path, null, -1));

    /// <summary>The listed paths, then "+N more" when some were left out.</summary>
    public IReadOnlyList<string> ToList()
    {
        var all = new List<string>(_listed);
        if (Count > _listed.Count) all.Add($"+{Count - _listed.Count} more");
        return all;
    }
}

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
/// adventure starts, exactly as before); only a wrong KIND of value counts as broken. A number that is not finite
/// (1e400 reads as infinity, and infinity cannot be written back as JSON) counts as broken too.
///
/// Memory stays bounded by the size of the source: paths are built lazily (see <see cref="At"/>) and only the first
/// <see cref="RepairSink.MaxListed"/> are kept.
/// </summary>
internal static class FieldAudit
{
    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Audits the properties of <paramref name="type"/> inside <paramref name="obj"/>; <paramref name="skip"/> names properties to leave out.</summary>
    public static void AuditObject(JsonElement obj, Type type, string prefix, JsonSerializerOptions opts,
                                   ISet<string>? skip, RepairSink broken)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Same filter as LenientJson.Populate: only what the serializer would bind.
            if (p.IsDefined(typeof(JsonExtensionDataAttribute))) continue;
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || p.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            if (skip is not null && skip.Contains(p.Name)) continue;
            if (!obj.TryGetProperty(p.Name, out var node)) continue;   // case-sensitive, like the serializer

            var at = new At(prefix + p.Name, null, -1);
            if (node.ValueKind == JsonValueKind.Null)
            {
                // null is fine where the field is declared nullable (WindowLeft, Adventure); anywhere else it is a hole.
                if (!AllowsNull(p)) broken.Add(at);
                continue;
            }
            AuditValue(node, p.PropertyType, at, opts, broken);
        }
    }

    private static bool AllowsNull(PropertyInfo p) =>
        Nullable.GetUnderlyingType(p.PropertyType) is not null
        || (!p.PropertyType.IsValueType && Nullability.Create(p).WriteState == NullabilityState.Nullable);

    private static void AuditValue(JsonElement node, Type type, At at, JsonSerializerOptions opts, RepairSink broken)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsEnum)
        {
            if (!IsDefinedEnumValue(node, type)) broken.Add(at);
        }
        else if (IsStringKeyedDictionary(type, out var valueType))
        {
            if (node.ValueKind != JsonValueKind.Object) { broken.Add(at); return; }
            string here = at.Build();
            foreach (var m in node.EnumerateObject())
                AuditElement(m.Value, valueType, new At(here, m.Name, -1), opts, broken);
        }
        else if (IsList(type, out var itemType) || (type.IsArray && (itemType = type.GetElementType()) is not null))
        {
            if (node.ValueKind != JsonValueKind.Array) { broken.Add(at); return; }
            string here = at.Build();
            int i = 0;
            foreach (var child in node.EnumerateArray())
                AuditElement(child, itemType!, new At(here, null, i++), opts, broken);
        }
        else if (type.IsClass && type != typeof(string) && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            if (node.ValueKind != JsonValueKind.Object) { broken.Add(at); return; }
            AuditObject(node, type, at.Build() + ".", opts, null, broken);
        }
        else
        {
            try
            {
                object? read = node.Deserialize(type, opts);
                if (read is null
                    || (read is double d && !double.IsFinite(d))
                    || (read is float f && !float.IsFinite(f)))
                    broken.Add(at);
            }
            catch (Exception e) when (LenientJson.IsReadError(e))
            {
                broken.Add(at);
            }
        }
    }

    /// <summary>An element of a list, array or dictionary. null is never meaningful there (LenientJson drops it).</summary>
    private static void AuditElement(JsonElement node, Type type, At at, JsonSerializerOptions opts, RepairSink broken)
    {
        if (node.ValueKind == JsonValueKind.Null) broken.Add(at);
        else AuditValue(node, type, at, opts, broken);
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
