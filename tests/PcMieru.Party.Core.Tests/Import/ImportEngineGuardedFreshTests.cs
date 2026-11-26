using System.Text;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// codex review of C22, items 2, 4 and 5: the automatic first file after "no old save" proves that again under the lock; the last look before
/// the final move finds the candidate again; and the probe is the very last thing asked.
/// </summary>
public class ImportEngineGuardedFreshTests
{
    private sealed class ScriptedProbe(Func<int, LegacyRunningState> answer) : ILegacyRunningProbe
    {
        public int Calls { get; private set; }

        public LegacyRunningState Check() => answer(++Calls);
    }

    private static ImportEngine EngineWith(ImportBed bed, ILegacyRunningProbe probe) =>
        new(bed.Paths, probe, bed.LockName, TimeSpan.FromMilliseconds(500));

    private static byte[] Save(double memory) => ImportEnginePrepareTests.Save(memory);

    // ---- item 2: the automatic first file ----

    [Fact]
    public void The_automatic_first_file_is_written_when_there_is_still_no_old_save()
    {
        using var bed = new ImportBed();

        var result = bed.Engine.StartFreshIfStillNone();

        Assert.Equal(ImportOutcome.StartedFresh, result.Outcome);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
        Assert.Equal(1, bed.Probe.Calls);   // asked once: the last look
    }

    [Theory]
    [InlineData("PartsLife")]
    [InlineData("PartsParty")]
    public void An_old_save_that_appeared_after_the_plan_stops_the_automatic_first_file_and_nothing_is_left_behind(string origin)
    {
        using var bed = new ImportBed();
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.NoCandidate, plan.Status);
        var which = Enum.Parse<ImportOrigin>(origin);
        bed.Engine.BeforeMove = _ => bed.WriteSource(which, Save(111.5));   // the old product wrote its save after the plan, while the temp file was being made

        var result = bed.Engine.StartFreshIfStillNone();

        Assert.Equal(ImportOutcome.CandidateAppeared, result.Outcome);
        Assert.Equal(ImportReason.CandidateAppeared, result.Reason);
        Assert.False(File.Exists(bed.Destination));
        Assert.Empty(bed.DestinationFiles());   // no state.json, no temp file
        Assert.Equal(FirstStartAfter.PrepareAgain, FirstStartDecision.AfterWrite(result.Outcome));
    }

    [Fact]
    public void An_unreadable_old_save_that_appeared_counts_as_a_candidate_too()
    {
        using var bed = new ImportBed();
        bed.Engine.BeforeMove = _ => Directory.CreateDirectory(bed.SourcePath(ImportOrigin.PartsLife));   // a folder named state.json: it is there, it cannot be read

        var result = bed.Engine.StartFreshIfStillNone();

        Assert.Equal(ImportOutcome.CandidateAppeared, result.Outcome);
        Assert.Empty(bed.DestinationFiles());
        Directory.Delete(bed.SourcePath(ImportOrigin.PartsLife));
    }

    [Fact]
    public void The_old_product_starting_before_the_automatic_first_file_stops_it()
    {
        using var bed = new ImportBed();
        var engine = EngineWith(bed, new ScriptedProbe(_ => LegacyRunningState.Running));

        var result = engine.StartFreshIfStillNone();

        Assert.Equal(ImportOutcome.OldAppRunning, result.Outcome);
        Assert.Equal(ImportReason.OldAppRunning, result.Reason);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void The_candidates_are_looked_at_before_the_old_product_is_asked()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var engine = EngineWith(bed, new ScriptedProbe(_ => LegacyRunningState.Running));

        // Both are true: a save is there AND the old product runs. The save is the more basic fact; the plan is made again and says the rest.
        Assert.Equal(ImportOutcome.CandidateAppeared, engine.StartFreshIfStillNone().Outcome);
    }

    [Fact]
    public void The_automatic_first_file_never_touches_a_destination_that_is_there()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        byte[] valid = bed.DestinationBytes();
        Assert.Equal(ImportOutcome.AlreadyHave, bed.Engine.StartFreshIfStillNone().Outcome);
        Assert.Equal(valid, bed.DestinationBytes());

        byte[] junk = Encoding.UTF8.GetBytes("{ not a save");
        bed.WriteDestination(junk);
        var blocked = bed.Engine.StartFreshIfStillNone();
        Assert.Equal(ImportOutcome.DestinationBlocked, blocked.Outcome);
        Assert.Equal(junk, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_start_fresh_the_user_chose_is_not_stopped_by_an_old_save_it_keeps_today_s_behaviour()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;   // not even the probe is asked

        var result = bed.Engine.StartFresh();

        Assert.Equal(ImportOutcome.StartedFresh, result.Outcome);
        Assert.Equal(0, bed.Probe.Calls);
    }

    // ---- item 4: the last look finds the candidate again ----

    [Fact]
    public void A_better_candidate_that_appears_while_the_save_is_written_refuses_the_move()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        Assert.Equal(ImportOrigin.PartsParty, plan.Candidate!.Origin);
        // The plan chose PartsParty. PartsLife is first in the order, and it appears after the start of Commit, before the final move.
        bed.Engine.BeforeMove = _ => bed.WriteSource(ImportOrigin.PartsLife, Save(333.5));

        var result = bed.Engine.Commit(plan, consentToRepair: false);

        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Equal(ImportReason.SourceChanged, result.Reason);
        Assert.False(File.Exists(bed.Destination));
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void The_chosen_candidate_vanishing_before_the_move_refuses_it()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        bed.Engine.BeforeMove = _ => bed.DeleteSource(ImportOrigin.PartsLife);

        var result = bed.Engine.Commit(plan, consentToRepair: false);

        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Empty(bed.DestinationFiles());
    }

    // ---- item 5: the probe is asked after the two fingerprint reads ----

    [Fact]
    public void A_source_that_changed_is_reported_before_the_probe_is_even_asked_at_the_last_look()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        // Call 1 = Prepare, 2 = the start of Commit, 3 = the last look. Both are true at the last look: the save changed AND the old product runs.
        var probe = new ScriptedProbe(n => n >= 3 ? LegacyRunningState.Running : LegacyRunningState.NotRunning);
        var engine = EngineWith(bed, probe);
        var plan = engine.Prepare();
        engine.BeforeMove = _ => bed.RewriteInPlace(ImportOrigin.PartsLife, Save(999.5));

        var result = engine.Commit(plan, consentToRepair: false);

        // The reads come first: the changed source stops the move before the probe is asked a third time.
        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Equal(2, probe.Calls);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void The_probe_is_the_last_question_before_the_final_move_and_a_running_product_still_stops_it()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var probe = new ScriptedProbe(n => n >= 3 ? LegacyRunningState.Running : LegacyRunningState.NotRunning);
        var engine = EngineWith(bed, probe);
        var plan = engine.Prepare();

        var result = engine.Commit(plan, consentToRepair: false);

        Assert.Equal(ImportOutcome.OldAppRunning, result.Outcome);
        Assert.Equal(3, probe.Calls);
        Assert.Empty(bed.DestinationFiles());
    }
}
