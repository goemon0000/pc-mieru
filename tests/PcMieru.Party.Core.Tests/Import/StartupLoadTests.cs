using System.Text;
using PcMieru.Party.Game;
using PcMieru.Party.Import;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>codex review of C22, items 1 and 3: an unreadable new save is never loaded for writing; a save that is gone at load time sends the start back to the first-start flow.</summary>
public class StartupLoadTests
{
    private sealed class Script : IFirstStartPrompts
    {
        public Queue<ContinueChoice> Continues { get; } = new();
        public Queue<ProblemChoice> Problems { get; } = new();
        public List<ImportPlan> Asked { get; } = [];
        public List<ProblemView> Shown { get; } = [];

        public ContinueChoice AskContinue(ImportPlan plan)
        {
            Asked.Add(plan);
            return Continues.Dequeue();
        }

        public bool ConfirmStartFresh() => throw new InvalidOperationException("not asked in these tests");

        public ProblemChoice ShowProblem(ProblemView problem)
        {
            Shown.Add(problem);
            return Problems.Dequeue();
        }
    }

    private static readonly byte[] Junk = Encoding.UTF8.GetBytes("{ this is not a save");

    // ---- item 1: an unreadable destination ----

    [Fact]
    public void An_unreadable_new_save_shows_its_reason_and_Close_ends_without_touching_it()
    {
        using var bed = new ImportBed();
        bed.WriteDestination(Junk);
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.Close);

        Assert.Null(StartupLoad.Run(bed.Engine, script, bed.DestinationDir));

        var shown = Assert.Single(script.Shown);
        Assert.Equal(ImportOutcome.DestinationBlocked, shown.Outcome);
        Assert.Equal(ImportReason.DestinationBroken, shown.Reason);
        Assert.True(shown.CanOpenReadOnly);
        Assert.False(shown.CanStartFresh);
        Assert.Equal(Junk, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void Going_on_over_an_unreadable_new_save_loads_it_read_only_and_leaves_the_file_exactly_where_it_is()
    {
        using var bed = new ImportBed();
        bed.WriteDestination(Junk);
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.OpenReadOnly);

        var state = StartupLoad.Run(bed.Engine, script, bed.DestinationDir);

        Assert.NotNull(state);
        Assert.True(state!.SaveBlocked);
        Assert.Equal(Junk, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());   // no state.json.bad-<UTC>, no fresh file
        state.Save();                                                   // the guard of Save: nothing is written
        Assert.Equal(Junk, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void Retry_on_an_unreadable_new_save_looks_again_and_the_default_load_is_never_reached_for_writing()
    {
        using var bed = new ImportBed();
        bed.WriteDestination(Junk);
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.Retry);
        script.Problems.Enqueue(ProblemChoice.OpenReadOnly);

        var state = StartupLoad.Run(bed.Engine, script, bed.DestinationDir);

        Assert.Equal(2, script.Shown.Count);
        Assert.True(state!.SaveBlocked);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void Open_read_only_is_not_honoured_on_a_message_that_does_not_offer_it()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, ImportEnginePrepareTests.Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.OpenReadOnly);
        script.Problems.Enqueue(ProblemChoice.Close);

        Assert.Null(StartupLoad.Run(bed.Engine, script, bed.DestinationDir));

        Assert.Equal(2, script.Shown.Count);   // the first answer was ignored: the same message came again
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void The_read_only_load_reads_a_save_that_can_be_read_and_still_never_saves()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        byte[] before = bed.DestinationBytes();

        var state = PartyState.LoadReadOnly(bed.DestinationDir);

        Assert.True(state.SaveBlocked);
        state.Save();
        Assert.Equal(before, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void The_read_only_load_of_a_missing_save_is_an_empty_state_that_does_not_create_the_file()
    {
        using var bed = new ImportBed();

        var state = PartyState.LoadReadOnly(bed.DestinationDir);

        Assert.True(state.SaveBlocked);
        state.Save();
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    // ---- item 3: the save is gone at load time ----

    [Fact]
    public void A_readable_save_is_loaded_after_one_round_and_is_writable()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        int rounds = 0;

        var state = StartupLoad.Run(bed.Engine, new Script(), bed.DestinationDir, n => rounds = n);

        Assert.NotNull(state);
        Assert.False(state!.SaveBlocked);
        Assert.Equal(1, rounds);
    }

    [Fact]
    public void A_save_that_is_gone_at_load_time_sends_the_start_back_to_the_flow_not_to_an_empty_writable_state()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);   // round 1: AlreadyHave
        int rounds = 0;

        var state = StartupLoad.Run(bed.Engine, new Script(), bed.DestinationDir, n =>
        {
            rounds = n;
            if (n == 1) File.Delete(bed.Destination);   // gone between the flow's verdict and the load
        });

        Assert.Equal(2, rounds);   // the flow ran again: no old save, so the engine wrote the first file
        Assert.NotNull(state);
        Assert.False(state!.SaveBlocked);
        Assert.True(File.Exists(bed.Destination));
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_save_that_is_gone_at_load_time_asks_about_the_old_save_again_when_there_is_one()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        bed.WriteSource(ImportOrigin.PartsLife, ImportEnginePrepareTests.Save(111.5));
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.Continue);

        var state = StartupLoad.Run(bed.Engine, script, bed.DestinationDir, n =>
        {
            if (n == 1) File.Delete(bed.Destination);
        });

        Assert.Single(script.Asked);   // the question of the first start, not a silent empty start
        Assert.False(state!.SaveBlocked);
        Assert.True(File.Exists(bed.Destination));
    }

    [Fact]
    public void A_save_that_keeps_going_away_ends_read_only_after_a_few_rounds_never_writable()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        int rounds = 0;

        var state = StartupLoad.Run(bed.Engine, new Script(), bed.DestinationDir, n =>
        {
            rounds = n;
            File.Delete(bed.Destination);
        });

        Assert.Equal(StartupLoad.MaxRounds, rounds);
        Assert.True(state!.SaveBlocked);
        Assert.False(File.Exists(bed.Destination));
    }
}
