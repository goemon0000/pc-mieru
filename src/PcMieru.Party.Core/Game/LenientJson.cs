using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMieru.Party.Game;

/// <summary>
/// 保存を**欄ごとに**読む。普通の読み方（<see cref="JsonSerializer"/> で丸ごと）が失敗したときだけ使う。
///
/// 丸ごと読むと、冒険の中の一つの欄（綴り違いの列挙、小数の金、null の体力）が読めないだけで
/// 例外になり、物語の進みとレベルまで一緒に捨てることになる。ここでは:
///   - 読めない欄は既定値のまま残し、他の欄は読む
///   - 辞書と一覧は、読めない要素だけを落とす（配列は位置を保ち、読めない要素を既定値にする）
///   - 整数の欄に小数が入っていたら切り捨てて使う
///   - 知らない欄は <see cref="JsonExtensionDataAttribute"/> の付いた欄に預け、書き戻すときに残す
///   - 同じ名前の欄が二つあれば後の方（丸ごと読むときと同じ）
///
/// 起動時に一度だけ、しかも壊れていたときだけ走るので、速さより確実さを取って反射で書いてある。
/// </summary>
internal static class LenientJson
{
    public static object Read(JsonElement obj, Type type, JsonSerializerOptions opts)
    {
        var target = Activator.CreateInstance(type)!;
        Populate(target, obj, opts);
        return target;
    }

    public static void Populate(object target, JsonElement obj, JsonSerializerOptions opts)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject()) fields[p.Name] = p.Value;

        PropertyInfo? extra = null;
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.IsDefined(typeof(JsonExtensionDataAttribute))) { extra = p; continue; }
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0
                || p.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            if (!fields.Remove(p.Name, out var node)) continue;
            // null の欄は既定値のまま（null の体力・null の一覧で止まらない）
            if (node.ValueKind == JsonValueKind.Null) continue;
            if (TryRead(node, p.PropertyType, opts, out var value)) p.SetValue(target, value);
        }
        // 残ったのは知らない欄。新しい版の物かもしれないので、預かって書き戻す
        if (extra is not null && fields.Count > 0)
            extra.SetValue(target, fields.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal));
    }

    public static bool TryRead(JsonElement node, Type type, JsonSerializerOptions opts, out object? value)
    {
        try
        {
            value = node.Deserialize(type, opts);
            if (value is not null) return true;
        }
        catch (Exception e) when (IsReadError(e)) { }
        value = null;

        // ---- 直せる物は直す ----
        if ((type == typeof(int) || type == typeof(long))
            && node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out double d) && double.IsFinite(d))
        {
            double t = Math.Truncate(d);
            value = type == typeof(int)
                ? (int)Math.Clamp(t, int.MinValue, int.MaxValue)
                : (object)(long)Math.Clamp(t, long.MinValue, long.MaxValue);
            return true;
        }
        if (node.ValueKind == JsonValueKind.Object && type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && type.GetGenericArguments()[0] == typeof(string))
        {
            var dict = (IDictionary)Activator.CreateInstance(type)!;
            var vt = type.GetGenericArguments()[1];
            foreach (var p in node.EnumerateObject())
                if (p.Value.ValueKind != JsonValueKind.Null && TryRead(p.Value, vt, opts, out var cv)) dict[p.Name] = cv;
            value = dict;
            return true;
        }
        if (node.ValueKind == JsonValueKind.Array && type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            var et = type.GetGenericArguments()[0];
            foreach (var child in node.EnumerateArray())
                if (child.ValueKind != JsonValueKind.Null && TryRead(child, et, opts, out var cv)) list.Add(cv);
            value = list;
            return true;
        }
        if (node.ValueKind == JsonValueKind.Array && type.IsArray)
        {
            // 配列は位置に意味がある（装備の [手, 身, 守り]）。読めない要素は詰めずに既定値にする
            var et = type.GetElementType()!;
            var arr = Array.CreateInstance(et, node.GetArrayLength());
            int i = 0;
            foreach (var child in node.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Null && TryRead(child, et, opts, out var cv)) arr.SetValue(cv, i);
                i++;
            }
            value = arr;
            return true;
        }
        if (node.ValueKind == JsonValueKind.Object && type.IsClass && type != typeof(string)
            && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            try
            {
                value = Read(node, type, opts);
                return true;
            }
            catch (Exception e) when (IsReadError(e) || e is TargetInvocationException or MissingMethodException) { }
        }
        return false;
    }

    /// <summary>中身が想定と違うときに出る例外。これ以外（メモリ不足など）は握らない。</summary>
    public static bool IsReadError(Exception e) =>
        e is JsonException or InvalidOperationException or NotSupportedException
          or FormatException or OverflowException or ArgumentException;
}

/// <summary>
/// 列挙を名前で読み書きする。**知らない名前・範囲外の数は、決めておいた安全な値になる。**
/// 新しい版で増えた値（"Mode": "Ship" 等）や手で書き換えた綴り違いで、保存ごと読めなくならないように。
/// 数で書かれた古い保存も読める。
/// </summary>
public class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly T _fallback;

    protected TolerantEnumConverter(T fallback) => _fallback = fallback;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse(reader.GetString(), ignoreCase: true, out T named) && Enum.IsDefined(named)
                    ? named : _fallback;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out int n))
                {
                    var e = (T)Enum.ToObject(typeof(T), n);
                    if (Enum.IsDefined(e)) return e;
                }
                return _fallback;
            default:
                reader.Skip();   // 物や配列が入っていても、読み終えて既定値にする
                return _fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
