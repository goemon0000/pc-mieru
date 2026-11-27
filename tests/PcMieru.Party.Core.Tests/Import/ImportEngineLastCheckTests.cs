using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// The four fixes of codex's review of the engine: the old product is asked before a candidate is opened; the original copy is staged like
/// the destination; the last look just before the final move; and the candidate is found again at the commit.
/// </summary>
public class ImportEngineLastCheckTests
{
    private static byte[] Save(double memory) => ImportEnginePrepareTests.Save(memory);

    private static byte[] BrokenGold(string gold = "lots")
    {
        var src = V1();
        src["Adventure"]!["Gold"] = gold;
        return Bytes(src);
    }

    /// <summary>Says NotRunning until it is asked for the Nth time (1-based), and Running from then on.</summary>
    private sealed class FlippingProbe(int runningFromCall, LegacyRunningState then = LegacyRunningState.Running) : ILegacyRunningProbe
    {
        public int Calls { get; private set; }

        public LegacyRunningState Check() => ++Calls >= runningFromCall ? then : LegacyRunningState.NotRunning;
    }

    private static ImportEngine EngineWith(ImportBed bed, ILegacyRunningProbe probe) =>
        new(bed.Paths, probe, bed.LockName, TimeSpan.FromMilliseconds(500));

    // ---- codex 1: the probe is asked before any candidate is opened ----

    [Fact]
    public void A_running_old_product_is_reported_without_opening_the_source()
    {
        using var bed = new ImportBed();
        string path = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Running;

        // The source is held without sharing: opening it would say "source-sharing". It must not be opened at all.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var plan = bed.Engine.Prepare();
            Assert.Equal(ImportOutcome.OldAppRunning, plan.Status);
            Assert.Equal(ImportReason.OldAppRunning, plan.Reason);
        }

