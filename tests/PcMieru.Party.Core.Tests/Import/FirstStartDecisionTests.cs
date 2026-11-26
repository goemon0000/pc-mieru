using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

public class FirstStartDecisionTests
{
    private static readonly Dictionary<ImportOutcome, FirstStartStep> Expected = new()
    {
        [ImportOutcome.AlreadyHave] = FirstStartStep.Load,
        [ImportOutcome.NoCandidate] = FirstStartStep.StartFreshThenLoad,
        [ImportOutcome.Ready] = FirstStartStep.Ask,
        [ImportOutcome.NeedsRepair] = FirstStartStep.Ask,
        // An unreadable destination is never covered by a fresh start: it is loaded as it is (PartyState.Load keeps it and blocks saving).
        [ImportOutcome.DestinationBlocked] = FirstStartStep.Load,
        [ImportOutcome.OldAppRunning] = FirstStartStep.Problem,
        [ImportOutcome.SourceUnreadable] = FirstStartStep.Problem,
        [ImportOutcome.SourceChanged] = FirstStartStep.Problem,
        [ImportOutcome.Busy] = FirstStartStep.Problem,
        [ImportOutcome.Hold] = FirstStartStep.Problem,
        [ImportOutcome.Failed] = FirstStartStep.Problem,
        // Not answers of Prepare: nothing was proven about the old save, so they never start anything.
        [ImportOutcome.Imported] = FirstStartStep.Problem,
        [ImportOutcome.StartedFresh] = FirstStartStep.Problem,
        [ImportOutcome.NeedsConsent] = FirstStartStep.Problem,
    };

    public static TheoryData<ImportOutcome> AllOutcomes() => new(Enum.GetValues<ImportOutcome>());

    [Fact]
    public void The_table_names_every_outcome_so_a_new_one_cannot_be_left_out()
    {
        Assert.Equal(Enum.GetValues<ImportOutcome>().OrderBy(o => o), Expected.Keys.OrderBy(o => o));
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void Every_outcome_has_its_step(ImportOutcome outcome)
    {
        Assert.Equal(Expected[outcome], FirstStartDecision.Next(outcome));
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void Only_no_candidate_starts_fresh_by_itself(ImportOutcome outcome)
    {
        Assert.Equal(outcome == ImportOutcome.NoCandidate, FirstStartDecision.Next(outcome) == FirstStartStep.StartFreshThenLoad);
    }

    [Theory]
    [InlineData(ImportOutcome.Imported, FirstStartAfter.Load)]
    [InlineData(ImportOutcome.StartedFresh, FirstStartAfter.Load)]
    [InlineData(ImportOutcome.AlreadyHave, FirstStartAfter.Load)]
    [InlineData(ImportOutcome.DestinationBlocked, FirstStartAfter.Load)]
    [InlineData(ImportOutcome.SourceChanged, FirstStartAfter.PrepareAgain)]
    [InlineData(ImportOutcome.OldAppRunning, FirstStartAfter.PrepareAgain)]
    [InlineData(ImportOutcome.NeedsConsent, FirstStartAfter.PrepareAgain)]
    [InlineData(ImportOutcome.Busy, FirstStartAfter.Problem)]
    [InlineData(ImportOutcome.Failed, FirstStartAfter.Problem)]
    [InlineData(ImportOutcome.Hold, FirstStartAfter.Problem)]
    [InlineData(ImportOutcome.SourceUnreadable, FirstStartAfter.Problem)]
    public void What_follows_a_write(ImportOutcome outcome, FirstStartAfter expected)
    {
        Assert.Equal(expected, FirstStartDecision.AfterWrite(outcome));
    }

    [Theory]
    [MemberData(nameof(AllOutcomes))]
    public void Only_a_hold_offers_start_fresh_next_to_retry_and_close(ImportOutcome outcome)
    {
        Assert.Equal(outcome == ImportOutcome.Hold, FirstStartDecision.OffersStartFresh(outcome));
        Assert.Equal(outcome == ImportOutcome.Hold, new ProblemView(outcome, "x", null).CanStartFresh);
    }
}
