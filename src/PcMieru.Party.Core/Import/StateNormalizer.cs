using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PcMieru.Party.Import;

/// <summary>
/// Applies, on the state that was read, the repairs that <see cref="FieldAudit"/> reports, so that the converted bytes are
/// clean and importing them again finds nothing to repair.
///
/// The serializer is forgiving in places the audit is not: a null inside a list, a null equipment slot, a null dictionary
/// value and an infinite number all read without error, but cannot be written back as they were (or would be reported
/// again). They are fixed here the way <see cref="PcMieru.Party.Game.LenientJson"/> does it:
///   - a non-finite number becomes the default of its field (or is dropped from a list or dictionary);
///   - a null element of a list, and a dictionary entry whose value is null, is dropped;
///   - a null slot of an array keeps its position and becomes an empty string (an empty slot).
/// Nothing else is touched: values that are merely out of range are carried as they are.
/// Walks the properties by reflection, like the audit, so a new field is covered without editing this file.
/// </summary>
internal static class StateNormalizer
{
    private const int MaxDepth = 8;

    public static void Run(object state) => FixObject(state, 0);

    private static void FixObject(object obj, int depth)
    {
        if (depth > MaxDepth) return;
        var type = obj.GetType();
        object? fresh = null;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.IsDefined(typeof(JsonExtensionDataAttribute)) || p.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            object? value = p.GetValue(obj);
            if (value is null) continue;
            if (IsNonFinite(value))
            {
                fresh ??= Activator.CreateInstance(type)!;
                p.SetValue(obj, p.GetValue(fresh));
            }
            else
            {
                FixValue(value, depth + 1);
            }
        }
    }

    private static bool IsNonFinite(object v) =>
        (v is double d && !double.IsFinite(d)) || (v is float f && !float.IsFinite(f));

    private static void FixValue(object v, int depth)
    {
        switch (v)
        {
            case string:
                return;

            case IDictionary dict:
            {
                var drop = new List<object>();
                foreach (DictionaryEntry e in dict)
                {
                    if (e.Value is null || IsNonFinite(e.Value)) drop.Add(e.Key);
                    else FixValue(e.Value, depth + 1);
                }
                foreach (var key in drop) dict.Remove(key);
                return;
            }

            case Array arr:
            {
                bool strings = arr.GetType().GetElementType() == typeof(string);
                for (int i = 0; i < arr.Length; i++)
                {
                    object? x = arr.GetValue(i);
                    if (x is null) { if (strings) arr.SetValue("", i); }
                    else if (IsNonFinite(x)) arr.SetValue(Convert.ChangeType(0, x.GetType()), i);
                    else FixValue(x, depth + 1);
                }
                return;
            }

            case IList list:
            {
                bool dirty = false;
                foreach (var x in list)
                    if (x is null || IsNonFinite(x)) { dirty = true; break; }
                if (dirty)
                {
                    var keep = new List<object?>(list.Count);
                    foreach (var x in list)
                        if (!(x is null || IsNonFinite(x))) keep.Add(x);
                    list.Clear();
                    foreach (var x in keep) list.Add(x);
                }
                foreach (var x in list)
                    if (x is not null) FixValue(x, depth + 1);
                return;
            }

            default:
                if (v.GetType().IsClass && v.GetType().GetConstructor(Type.EmptyTypes) is not null) FixObject(v, depth);
                return;
        }
    }
}