        Assert.Equal(1, bed.Probe.Calls);
    }

    [Fact]
    public void An_unknown_answer_of_the_probe_is_reported_without_opening_the_source()
    {
        using var bed = new ImportBed();
        string path = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = LegacyRunningState.Unknown;

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var plan = bed.Engine.Prepare();
            Assert.Equal(ImportOutcome.OldAppRunning, plan.Status);
            Assert.Equal(ImportReason.OldAppUnknown, plan.Reason);
        }
    }

    // ---- codex 2: the original copy is staged like the destination ----

    [Fact]
    public void The_original_copy_is_complete_in_a_temp_file_before_it_gets_its_final_name()
    {
        using var bed = new ImportBed();
        byte[] source = BrokenGold();
        bed.WriteSource(ImportOrigin.PartsLife, source);
        bed.Engine.UtcNow = () => new DateTime(2026, 11, 26, 12, 34, 56, DateTimeKind.Utc);
        var seen = new List<string>();
        bed.Engine.BeforeOriginalMove = tmp =>
        {
            seen.Add(tmp);
            Assert.Equal(bed.DestinationDir, Path.GetDirectoryName(tmp));
            Assert.Matches(@"^imported-original\.import-[0-9a-f]{32}\.tmp$", Path.GetFileName(tmp));
            Assert.Equal(source, File.ReadAllBytes(tmp));                                                  // all of it, already
            Assert.False(File.Exists(Path.Combine(bed.DestinationDir, "imported-original-20261126T123456Z.json")));   // the final name is not there yet
        };

        var result = bed.Engine.Commit(bed.Engine.Prepare(), consentToRepair: true);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Single(seen);
        Assert.False(File.Exists(seen[0]));   // moved, not copied
        Assert.Equal(source, File.ReadAllBytes(result.OriginalCopy!));
        Assert.Equal(new[] { "imported-original-20261126T123456Z.json", "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_failure_while_the_original_copy_is_staged_leaves_no_file_and_no_destination()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        bed.Engine.BeforeOriginalMove = _ => throw new IOException("the power went");
        var plan = bed.Engine.Prepare();

        var result = bed.Engine.Commit(plan, consentToRepair: true);

        Assert.Equal(ImportOutcome.Failed, result.Outcome);
        Assert.Empty(bed.DestinationFiles());   // neither the staged copy nor any state.json
    }

    // ---- codex 3 and 4: the last look just before the final move ----

    [Fact]
    public void The_old_product_starting_just_before_the_final_move_stops_it_and_removes_the_files_of_this_call()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        bed.Engine.UtcNow = () => new DateTime(2026, 11, 26, 12, 34, 56, DateTimeKind.Utc);
        var probe = new FlippingProbe(runningFromCall: 3);   // 1 = Prepare, 2 = the start of Commit, 3 = the last look
        var engine = EngineWith(bed, probe);
        engine.UtcNow = bed.Engine.UtcNow;
        var plan = engine.Prepare();
        Assert.Equal(ImportOutcome.NeedsRepair, plan.Status);

        var result = engine.Commit(plan, consentToRepair: true);

        Assert.Equal(ImportOutcome.OldAppRunning, result.Outcome);
        Assert.Equal(ImportReason.OldAppRunning, result.Reason);
        Assert.Equal(3, probe.Calls);
        Assert.Null(result.OriginalCopy);
        Assert.Empty(bed.DestinationFiles());   // no state.json, no temp file, no original copy
    }

    [Fact]
    public void An_unknown_answer_at_the_last_look_stops_it_too()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var engine = EngineWith(bed, new FlippingProbe(3, LegacyRunningState.Unknown));
        var plan = engine.Prepare();

        var result = engine.Commit(plan, false);

        Assert.Equal(ImportOutcome.OldAppRunning, result.Outcome);
        Assert.Equal(ImportReason.OldAppUnknown, result.Reason);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void The_source_replaced_just_before_the_final_move_stops_it_and_removes_the_files_of_this_call()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold("lots"));
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.NeedsRepair, plan.Status);
        // the old program saves once more (its own way: a new file, then a rename over the old one) after the converted bytes were staged
        bed.Engine.BeforeMove = _ => bed.ReplaceByRename(ImportOrigin.PartsLife, BrokenGold("many"));

        var result = bed.Engine.Commit(plan, consentToRepair: true);

        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Equal(ImportReason.SourceChanged, result.Reason);
        Assert.Null(result.OriginalCopy);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void The_source_rewritten_in_place_with_the_same_length_just_before_the_final_move_is_seen_by_the_hash()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        bed.Engine.BeforeMove = _ => bed.RewriteInPlace(ImportOrigin.PartsLife, Save(222.5));

        var result = bed.Engine.Commit(plan, false);

        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void A_source_that_vanishes_just_before_the_final_move_stops_it()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        bed.Engine.BeforeMove = _ => bed.DeleteSource(ImportOrigin.PartsLife);

        var result = bed.Engine.Commit(plan, false);

        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Empty(bed.DestinationFiles());
    }

    [Fact]
    public void An_unchanged_world_passes_the_last_look_and_the_destination_is_written()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var probe = new FlippingProbe(runningFromCall: 99);
        var engine = EngineWith(bed, probe);

        var result = engine.Commit(engine.Prepare(), false);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal(3, probe.Calls);   // Prepare, the start of Commit, the last look
    }

    // ---- codex 5: the candidate is found again at the commit ----

    [Fact]
    public void A_PartsLife_that_appeared_after_the_plan_chose_PartsParty_makes_the_plan_stale()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOrigin.PartsParty, plan.Candidate!.Origin);

        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Equal(ImportReason.SourceChanged, result.Reason);
        Assert.False(Directory.Exists(bed.DestinationDir));

        // A new plan picks the newcomer, and that one commits.
        var again = bed.Engine.Prepare();
        Assert.Equal(ImportOrigin.PartsLife, again.Candidate!.Origin);
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(again, false).Outcome);
    }

    [Fact]
    public void A_PartsLife_that_became_unreadable_after_the_plan_chose_PartsParty_makes_the_plan_stale()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));
        var plan = bed.Engine.Prepare();
        string life = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        using (new FileStream(life, FileMode.Open, FileAccess.Read, FileShare.None))   // the old product holds it without sharing
        {
            var result = bed.Engine.Commit(plan, false);
            Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
            Assert.False(Directory.Exists(bed.DestinationDir));
        }
    }

    [Fact]
    public void A_candidate_that_is_gone_at_the_commit_makes_the_plan_stale()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));
        var plan = bed.Engine.Prepare();
        bed.DeleteSource(ImportOrigin.PartsParty);

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void The_same_candidate_that_became_unreadable_keeps_its_own_reason()
    {
        using var bed = new ImportBed();
        string life = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        using (new FileStream(life, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = bed.Engine.Commit(plan, false);
            Assert.Equal(ImportOutcome.SourceUnreadable, result.Outcome);
            Assert.Equal(ImportReason.SourceSharing, result.Reason);
        }

        Assert.False(Directory.Exists(bed.DestinationDir));
    }
}
