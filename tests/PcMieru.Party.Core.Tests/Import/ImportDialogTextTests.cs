using System.Reflection;
using PcMieru.Party.I18n;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary><c>Strings.Lang</c> is one static for the whole process: every test that sets it runs alone and puts it back.</summary>
[CollectionDefinition("Strings.Lang", DisableParallelization = true)]
public class StringsLangCollection;

[Collection("Strings.Lang")]
public class ImportDialogTextTests
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

    private static string[] AllReasonCodes() =>
        typeof(ImportReason).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void The_reflection_finds_the_reason_codes()
    {
        var codes = AllReasonCodes();
        Assert.True(codes.Length >= 30, "found only " + codes.Length);
        Assert.Contains(ImportReason.OldAppRunning, codes);
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Every_reason_code_has_a_sentence_in_both_languages(string lang)
    {
        InLanguage(lang, () =>
        {
            foreach (var code in AllReasonCodes())
            {
                var text = Strings.ImportReasonTextOrNull(code);
                Assert.False(string.IsNullOrWhiteSpace(text), $"no {lang} sentence for reason code '{code}'");
                Assert.DoesNotContain(code, text!, StringComparison.Ordinal);   // the code is for logs, never the sentence
                if (lang == "en") Assert.All(text!, c => Assert.True(c < 128, $"non-ASCII in the en sentence of '{code}'"));
                else Assert.Contains(text!, c => c >= 0x3000);
            }
        });
    }

    [Fact]
    public void The_two_languages_differ_for_every_code()
    {
        var ja = new Dictionary<string, string>();
        InLanguage("ja", () => { foreach (var c in AllReasonCodes()) ja[c] = Strings.ImportReasonText(c); });
        InLanguage("en", () => { foreach (var c in AllReasonCodes()) Assert.NotEqual(ja[c], Strings.ImportReasonText(c)); });
    }

    [Fact]
    public void The_old_app_sentence_is_the_agreed_one()
    {
        InLanguage("ja", () => Assert.Equal("パーツぐらし（旧版）を終了してから、もう一度お試しください", Strings.ImportReasonText(ImportReason.OldAppRunning)));
        InLanguage("en", () => Assert.Equal("Close the old Parts Life, then try again", Strings.ImportReasonText(ImportReason.OldAppRunning)));
    }

    [Fact]
    public void An_unknown_code_has_no_specific_sentence_but_still_gets_a_generic_one()
    {
        InLanguage("en", () =>
        {
            Assert.Null(Strings.ImportReasonTextOrNull("a-code-from-the-future"));
            Assert.False(string.IsNullOrWhiteSpace(Strings.ImportReasonText("a-code-from-the-future")));
        });
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Every_outcome_has_a_message_and_only_a_hold_says_the_progress_cannot_come_over(string lang)
    {
        InLanguage(lang, () =>
        {
            foreach (var outcome in Enum.GetValues<ImportOutcome>())
            {
                var message = Strings.ImportMessage(outcome, ImportReason.WriteFailed);
                Assert.False(string.IsNullOrWhiteSpace(message));
                Assert.Equal(outcome == ImportOutcome.Hold, message.StartsWith(Strings.ImportHoldPrefix, StringComparison.Ordinal));
            }
        });
    }

    [Fact]
    public void The_buttons_and_the_confirmation_say_what_was_agreed()
    {
        InLanguage("ja", () =>
        {
            Assert.Equal("引き継ぐ", Strings.ImportContinue);
            Assert.Equal("あとで", Strings.ImportLater);
            Assert.Equal("新しく始める", Strings.ImportStartFresh);
            Assert.Equal("もう一度", Strings.ImportRetry);
            Assert.Equal("閉じる", Strings.ImportCloseButton);
            Assert.Equal("パーツぐらしの続き", Strings.ImportTitle);
            Assert.Contains("旧パーツぐらしの続きはこのあと引き継げなくなります", Strings.ImportFreshAsk);
            Assert.Contains("直せなかった欄は初期値になります", Strings.ImportRepairNote);
        });
        InLanguage("en", () =>
        {
            Assert.Equal("Continue", Strings.ImportContinue);
            Assert.Equal("Later", Strings.ImportLater);
            Assert.Equal("Start fresh", Strings.ImportStartFresh);
            Assert.Equal("Retry", Strings.ImportRetry);
            Assert.Equal("Close", Strings.ImportCloseButton);
            Assert.Equal("Continue Parts Life", Strings.ImportTitle);
            Assert.Contains("old progress cannot be brought over afterwards", Strings.ImportFreshAsk);
            Assert.Contains("start from their defaults", Strings.ImportRepairNote);
        });
    }
}

[Collection("Strings.Lang")]
public class ImportDialogModelTests
{
    private static ImportPlan Plan(ImportOutcome status, ImportOrigin origin, params string[] repaired) => new()
    {
        Status = status,
        Reason = ImportReason.Accept,
        Candidate = new ImportCandidate(origin, "label-only"),
        RepairedFields = repaired,
    };

    private static void Ja(Action body)
    {
        var before = Strings.Lang;
        Strings.Lang = "ja";
        try
        {
            body();
        }
        finally
        {
            Strings.Lang = before;
        }
    }

    [Fact]
    public void The_question_has_Continue_as_the_default_Later_as_the_cancel_and_Start_fresh()
    {
        Ja(() =>
        {
            var m = ImportDialogModels.Ask(Plan(ImportOutcome.Ready, ImportOrigin.PartsLife));
            Assert.Equal(new[] { ImportDialogResult.Continue, ImportDialogResult.Later, ImportDialogResult.StartFresh }, m.Buttons.Select(b => b.Result));
            Assert.Equal(ImportDialogResult.Continue, m.Buttons.Single(b => b.IsDefault).Result);
            Assert.Equal(ImportDialogResult.Later, m.CancelResult);
            Assert.Empty(m.List);
            Assert.Null(m.ListHeader);
            Assert.Contains("PartsLife", m.Body);
            Assert.DoesNotContain("PartsParty", m.Body);
        });
    }

    [Fact]
    public void A_repair_lists_the_fields_before_the_buttons_and_says_they_start_from_their_defaults()
    {
        Ja(() =>
        {
            var m = ImportDialogModels.Ask(Plan(ImportOutcome.NeedsRepair, ImportOrigin.PartsParty, "Adventure.Gold", "Levels.Cpu"));
            Assert.Equal(new[] { "Adventure.Gold", "Levels.Cpu" }, m.List);
            Assert.NotNull(m.ListHeader);
            Assert.Contains("直せなかった欄は初期値になります", m.Note);
            Assert.Contains("PartsParty", m.Body);
        });
    }

    [Fact]
    public void A_message_has_Retry_as_the_default_and_Close_as_the_cancel()
    {
        Ja(() =>
        {
            var m = ImportDialogModels.Problem(new ProblemView(ImportOutcome.OldAppRunning, ImportReason.OldAppRunning, null));
            Assert.Equal(new[] { ImportDialogResult.Retry, ImportDialogResult.Close }, m.Buttons.Select(b => b.Result));
            Assert.Equal(ImportDialogResult.Retry, m.Buttons.Single(b => b.IsDefault).Result);
            Assert.Equal(ImportDialogResult.Close, m.CancelResult);
            Assert.Equal("パーツぐらし（旧版）を終了してから、もう一度お試しください", m.Body);
        });
    }

    [Fact]
    public void A_hold_message_also_offers_Start_fresh_between_Retry_and_Close()
    {
        Ja(() =>
        {
            var m = ImportDialogModels.Problem(new ProblemView(ImportOutcome.Hold, ImportReason.VersionFuture, null));
            Assert.Equal(new[] { ImportDialogResult.Retry, ImportDialogResult.StartFresh, ImportDialogResult.Close }, m.Buttons.Select(b => b.Result));
        });
    }

    [Theory]
    [InlineData("Ready", 3)]
    [InlineData("Repair", 3)]
    [InlineData("OldApp", 2)]
    [InlineData("Hold", 3)]
    public void Every_preview_case_makes_a_dialog_without_an_engine(string name, int buttons)
    {
        var which = Enum.Parse<PartyImportPreview>(name);
        Ja(() =>
        {
            var m = ImportDialogModels.Preview(which);
            Assert.Equal(buttons, m.Buttons.Count);
            Assert.False(string.IsNullOrWhiteSpace(m.Body));
            Assert.Equal(which == PartyImportPreview.Repair, m.List.Count > 0);
        });
    }

    [Fact]
    public void The_preview_cases_are_exactly_the_five_of_the_command_line()
    {
        Assert.Equal(5, Enum.GetValues<PartyImportPreview>().Length);
    }

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

    [Fact]
    public void The_sign_in_question_says_what_was_agreed_in_both_languages()
    {
        InLanguage("ja", () =>
        {
            var model = ImportDialogModels.RunSwitchAsk();
            Assert.Equal("ログオン時の起動も、パーツぐらし（PCミエル）に切り替えますか？", model.Body);
            Assert.Equal(["切り替える", "今はしない"], model.Buttons.Select(b => b.Text));
            Assert.Contains("すでに完了", model.Note);
        });
        InLanguage("en", () =>
        {
            var model = ImportDialogModels.RunSwitchAsk();
            Assert.Equal("Switch sign-in start to Parts Life (PC Mieru) too?", model.Body);
            Assert.Equal(["Switch", "Not now"], model.Buttons.Select(b => b.Text));
            Assert.Contains("already", model.Note);
        });
    }

    [Fact]
    public void Only_the_switch_button_switches_and_the_cross_or_Escape_means_not_now()
    {
        InLanguage("en", () =>
        {
            var model = ImportDialogModels.RunSwitchAsk();
            Assert.Equal(ImportDialogResult.NotNow, model.CancelResult);
            Assert.Equal(ImportDialogResult.Switch, model.Buttons.Single(b => b.IsDefault).Result);
            Assert.Equal([ImportDialogResult.Switch, ImportDialogResult.NotNow], model.Buttons.Select(b => b.Result));
        });
    }

    [Fact]
    public void The_check_notice_is_one_line_with_Close_in_both_languages()
    {
        InLanguage("ja", () =>
        {
            var model = ImportDialogModels.RunSwitchCheck();
            Assert.Equal("ログオン設定の復元を確認してください", model.Body);
            Assert.Equal(ImportDialogResult.Close, model.CancelResult);
            Assert.Single(model.Buttons);
        });
        InLanguage("en", () =>
        {
            var model = ImportDialogModels.RunSwitchCheck();
            Assert.Equal("Please check the sign-in setting", model.Body);
            Assert.Equal(ImportDialogResult.Close, model.CancelResult);
            Assert.Single(model.Buttons);
        });
    }

    [Fact]
    public void The_preview_of_the_sign_in_question_is_the_question()
    {
        InLanguage("ja", () => Assert.Equal(ImportDialogModels.RunSwitchAsk().Body, ImportDialogModels.Preview(PartyImportPreview.RunSwitch).Body));
    }
}
