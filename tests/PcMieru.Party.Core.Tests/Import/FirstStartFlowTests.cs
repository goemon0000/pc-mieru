using System.Text;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>The first start with the real engine on temp folders and a scripted person instead of windows.</summary>
public class FirstStartFlowTests
{
    private sealed class Script : IFirstStartPrompts
    {
        public Queue<ContinueChoice> Continues { get; } = new();
        public Queue<bool> Confirms { get; } = new();
        public Queue<ProblemChoice> Problems { get; } = new();
        public List<ImportPlan> Asked { get; } = [];
        public List<ProblemView> Shown { get; } = [];
        public int ConfirmCalls { get; private set; }
        public Action? OnAsk { get; set; }
        public Action? OnProblem { get; set; }

        public ContinueChoice AskContinue(ImportPlan plan)
        {
            Asked.Add(plan);
            var hook = OnAsk;
            OnAsk = null;
            hook?.Invoke();
            return Continues.Dequeue();
        }

        public bool ConfirmStartFresh()
        {
            ConfirmCalls++;
            return Confirms.Dequeue();
        }

        public ProblemChoice ShowProblem(ProblemView problem)
        {
            Shown.Add(problem);
            var hook = OnProblem;
            OnProblem = null;
            hook?.Invoke();
            return Problems.Dequeue();
        }
    }

    private static byte[] Save(double memory) => ImportEnginePrepareTests.Save(memory);

    private static byte[] BrokenGold()
    {
        var src = V1();
        src["Adventure"]!["Gold"] = "lots";
        return Bytes(src);
    }

    [Fact]
    public void No_old_save_starts_fresh_through_the_engine_without_any_question()
    {
        using var bed = new ImportBed();
        var script = new Script();

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
        Assert.Empty(script.Asked);
        Assert.Empty(script.Shown);
    }

    [Fact]
    public void A_save_that_is_already_there_is_loaded_without_a_write()
    {
        using var bed = new ImportBed();
        Assert.Equal(ImportOutcome.StartedFresh, bed.Engine.StartFresh().Outcome);
        byte[] before = bed.DestinationBytes();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var script = new Script();

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        Assert.Equal(before, bed.DestinationBytes());
        Assert.Empty(script.Asked);
    }

    [Fact]
    public void A_destination_that_cannot_be_read_is_never_a_plain_proceed_and_never_started_fresh_over()
    {
        using var bed = new ImportBed();
        byte[] junk = Encoding.UTF8.GetBytes("{ this is not a save");
        bed.WriteDestination(junk);
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.OpenReadOnly);

        // The reason is shown first; going on is a read-only proceed (the load that follows moves nothing and writes nothing).
        Assert.Equal(FirstStartEnd.ProceedReadOnly, FirstStartFlow.Run(bed.Engine, script));

        var shown = Assert.Single(script.Shown);
        Assert.Equal(ImportOutcome.DestinationBlocked, shown.Outcome);
        Assert.True(shown.CanOpenReadOnly);
        Assert.Equal(junk, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
        Assert.Empty(script.Asked);
    }

    [Fact]
    public void An_unreadable_destination_with_Close_exits()
    {
        using var bed = new ImportBed();
        bed.WriteDestination(Encoding.UTF8.GetBytes("{ this is not a save"));
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.Close);

