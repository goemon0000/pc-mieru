using PcMieru.Checks;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The words of the checks page. The Checks library gives IDs and codes and never display text (SPEC-unify §3), so every ID and code it can
/// produce needs a label here in both languages. These tests list the registry and the code tables and fail on the first one that has no
/// label: a new item ID, reason, note, outcome or evidence code cannot be added without its words (like <see cref="GuiStringsTests"/> does for
/// the rest of the screen). The labels are written out one by one in CheckStrings on purpose; nothing here makes one up.
/// </summary>
public class CheckStringsTests
{
    private static readonly Dictionary<string, StringEntry> ByKey = CheckStrings.All
        .GroupBy(static e => e.Key, StringComparer.Ordinal)
        .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);

    private static string Snake(string name) => CheckFormat.Snake(name);

    /// <summary>The keys that have no usable text in one of the two languages: absent, empty, or the "[key]" the table answers for a key it does not know.</summary>
    private static string[] Unlabelled(IEnumerable<string> keys)
    {
        var missing = new List<string>();
        foreach (var key in keys)
        {
            if (!ByKey.TryGetValue(key, out var entry) || !HasText(entry.Ja, key) || !HasText(entry.En, key))
            {
                missing.Add(key);
            }
        }

        return [.. missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static bool HasText(string text, string key) => !string.IsNullOrWhiteSpace(text) && text != "[" + key + "]";

    private static IEnumerable<string> Members<T>(string prefix)
        where T : struct, Enum =>
        Enum.GetValues<T>().Select(member => prefix + Snake(member.ToString()));

    [Fact]
    public void Every_item_id_of_the_registry_has_a_label_in_both_languages()
    {
        Assert.NotEmpty(CheckItemRegistry.All);
        var keys = CheckItemRegistry.All.Select(static spec => CheckStrings.ItemPrefix + spec.Id).ToArray();

        Assert.Empty(Unlabelled(keys));
    }

    [Fact]
    public void The_item_labels_are_found_through_the_string_table_in_both_languages()
    {
        using var _ = Harness.Locale();
        foreach (var spec in CheckItemRegistry.All)
        {
            var key = CheckStrings.ItemPrefix + spec.Id;
            foreach (var language in new[] { Language.Ja, Language.En })
            {
                var text = Strings.Get(key, language);
                Assert.True(text.Length > 0 && text != "[" + key + "]", $"No {language} label for item '{spec.Id}'.");
            }
        }
    }

    [Fact]
    public void Every_reason_outcome_kind_verdict_and_evidence_code_has_words_in_both_languages()
    {
        var keys = Members<CheckReason>("gui.chk.reason.")
            .Concat(Members<CheckOutcome>("gui.chk.outcome."))
            .Concat(Members<CheckKind>("gui.chk.kind."))
            .Concat(Members<CheckVerdict>("gui.chk.verdict."))
            .Concat(Members<ItemVerdict>("gui.chk.verdict."))
            .Concat(Members<CheckEvidenceCode>("gui.chk.ev."))
            .ToArray();

        Assert.Empty(Unlabelled(keys));
    }

    [Fact]
    public void Every_note_failure_and_warning_code_has_words_in_both_languages()
    {
        Assert.NotEmpty(CheckNotes.Known);
        Assert.NotEmpty(CheckRunFailureCodes.All);
        Assert.NotEmpty(CheckSaveFailureCodes.All);
        Assert.NotEmpty(CheckHistoryWarningCodes.All);

        var keys = CheckNotes.Known.Select(static code => "gui.chk.note." + code)
            .Concat(CheckRunFailureCodes.All.Select(static code => "gui.chk.fail." + code))
            .Concat(CheckSaveFailureCodes.All.Select(static code => "gui.chk.savefail." + code))
            .Concat(CheckHistoryWarningCodes.All.Select(static code => "gui.chk.warn." + code))
            .Concat(CheckHistoryWarningCodes.All.Select(static code => "gui.chk.warnone." + code))
            .Concat(["gui.chk.note.unknown", "gui.chk.fail.unknown", "gui.chk.savefail.unknown", "gui.chk.warn.unknown", "gui.chk.warnone.unknown"])
            .ToArray();

        Assert.Empty(Unlabelled(keys));
    }

    [Fact]
    public void Every_text_value_unit_and_subject_the_registry_allows_has_words_in_both_languages()
    {
        var texts = CheckItemRegistry.All
            .SelectMany(static spec => spec.AllowedText ?? [])
            .Select(static value => "gui.chk.text." + value);
        var subjects = CheckItemRegistry.All
            .Select(static spec => SubjectKinds.Prefix(spec.Subject))
            .OfType<string>()
            .Select(static prefix => "gui.chk.subject." + prefix);
        var units = new List<string>();
        foreach (var unit in CheckItemRegistry.All.Select(static spec => spec.Unit).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var key = CheckFormat.UnitKey(unit);
            Assert.True(key is not null, $"No template for the unit '{unit}'.");
            units.Add(key);
        }

        Assert.Empty(Unlabelled(texts.Concat(subjects).Concat(units)));
    }

    [Fact]
    public void Every_state_and_stage_the_page_can_show_has_words_in_both_languages()
    {
        string[] keys =
        [
            "gui.chk.state.notrun",
            "gui.chk.state.running",
            "gui.chk.state.stopping",
            "gui.chk.state.completed",
            "gui.chk.state.stopped",
            "gui.chk.state.failed",
            "gui.chk.state.savefailed",
            "gui.chk.load.stage.collecting",
            "gui.chk.load.stage.judging",
            "gui.chk.load.stage.saving",
            "gui.chk.close.title",
            "gui.chk.close.message",
            "gui.chk.close.yes",
            "gui.chk.tab.system",
            "gui.chk.tab.load",
            "gui.chk.tab.storage",
            "gui.chk.tab.history",
            "gui.nav.checks",
        ];

        Assert.Empty(Unlabelled(keys));
    }

    [Fact]
    public void Every_key_of_the_table_is_registered_unique_and_belongs_to_the_checks_page()
    {
        var registered = GuiStrings.All.Select(static e => e.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(CheckStrings.All.Length, ByKey.Count);
        foreach (var entry in CheckStrings.All)
        {
            Assert.True(entry.Key.StartsWith("gui.chk.", StringComparison.Ordinal) || entry.Key == "gui.nav.checks", $"'{entry.Key}' is not a key of the checks page.");
            Assert.Contains(entry.Key, registered);
        }
    }

    [Fact]
    public void No_item_label_is_left_for_an_id_the_registry_does_not_have()
    {
        var orphans = CheckStrings.All
            .Select(static e => e.Key)
            .Where(static key => key.StartsWith(CheckStrings.ItemPrefix, StringComparison.Ordinal))
            .Where(static key => !CheckItemRegistry.IsKnown(key[CheckStrings.ItemPrefix.Length..]))
            .ToArray();

        Assert.Empty(orphans);
    }

    [Fact]
    public void An_id_or_code_this_version_has_no_words_for_shows_as_itself_and_never_as_a_bracketed_key()
    {
        using var _ = Harness.Locale();

        Assert.Equal("system.from_a_later_version", CheckFormat.ItemLabel("system.from_a_later_version"));
        Assert.Contains("storage.from_a_later_version", CheckFormat.NoteText("storage.from_a_later_version"), StringComparison.Ordinal);
        Assert.Equal(Strings.Get("gui.chk.fail.unknown"), CheckFormat.FailureText("from_a_later_version"));
        Assert.Equal(Strings.Get("gui.chk.savefail.unknown"), CheckFormat.SaveFailureText("from_a_later_version"));
        Assert.Equal(Strings.Get("gui.chk.warnone.unknown"), CheckFormat.WarningOneText("from_a_later_version"));
        Assert.Contains("from_a_later_version", CheckFormat.WarningText(new CheckHistoryWarning("from_a_later_version", 2)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_words_are_the_four_of_the_spec_and_the_observation_has_its_own_word()
    {
        using var _ = Harness.Locale(Language.En);
        Assert.Equal("OK", CheckFormat.VerdictWord(CheckVerdict.Ok));
        Assert.Equal("Caution", CheckFormat.VerdictWord(CheckVerdict.Caution));
        Assert.Equal("Needs review", CheckFormat.VerdictWord(CheckVerdict.Check));
        Assert.Equal("Not measured", CheckFormat.VerdictWord(CheckVerdict.Unavailable));
        Assert.Equal("Info", CheckFormat.VerdictWord(ItemVerdict.Info));

        Strings.Current = Language.Ja;
        Assert.Equal("OK", CheckFormat.VerdictWord(CheckVerdict.Ok));
        Assert.Equal("注意", CheckFormat.VerdictWord(CheckVerdict.Caution));
        Assert.Equal("要確認", CheckFormat.VerdictWord(CheckVerdict.Check));
        Assert.Equal("測れず", CheckFormat.VerdictWord(CheckVerdict.Unavailable));
        Assert.Equal("参考", CheckFormat.VerdictWord(ItemVerdict.Info));
    }
}
