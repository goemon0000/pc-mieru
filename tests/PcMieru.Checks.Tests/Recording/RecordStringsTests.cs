using PcMieru.Core;
using static PcMieru.Checks.Tests.SeriesKit;

namespace PcMieru.Checks.Tests;

/// <summary>
/// The texts of the recording (the Strings pattern of the other areas): every code the recorder, the store and the summary can produce has a ja and
/// an en text with the same placeholders, and a sentence built from any note leaves no <c>{name}</c> behind.
/// </summary>
public class RecordStringsTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En];

    // The values NoteArgs gives a note's sentence; a text may leave one out but must not ask for another.
    private static readonly string[] NoteGiven = ["subject", "detail", "a", "b"];

    private static StringEntry[] Entries()
    {
        Strings.Register(RecordStrings.All);
        return RecordStrings.All;
    }

    private static bool Has(string key, Language language)
    {
        Strings.Register(RecordStrings.All);
        var text = Strings.Get(key, language);
        return !string.IsNullOrWhiteSpace(text) && text != "[" + key + "]";
    }

    private static List<string> MissingKeys(IEnumerable<string> keys)
    {
        var missing = new List<string>();
        foreach (var key in keys)
        {
            foreach (var language in Languages)
            {
                if (!Has(key, language))
                {
                    missing.Add($"{key} ({language})");
                }
            }
        }

        return missing;
    }

    [Fact]
    public void Every_entry_has_a_ja_and_an_en_text_and_a_gui_rec_key()
    {
        var entries = Entries();
        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.StartsWith("gui.rec.", e.Key, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(e.Ja), e.Key + " has no ja text");
            Assert.False(string.IsNullOrWhiteSpace(e.En), e.Key + " has no en text");
        });
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = Entries().Select(e => e.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ja_and_en_use_the_same_placeholders()
    {
        var problems = new List<string>();
        foreach (var entry in Entries())
        {
            var ja = Strings.Placeholders(entry.Ja);
            var en = Strings.Placeholders(entry.En);
            if (!ja.SequenceEqual(en, StringComparer.Ordinal))
            {
                problems.Add($"{entry.Key}: ja [{string.Join(",", ja)}] en [{string.Join(",", en)}]");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void A_note_text_asks_only_for_values_NoteArgs_gives()
    {
        var problems = new List<string>();
        foreach (var code in RecordStrings.NoteCodes)
        {
            foreach (var language in Languages)
            {
                var asked = Strings.Placeholders(Strings.Get(RecordStrings.NotePrefix + code, language));
                if (!asked.All(NoteGiven.Contains))
                {
                    problems.Add($"{code} ({language}): asks for [{string.Join(",", asked)}]");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Every_enum_value_has_a_text_in_both_languages()
    {
        var keys = new List<string>();
        keys.AddRange(Enum.GetValues<RunStatus>().Select(RecordStrings.KeyOf));
        keys.AddRange(Enum.GetValues<RunStopReason>().Select(RecordStrings.KeyOf));
        keys.AddRange(Enum.GetValues<MarkerKind>().Select(RecordStrings.KeyOf));
        keys.AddRange(Enum.GetValues<RunActivityState>().Select(RecordStrings.KeyOf));
        keys.AddRange(Enum.GetValues<RunStartFailure>().Select(RecordStrings.KeyOf));
        keys.AddRange(Enum.GetValues<RunDeleteOutcome>().Select(RecordStrings.KeyOf));
        Assert.Empty(MissingKeys(keys));
    }

    [Fact]
    public void Every_note_problem_state_reason_and_limit_code_has_a_text()
    {
        var keys = new List<string>();
        keys.AddRange(RecordStrings.NoteCodes.Select(c => RecordStrings.NotePrefix + c));
        keys.AddRange(RecordStrings.ProblemCodes.Select(RecordStrings.ProblemKey));
        keys.AddRange(new[] { RunCodes.StateFresh, RunCodes.StateRepeat, RunCodes.StateMissing }.Select(RecordStrings.StateKey));
        keys.AddRange(RunCodes.Reasons.Select(RecordStrings.ReasonKey));
        keys.AddRange(RunLimitBits.All.Select(e => RecordStrings.LimitKey(e.Name)));
        keys.Add(RecordStrings.LimitKey(RunLimitBits.UnknownName));
        keys.Add("gui.rec.fresh.exact");
        keys.Add("gui.rec.fresh.legacy_estimated");
        keys.Add(RecordStrings.SectionPrefix + "bench");
        keys.Add(RecordStrings.SectionPrefix + "stress");
        Assert.Empty(MissingKeys(keys));
    }

    [Fact]
    public void Every_column_of_the_vocabulary_has_a_label()
    {
        var defs = RunMetrics.CoreOnly.Concat([RunMetrics.GpuUtil, RunMetrics.GpuVram]).Concat(RunMetrics.GpuVendor);
        Assert.Empty(MissingKeys(defs.Select(d => RecordStrings.MetricPrefix + d.Name)));
    }

    [Fact]
    public void A_sentence_from_any_note_leaves_no_placeholder_behind()
    {
        Strings.Register(RecordStrings.All);
        var problems = new List<string>();
        foreach (var code in RecordStrings.NoteCodes)
        {
            // Each kind of subject and detail the summarizer writes: a status and its stop reason, a marker, a device, a metric and the reason codes, a "a/b" pair.
            var notes = new[]
            {
                new RunNote(code, "", "3"),
                new RunNote(code, "interrupted", "io_error"),
                new RunNote(code, "bench-start", ""),
                new RunNote(code, "gpu1", "12/34"),
                new RunNote(code, "gpu1.temp_c", "host_restart,read_failed"),
                new RunNote(code, "cpu", "1500/3500"),
            };
            if (code == "no-data")
            {
                // The summarizer writes a metric key and the reason codes of the gaps (RunCodes.Reasons), nothing else.
                notes =
                [
                    new RunNote(code, "gpu1.temp_c", "host_restart,read_failed"),
                    new RunNote(code, "core.cpu_usage_pct", "not_started"),
                    new RunNote(code, "gpu1.power_w", ""),
                ];
            }

            foreach (var note in notes)
            {
                foreach (var language in Languages)
                {
                    var text = RecordStrings.NoteText(note, language);
                    // {a} and {b} are filled only from a detail written "a/b"; for any other detail they may stay (the note is not written that way).
                    var left = Strings.Placeholders(text).Where(p => !(p is "a" or "b") || note.Detail.Contains('/', StringComparison.Ordinal));
                    if (left.Any())
                    {
                        problems.Add($"{code} ({language}) left a placeholder: {text}");
                    }

                    if (text.StartsWith("[gui.", StringComparison.Ordinal) || text.Contains("[gui.rec.", StringComparison.Ordinal))
                    {
                        problems.Add($"{code} ({language}) has an unknown key inside: {text}");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void The_codes_in_a_notes_words_are_the_words_not_the_codes()
    {
        Strings.Register(RecordStrings.All);
        var ja = RecordStrings.NoteText(new RunNote("not-completed", "interrupted", "io_error"), Language.Ja);
        var en = RecordStrings.NoteText(new RunNote("not-completed", "interrupted", "io_error"), Language.En);
        Assert.Contains("途中で中断", ja, StringComparison.Ordinal);
        Assert.Contains("書き込みに失敗した", ja, StringComparison.Ordinal);
        Assert.Contains("Interrupted", en, StringComparison.Ordinal);
        Assert.Contains("Writing failed", en, StringComparison.Ordinal);
        Assert.DoesNotContain("io_error", en, StringComparison.Ordinal);

        var gap = RecordStrings.NoteText(new RunNote("no-data", "gpu1.temp_c", "host_restart,read_failed"), Language.En);
        Assert.Contains("GPU 1 temperature", gap, StringComparison.Ordinal);
        Assert.Contains("The sensor helper restarted, The read failed", gap, StringComparison.Ordinal);
    }

    [Fact]
    public void A_metric_label_puts_the_device_first_and_an_unknown_key_stays_as_it_is()
    {
        Strings.Register(RecordStrings.All);
        Assert.Equal("CPU usage", RecordStrings.MetricLabel("core.cpu_usage_pct", Language.En));
        Assert.Equal("GPU 2 usage", RecordStrings.MetricLabel("core.gpu2_util_pct", Language.En));
        Assert.Equal("GPU 1 temperature", RecordStrings.MetricLabel("gpu1.temp_c", Language.En));
        Assert.Equal("gpu9.future_metric", RecordStrings.MetricLabel("gpu9.future_metric", Language.En));
        Assert.Equal("nodot", RecordStrings.MetricLabel("nodot", Language.En));
    }

    [Fact]
    public void Every_note_code_a_summary_gives_is_one_the_table_lists()
    {
        // The summaries below between them make most of the notes; none may use a code that has no text.
        var series = new List<RunSeries>
        {
            new RunSeries { EndUs = 5_000_000, NormalEnd = true, DroppedTicks = 2, Rows = 3 },
            Series(
                40,
                RunStatus.Interrupted,
                RunStopReason.Unknown,
                markers: [Marker(MarkerKind.BenchStart, 3)],
                ("core.cpu_usage_pct", t => t < 20 ? double.NaN : 1),
                ("core.ac_connected", t => t < 10 ? 1 : 0),
                ("core.gpu1_util_pct", _ => 0)),
            ThermalRamp(clock: t => 2800 - (t * 2)),
        };
        var known = RecordStrings.NoteCodes.ToHashSet(StringComparer.Ordinal);
        foreach (var s in series)
        {
            var summary = Summarize(s);
            Assert.All(summary.Notes, n => Assert.Contains(n.Code, known));
            Assert.All(summary.Notes, n => Assert.False(RecordStrings.NoteText(n, Language.En).StartsWith("[gui.", StringComparison.Ordinal)));
        }
    }
}