        Assert.Equal(FirstStartEnd.Exit, FirstStartFlow.Run(bed.Engine, script));
    }

    [Fact]
    public void An_old_save_that_appears_while_the_automatic_first_file_is_written_is_asked_about_not_buried()
    {
        using var bed = new ImportBed();
        bed.Engine.BeforeMove = _ => bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));   // after "no old save", before the first file
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.Later);

        Assert.Equal(FirstStartEnd.Exit, FirstStartFlow.Run(bed.Engine, script));

        Assert.Single(script.Asked);   // the plan was made again and found the save
        Assert.False(File.Exists(bed.Destination));
    }

    [Fact]
    public void Continue_brings_a_ready_save_over_and_leaves_the_old_one_alone()
    {
        using var bed = new ImportBed();
        string source = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        byte[] old = File.ReadAllBytes(source);
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.Continue);

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        Assert.Equal(ImportOutcome.Ready, script.Asked.Single().Status);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
        Assert.Equal(old, File.ReadAllBytes(source));
    }

    [Fact]
    public void Continue_on_a_repair_is_the_consent_and_keeps_the_original_copy()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.Continue);

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        var asked = script.Asked.Single();
        Assert.Equal(ImportOutcome.NeedsRepair, asked.Status);
        Assert.NotEmpty(asked.RepairedFields);
        var files = bed.DestinationFiles();
        Assert.Equal(2, files.Length);
        Assert.Contains("state.json", files);
        Assert.Contains(files, f => f.StartsWith("imported-original-", StringComparison.Ordinal));
    }

    [Fact]
    public void Later_exits_and_writes_nothing_not_even_the_folder()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.Later);

        Assert.Equal(FirstStartEnd.Exit, FirstStartFlow.Run(bed.Engine, script));

        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void Start_fresh_asks_to_confirm_and_No_puts_the_same_question_again()
    {
        using var bed = new ImportBed();
        string source = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        byte[] old = File.ReadAllBytes(source);
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.StartFresh);
        script.Confirms.Enqueue(false);
        script.Continues.Enqueue(ContinueChoice.StartFresh);
        script.Confirms.Enqueue(true);

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        Assert.Equal(2, script.Asked.Count);
        Assert.Equal(2, script.ConfirmCalls);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
        Assert.Equal(old, File.ReadAllBytes(source));
    }

    [Fact]
    public void No_to_the_confirmation_then_Later_writes_nothing()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var script = new Script();
        script.Continues.Enqueue(ContinueChoice.StartFresh);
        script.Confirms.Enqueue(false);
        script.Continues.Enqueue(ContinueChoice.Later);

        Assert.Equal(FirstStartEnd.Exit, FirstStartFlow.Run(bed.Engine, script));

        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_running_old_product_shows_the_message_and_Retry_goes_on_once_it_is_closed()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;
        var script = new Script { OnProblem = () => bed.Probe.State = LegacyRunningState.NotRunning };
        script.Problems.Enqueue(ProblemChoice.Retry);
        script.Continues.Enqueue(ContinueChoice.Continue);

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        var shown = script.Shown.Single();
        Assert.Equal(ImportOutcome.OldAppRunning, shown.Outcome);
        Assert.Equal(ImportReason.OldAppRunning, shown.Reason);
        Assert.False(shown.CanStartFresh);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void Close_on_a_message_exits_and_writes_nothing()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.Close);

        Assert.Equal(FirstStartEnd.Exit, FirstStartFlow.Run(bed.Engine, script));

        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_message_that_does_not_offer_start_fresh_never_starts_fresh_even_when_it_is_answered_so()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.StartFresh);
        script.Problems.Enqueue(ProblemChoice.Close);

        Assert.Equal(FirstStartEnd.Exit, FirstStartFlow.Run(bed.Engine, script));

        Assert.Equal(2, script.Shown.Count);
        Assert.Equal(0, script.ConfirmCalls);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_hold_offers_start_fresh_with_the_confirmation()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Bytes(Minimal(saveVersion: 99)));
        var script = new Script();
        script.Problems.Enqueue(ProblemChoice.StartFresh);
        script.Confirms.Enqueue(true);

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        var shown = script.Shown.Single();
        Assert.Equal(ImportOutcome.Hold, shown.Outcome);
        Assert.Equal(ImportReason.VersionFuture, shown.Reason);
        Assert.True(shown.CanStartFresh);
        Assert.Equal(1, script.ConfirmCalls);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_source_that_changed_between_the_question_and_the_button_is_looked_at_again()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var script = new Script { OnAsk = () => bed.RewriteInPlace(ImportOrigin.PartsLife, Save(222.5)) };
        script.Continues.Enqueue(ContinueChoice.Continue);   // refused: the source is not the version that was shown
        script.Continues.Enqueue(ContinueChoice.Continue);   // the new question

        Assert.Equal(FirstStartEnd.Proceed, FirstStartFlow.Run(bed.Engine, script));

        Assert.Equal(2, script.Asked.Count);
        Assert.NotEqual(script.Asked[0].Source, script.Asked[1].Source);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }
}
