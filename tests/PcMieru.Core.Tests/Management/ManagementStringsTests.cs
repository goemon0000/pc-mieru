using System.Reflection;
using PcMieru.Core.Management;
using PcMieru.Wire;

namespace PcMieru.Core.Tests.Management;

public class ManagementStringsTests
{
    // The values each key takes, written out here on purpose: the text of a key must use exactly these.
    private static readonly Dictionary<string, string[]> Expected = new()
    {
        ["mgmt.process.terminated"] = ["pid"],
        ["mgmt.process.terminate-pending"] = ["pid"],
        ["mgmt.process.error"] = ["code"],
        ["mgmt.process.priority-set"] = ["priority"],
        ["mgmt.process.closed"] = ["pid"],
        ["mgmt.process.close-sent"] = ["pid"],
        ["mgmt.process.tree-done"] = ["count"],
        ["mgmt.process.tree-partial"] = ["done", "total"],
        ["mgmt.service.started"] = ["name"],
        ["mgmt.service.stopped"] = ["name"],
        ["mgmt.service.restarted"] = ["name"],
        ["mgmt.service.already-running"] = ["name"],
        ["mgmt.service.already-stopped"] = ["name"],
        ["mgmt.service.start-pending"] = ["name"],
        ["mgmt.service.stop-pending"] = ["name"],
        ["mgmt.service.start-failed"] = ["exitCode", "name"],
        ["mgmt.service.not-found"] = ["name"],
        ["mgmt.service.denied"] = ["name"],
        ["mgmt.service.disabled"] = ["name"],
        ["mgmt.service.dependents-running"] = ["dependents", "name"],
        ["mgmt.service.cannot-stop"] = ["name"],
        ["mgmt.service.restart-stopped-only"] = ["name", "reason"],
        ["mgmt.service.error"] = ["code", "name"],
        ["mgmt.startup.enabled"] = ["name"],
        ["mgmt.startup.disabled"] = ["name"],
        ["mgmt.startup.already-enabled"] = ["name"],
        ["mgmt.startup.already-disabled"] = ["name"],
        ["mgmt.startup.not-found"] = ["name"],
        ["mgmt.startup.error"] = ["code"],
        ["mgmt.startup.unknown-state"] = ["name"],
        ["mgmt.startup.denied"] = ["name"],
        ["mgmt.startup.restored"] = ["name"],
        ["mgmt.startup.nothing-to-undo"] = ["id"],
        ["mgmt.disk.not-found"] = ["drive"],
        ["mgmt.disk.unsupported-bus"] = ["bus"],
        ["mgmt.disk.error"] = ["code"],
        ["mgmt.helper.start-failed"] = ["code"],
        ["mgmt.helper.unexpected-exit"] = ["code"],
        ["mgmt.task.started"] = ["command"],
        ["mgmt.task.not-found"] = ["command"],
        ["mgmt.task.failed"] = ["code"],
    };

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static IEnumerable<string> AllKeys() =>
        ConstantsOf(typeof(MessageKeys)).Concat(ConstantsOf(typeof(ManagementStrings)));

    [Fact]
    public void Every_message_key_has_a_ja_and_an_en_text()
    {
        Strings.Register(ManagementStrings.All);
        var missing = new List<string>();
        foreach (var key in AllKeys())
        {
            foreach (var language in new[] { Language.Ja, Language.En })
            {
                var text = Strings.Get(key, language);
                if (string.IsNullOrWhiteSpace(text) || text == "[" + key + "]")
                {
                    missing.Add($"{key} ({language})");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_key_uses_the_mgmt_prefix_and_is_unique()
    {
        var keys = AllKeys().ToArray();
        Assert.All(keys, k => Assert.StartsWith("mgmt.", k, StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.Length, ManagementStrings.All.Length);
    }

    [Fact]
    public void Each_text_asks_only_for_values_the_caller_gives()
    {
        Strings.Register(ManagementStrings.All);
        var problems = new List<string>();
        foreach (var key in AllKeys())
        {
            var given = Expected.TryGetValue(key, out var names) ? names : [];
            foreach (var language in new[] { Language.Ja, Language.En })
            {
                var asked = Strings.Placeholders(Strings.Get(key, language)).Distinct().Order(StringComparer.Ordinal).ToArray();
                // A text may leave a value out, but a placeholder nobody fills would show as "{name}" to the person.
                if (!asked.All(given.Contains))
                {
                    problems.Add($"{key} ({language}): asks for [{string.Join(",", asked)}] but the caller gives [{string.Join(",", given)}]");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void The_expected_table_names_only_keys_that_exist()
    {
        var keys = AllKeys().ToHashSet(StringComparer.Ordinal);
        Assert.All(Expected.Keys, k => Assert.Contains(k, keys));
    }

    [Fact]
    public void Wire_message_keys_and_the_strings_class_never_use_the_same_key_twice()
    {
        var wire = ConstantsOf(typeof(MessageKeys)).ToHashSet(StringComparer.Ordinal);
        var core = ConstantsOf(typeof(ManagementStrings)).ToArray();
        Assert.DoesNotContain(core, wire.Contains);
    }
}
