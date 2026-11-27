using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PcMieru.Party.Game;
using PcMieru.Party.I18n;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// The list of fields a repair could not read shows names, not internal paths. Every path the import can produce must have a name in both
/// languages (an unknown path is still shown as it is, so nothing is hidden, but a field added to the save without a name fails here).
/// </summary>
[Collection("Strings.Lang")]
public class ImportFieldNamesTests
{
    private static void InLanguage(string lang, Action body)
    {
        var before = Strings.Lang;
        Strings.Lang = lang;
        try
        {
            body();
        }
        finally
        {
            Strings.Lang = before;
        }
    }

    // ---- the shapes of every path, taken from the type the audit walks (the same filter and the same kinds as FieldAudit) ----

    private static IEnumerable<PropertyInfo> Bound(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !p.IsDefined(typeof(JsonExtensionDataAttribute))
                        && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(JsonIgnoreAttribute)));

    private static bool IsDictionary(Type t, out Type value)
    {
        value = typeof(object);
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(Dictionary<,>) || t.GetGenericArguments()[0] != typeof(string)) return false;
        value = t.GetGenericArguments()[1];
        return true;
    }

    private static bool IsSequence(Type t, out Type item)
    {
        item = typeof(object);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { item = t.GetGenericArguments()[0]; return true; }
        if (t.IsArray) { item = t.GetElementType()!; return true; }
        return false;
    }

    private static bool IsObject(Type t) => t.IsClass && t != typeof(string) && t.GetConstructor(Type.EmptyTypes) is not null;

    private static void Walk(Type type, string prefix, ISet<string> into)
    {
        foreach (var p in Bound(type))
        {
            string path = prefix + p.Name;
            into.Add(path);
            Visit(p.PropertyType, path, into);
        }
    }

    private static void Visit(Type t, string path, ISet<string> into)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsEnum) return;
        if (IsDictionary(t, out var v))
        {
            string e = path + "[]";
            into.Add(e);
            Visit(v, e, into);
        }
        else if (IsSequence(t, out var item))
        {
            string e = path + "[]";
            into.Add(e);
            Visit(item, e, into);
        }
        else if (IsObject(t))
        {
            Walk(t, path + ".", into);
        }
    }

    /// <summary>"Experience[cpu#0]" to "Experience[]" (the keys of these tests never hold a bracket).</summary>
    private static string Shape(string path)
    {
        var sb = new StringBuilder();
        bool inside = false;
        foreach (char c in path)
        {
            if (c == '[') { inside = true; sb.Append("[]"); }
            else if (c == ']') inside = false;
            else if (!inside) sb.Append(c);
        }

        return sb.ToString();
    }

    private static HashSet<string> EveryShape()
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        Walk(typeof(PartyState), "", all);
        return all;
    }

    // ---- a save broken in every place, read by the real audit ----

    private static JsonNode? Wrong(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsEnum) return "not-a-name";
        if (IsDictionary(t, out var v)) return new JsonObject { ["a#0"] = null, ["b#1"] = Wrong(v) };
        if (IsSequence(t, out var item)) return new JsonArray(null, Wrong(item));
        if (IsObject(t)) return WrongObject(t);
        return new JsonArray();   // an array is the wrong kind for a number, a flag and a text alike
    }

    private static JsonObject WrongObject(Type type)
    {
        var o = new JsonObject();
        foreach (var p in Bound(type)) o[p.Name] = Wrong(p.PropertyType);
        return o;
    }

    private static List<string> PathsTheAuditProduces()
    {
        var paths = new List<string>();
        // one document with every field of the save wrong as a whole, one with every nested place wrong
        var shallow = new JsonObject();
        foreach (var p in Bound(typeof(PartyState))) shallow[p.Name] = new JsonArray();
        foreach (var doc in new[] { shallow, WrongObject(typeof(PartyState)) })
        {
            var broken = new RepairSink();
            using var parsed = JsonDocument.Parse(doc.ToJsonString());
            FieldAudit.AuditObject(parsed.RootElement, typeof(PartyState), "", PartyState.Opts, null, broken);
            paths.AddRange(broken.ToList());
        }

        return paths.Where(p => !p.StartsWith('+')).Distinct().ToList();
    }

    [Fact]
    public void The_walk_finds_the_fields_of_the_save()
    {
        var all = EveryShape();
        Assert.True(all.Count >= 50, "found only " + all.Count);
        Assert.Contains("Adventure.Gold", all);
        Assert.Contains("Experience[]", all);
        Assert.Contains("GpuSlots[]", all);
        Assert.Contains("Adventure.Equipped[][]", all);
        Assert.DoesNotContain("Unknown", all);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Every_field_of_the_save_has_a_name_in_both_languages(string lang)
    {
        var known = new HashSet<string>(ImportFieldNames.KnownPatterns, StringComparer.Ordinal);
        foreach (var shape in EveryShape())
            Assert.True(known.Contains(shape), $"no name for the field '{shape}'");
        InLanguage(lang, () =>
        {
            foreach (var shape in EveryShape())
            {
                var name = ImportFieldNames.Display(shape);
                Assert.False(string.IsNullOrWhiteSpace(name), shape);
                Assert.NotEqual(shape, name);
                if (lang == "en") Assert.All(name, c => Assert.True(c < 128, $"non-ASCII in the en name of '{shape}'"));
                else Assert.Contains(name, c => c >= 0x3000);
            }
        });
    }

    [Fact]
    public void The_table_holds_no_name_for_a_field_that_is_gone()
    {
        var all = EveryShape();
        foreach (var pattern in ImportFieldNames.KnownPatterns)
            Assert.True(all.Contains(pattern), $"'{pattern}' is named but the save has no such field");
    }

    [Fact]
    public void The_two_languages_differ_for_every_field()
    {
        var ja = new Dictionary<string, string>();
        InLanguage("ja", () => { foreach (var s in ImportFieldNames.KnownPatterns) ja[s] = ImportFieldNames.Display(s); });
        InLanguage("en", () => { foreach (var s in ImportFieldNames.KnownPatterns) Assert.NotEqual(ja[s], ImportFieldNames.Display(s)); });
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Every_path_the_real_audit_produces_for_a_broken_save_is_named(string lang)
    {
        var paths = PathsTheAuditProduces();
        Assert.True(paths.Count >= 50, "the audit produced only " + paths.Count);
        Assert.Contains("Adventure.Gold", paths);
        Assert.Contains(paths, p => p.StartsWith("Experience[", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.StartsWith("GpuSlots[", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.StartsWith("Adventure.Equipped[", StringComparison.Ordinal));
        InLanguage(lang, () =>
        {
            foreach (var path in paths)
            {
                var name = ImportFieldNames.Display(path);
                Assert.NotEqual(path, name);   // a path that comes back as it is has no name
                Assert.DoesNotContain(".", name.Replace("M.2", ""), StringComparison.Ordinal);   // no internal "A.B" path is left in it
            }
        });
    }

    [Fact]
    public void The_paths_the_import_adds_itself_are_named_too()
    {
        // StateImport adds "GpuSlots" (a repaired list that the audit did not name) and "<unidentified>"; the sink adds "+N more".
        foreach (var lang in new[] { "ja", "en" })
            InLanguage(lang, () =>
            {
                foreach (var path in new[] { nameof(PartyState.GpuSlots), "<unidentified>", "+7 more" })
                    Assert.NotEqual(path, ImportFieldNames.Display(path));
            });
    }

    [Fact]
    public void The_named_examples_read_as_agreed()
    {
        InLanguage("ja", () =>
        {
            Assert.Equal("冒険のお金", ImportFieldNames.Display("Adventure.Gold"));
            Assert.Equal("経験値（CPU0）", ImportFieldNames.Display("Experience[cpu#0]"));
            Assert.Equal("経験値（FAN0）", ImportFieldNames.Display("Experience[cpu-cooler#0]"));
            Assert.Equal("経験値（MEM）", ImportFieldNames.Display("Experience[memory]"));
            Assert.Equal("経験値（GPU1）", ImportFieldNames.Display("Experience[gpu#1]"));
            Assert.Equal("経験値（M.2）", ImportFieldNames.Display("Experience[m2]"));
            Assert.Equal("GPU の番号の記録（2 件目）", ImportFieldNames.Display("GpuSlots[1]"));
            Assert.Equal("冒険の装備（GPU0、3 件目）", ImportFieldNames.Display("Adventure.Equipped[gpu#0][2]"));
            Assert.Equal("ほか 3 件", ImportFieldNames.Display("+3 more"));
        });
        InLanguage("en", () =>
        {
            Assert.Equal("Adventure gold", ImportFieldNames.Display("Adventure.Gold"));
            Assert.Equal("Experience (CPU0)", ImportFieldNames.Display("Experience[cpu#0]"));
            Assert.Equal("Experience (PSU)", ImportFieldNames.Display("Experience[psu]"));
            Assert.Equal("GPU slot record (#2)", ImportFieldNames.Display("GpuSlots[1]"));
            Assert.Equal("Adventure gear (GPU0, #3)", ImportFieldNames.Display("Adventure.Equipped[gpu#0][2]"));
            Assert.Equal("and 3 more", ImportFieldNames.Display("+3 more"));
        });
    }

    [Theory]
    [InlineData("Levels.Cpu")]
    [InlineData("Adventure.Nothing")]
    [InlineData("Adventure.Gold.Deeper")]
    [InlineData("Experience[unclosed")]
    [InlineData("Experience[a]b")]
    [InlineData("")]
    [InlineData(".Gold")]
    [InlineData("+x more")]
    public void An_unknown_or_odd_path_is_shown_as_it_is(string path)
    {
        foreach (var lang in new[] { "ja", "en" })
            InLanguage(lang, () => Assert.Equal(path, ImportFieldNames.Display(path)));
    }

    [Fact]
    public void An_unknown_key_of_a_known_field_is_kept_in_the_name()
    {
        InLanguage("ja", () =>
        {
            Assert.Equal("経験値（who-knows）", ImportFieldNames.Display("Experience[who-knows]"));
            Assert.Equal("傍白を出した時刻（a.b]c...）", ImportFieldNames.Display("LastEventAt[a.b]c...]"));
        });
    }

    [Fact]
    public void The_test_reading_of_a_path_and_the_shipped_one_give_the_same_field()
    {
        foreach (var path in PathsTheAuditProduces())
            Assert.Contains(Shape(path), ImportFieldNames.KnownPatterns);
    }
}
