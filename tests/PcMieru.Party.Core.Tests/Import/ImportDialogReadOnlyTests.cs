using PcMieru.Party.I18n;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>The message for an unreadable new save offers Retry, Open without saving and Close, and says what the read-only open means.</summary>
[Collection("Strings.Lang")]
public class ImportDialogReadOnlyTests
{
    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void The_message_of_an_unreadable_destination_offers_retry_open_read_only_and_close(string lang)
    {
        var before = Strings.Lang;
        Strings.Lang = lang;
        try
        {
            var model = ImportDialogModels.Problem(new ProblemView(ImportOutcome.DestinationBlocked, ImportReason.DestinationBroken, null));

            Assert.Equal(
                new[] { ImportDialogResult.Retry, ImportDialogResult.OpenReadOnly, ImportDialogResult.Close },
                model.Buttons.Select(b => b.Result).ToArray());
            Assert.Equal(ImportDialogResult.Close, model.CancelResult);                  // Escape and the cross never open it
            Assert.Equal(ImportDialogResult.Retry, model.Buttons.Single(b => b.IsDefault).Result);
            Assert.All(model.Buttons, b => Assert.False(string.IsNullOrWhiteSpace(b.Text)));
            Assert.False(string.IsNullOrWhiteSpace(model.Note));
            Assert.Contains(lang == "ja" ? "保存" : "save", model.Note!);
        }
        finally
        {
            Strings.Lang = before;
        }
    }

    [Fact]
    public void Other_messages_do_not_offer_to_open_read_only()
    {
        foreach (var outcome in new[] { ImportOutcome.OldAppRunning, ImportOutcome.Hold, ImportOutcome.Busy, ImportOutcome.Failed, ImportOutcome.SourceChanged })
        {
            var model = ImportDialogModels.Problem(new ProblemView(outcome, ImportReason.InternalError, null));
            Assert.DoesNotContain(model.Buttons, b => b.Result == ImportDialogResult.OpenReadOnly);
            Assert.Null(model.Note);
        }
    }
}
