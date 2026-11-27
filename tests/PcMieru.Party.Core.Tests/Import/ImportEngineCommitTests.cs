using System.Text;
using System.Text.RegularExpressions;
using PcMieru.Party.Game;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// ImportEngine.Commit: it re-checks everything and only then writes. The tests change the world between Prepare and Commit the way
/// the real world does (the old program rewrites its save, a rename replaces it, the destination appears) and look at what is left on disk.
/// </summary>
public class ImportEngineCommitTests
{
    private static byte[] Save(double memory) => ImportEnginePrepareTests.Save(memory);

    /// <summary>A source that needs repairs: one known field is not a number.</summary>
    private static byte[] BrokenGold(string gold = "lots")
    {
        var src = V1();
        src["Adventure"]!["Gold"] = gold;
        return Bytes(src);
    }

    // ---- the plain import ----

    [Fact]
    public void A_ready_plan_writes_the_converted_save_and_nothing_else()
    {
        using var bed = new ImportBed();
        byte[] source = Bytes(V1());
        bed.WriteSource(ImportOrigin.PartsLife, source);
        var expected = StateImport.Inspect(source);

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        var result = bed.Engine.Commit(plan, consentToRepair: false);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal(ImportReason.Imported, result.Reason);
        Assert.True(result.Succeeded);
        Assert.False(result.Repaired);
        Assert.Empty(result.RepairedFields);
        Assert.Null(result.OriginalCopy);
        Assert.Equal(ImportOrigin.PartsLife, result.Candidate!.Origin);

        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());          // no temp file, no copy of the original
        Assert.Equal(expected.Converted, bed.DestinationBytes());              // the bytes of the pure importer, as they are
        Assert.NotEqual((byte)0xEF, bed.DestinationBytes()[0]);                // no BOM

        var loaded = PartyState.Load(bed.DestinationDir);
        AssertSameProgress(Read(expected), loaded);
        Assert.NotEmpty(loaded.Experience);
        Assert.True(loaded.Journey > 0);
    }

    [Fact]
    public void Committing_the_same_plan_twice_leaves_the_first_import_alone()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(plan, false).Outcome);
        byte[] first = bed.DestinationBytes();
        DateTime written = File.GetLastWriteTimeUtc(bed.Destination);

        var again = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.AlreadyHave, again.Outcome);
        Assert.Equal(first, bed.DestinationBytes());
        Assert.Equal(written, File.GetLastWriteTimeUtc(bed.Destination));
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void After_an_import_the_destination_wins_and_the_old_save_is_not_asked_again()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(bed.Engine.Prepare(), false).Outcome);
        int asked = bed.Probe.Calls;

        bed.RewriteInPlace(ImportOrigin.PartsLife, Save(333.5));   // the old program goes on writing its own file
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.AlreadyHave, plan.Status);
        Assert.Equal(asked, bed.Probe.Calls);
        Assert.Equal("111.5", PartyState.Load(bed.DestinationDir).Experience.Values.Single().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_temp_file_is_next_to_the_destination_and_the_destination_is_not_there_until_the_move()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var seen = new List<string>();
        bed.Engine.BeforeMove = temp =>
        {
            seen.Add(temp);
            Assert.True(File.Exists(temp));
            Assert.False(File.Exists(bed.Destination));
            Assert.Equal(bed.DestinationDir, Path.GetDirectoryName(temp));
            Assert.Matches(@"^state\.json\.import-[0-9a-f]{32}\.tmp$", Path.GetFileName(temp));
        };

        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(bed.Engine.Prepare(), false).Outcome);
        Assert.Single(seen);
        Assert.False(File.Exists(seen[0]));   // moved, not copied
    }

    // ---- the world changes between Prepare and Commit ----

    [Fact]
    public void The_old_program_rewriting_in_place_with_the_same_size_is_seen_by_the_hash()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        bed.RewriteInPlace(ImportOrigin.PartsLife, Save(222.5));   // same file, same length, other bytes

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.Equal(ImportReason.SourceChanged, result.Reason);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void The_old_program_rewriting_in_place_with_another_size_is_seen()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        bed.RewriteInPlace(ImportOrigin.PartsLife, Save(1234567.5));

        Assert.Equal(ImportOutcome.SourceChanged, bed.Engine.Commit(plan, false).Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void The_old_program_replacing_it_by_rename_is_seen_even_when_the_bytes_are_identical()
    {
        using var bed = new ImportBed();
        byte[] bytes = Save(111.5);
        bed.WriteSource(ImportOrigin.PartsLife, bytes);
        var plan = bed.Engine.Prepare();

        bed.ReplaceByRename(ImportOrigin.PartsLife, bytes);   // a new file (new identity) with the very same content

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void The_old_program_replacing_it_by_rename_with_other_bytes_is_seen()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        bed.ReplaceByRename(ImportOrigin.PartsLife, Save(222.5));

        Assert.Equal(ImportOutcome.SourceChanged, bed.Engine.Commit(plan, false).Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_source_that_is_gone_is_a_change_not_an_absence()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        bed.DeleteSource(ImportOrigin.PartsLife);

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.SourceChanged, result.Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_source_held_without_sharing_at_commit_is_unreadable_not_changed()
    {
        using var bed = new ImportBed();
        string path = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = bed.Engine.Commit(plan, false);
            Assert.Equal(ImportOutcome.SourceUnreadable, result.Outcome);
            Assert.Equal(ImportReason.SourceSharing, result.Reason);
        }

        Assert.False(Directory.Exists(bed.DestinationDir));
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(plan, false).Outcome);   // and once it is free, the same plan still holds
    }

    [Fact]
    public void The_old_product_starting_between_Prepare_and_Commit_holds_the_commit()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        Assert.Equal(1, bed.Probe.Calls);

        bed.Probe.State = LegacyRunningState.Running;
        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.OldAppRunning, result.Outcome);
        Assert.Equal(ImportReason.OldAppRunning, result.Reason);
        Assert.Equal(2, bed.Probe.Calls);   // asked again at the commit
        Assert.False(Directory.Exists(bed.DestinationDir));

        bed.Probe.State = LegacyRunningState.Unknown;
        Assert.Equal(ImportReason.OldAppUnknown, bed.Engine.Commit(plan, false).Reason);

        bed.Probe.State = LegacyRunningState.NotRunning;
        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(plan, false).Outcome);
    }

    [Fact]
    public void A_destination_that_appeared_between_Prepare_and_Commit_wins()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        bed.WriteDestination(Save(5.5));
        byte[] theirs = bed.DestinationBytes();

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.AlreadyHave, result.Outcome);
        Assert.Equal(theirs, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_broken_destination_that_appeared_between_Prepare_and_Commit_is_kept_and_blocks()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        bed.WriteDestination(Encoding.UTF8.GetBytes("not json"));

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.DestinationBlocked, result.Outcome);
        Assert.Equal(ImportReason.DestinationBroken, result.Reason);
        Assert.Equal("not json", Encoding.UTF8.GetString(bed.DestinationBytes()));
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void A_destination_that_appears_just_before_the_move_wins_and_the_temp_file_goes()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        bed.Engine.BeforeMove = _ => File.WriteAllBytes(bed.Destination, Save(5.5));   // the folder exists: Publish made it

        var result = bed.Engine.Commit(plan, false);
        Assert.Equal(ImportOutcome.AlreadyHave, result.Outcome);
        Assert.Equal(Save(5.5), bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());   // only ours is deleted: theirs stays, our temp is gone
    }

    [Fact]
    public void A_broken_destination_that_appears_just_before_the_move_is_kept_and_nothing_of_ours_is_left()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.NeedsRepair, plan.Status);
        bed.Engine.BeforeMove = _ => File.WriteAllText(bed.Destination, "[]");

        var result = bed.Engine.Commit(plan, consentToRepair: true);
        Assert.Equal(ImportOutcome.DestinationBlocked, result.Outcome);
        Assert.Equal("[]", File.ReadAllText(bed.Destination));
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());   // the original copy of this call is gone too
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file_and_no_destination_and_the_next_try_works()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        var plan = bed.Engine.Prepare();
        bed.Engine.BeforeMove = _ => throw new IOException("the disk went away");

        var result = bed.Engine.Commit(plan, consentToRepair: true);
        Assert.Equal(ImportOutcome.Failed, result.Outcome);
        Assert.Equal(ImportReason.WriteFailed, result.Reason);
        Assert.False(File.Exists(bed.Destination));
        Assert.Empty(bed.DestinationFiles());   // neither the temp file nor the original copy of the failed call

        bed.Engine.BeforeMove = null;
        var retry = bed.Engine.Commit(bed.Engine.Prepare(), consentToRepair: true);
        Assert.Equal(ImportOutcome.Imported, retry.Outcome);
        Assert.Equal(2, bed.DestinationFiles().Length);   // state.json and the original copy
    }

    // ---- the plan itself ----

    [Fact]
    public void A_plan_that_points_outside_this_engines_candidates_is_refused_and_nothing_is_read()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();
        string elsewhere = Path.Combine(bed.Root, "elsewhere", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllBytes(elsewhere, Save(111.5));

        var forged = plan with { Candidate = new ImportCandidate(ImportOrigin.PartsLife, elsewhere) };
        var result = bed.Engine.Commit(forged, false);
        Assert.Equal(ImportOutcome.Failed, result.Outcome);
        Assert.Equal(ImportReason.InternalError, result.Reason);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_plan_with_a_made_up_fingerprint_is_not_the_inspected_version()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var plan = bed.Engine.Prepare();

        var forged = plan with { Source = plan.Source! with { Sha256 = new string('0', 64) } };
        Assert.Equal(ImportOutcome.SourceChanged, bed.Engine.Commit(forged, false).Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_null_plan_is_an_argument_error()
    {
        using var bed = new ImportBed();
        Assert.Throws<ArgumentNullException>(() => bed.Engine.Commit(null!, false));
    }

    // ---- repairs ----

    [Fact]
    public void A_source_that_needs_repairs_says_which_fields_and_waits_for_consent()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.NeedsRepair, plan.Status);
        Assert.Equal(ImportReason.NeedsRepair, plan.Reason);
        Assert.True(plan.CanCommit);
        Assert.Contains("Adventure.Gold", plan.RepairedFields);
        Assert.False(Directory.Exists(bed.DestinationDir));

        var refused = bed.Engine.Commit(plan, consentToRepair: false);
        Assert.Equal(ImportOutcome.NeedsConsent, refused.Outcome);
        Assert.Equal(ImportReason.NeedsConsent, refused.Reason);
        Assert.Contains("Adventure.Gold", refused.RepairedFields);
        Assert.False(refused.Succeeded);
        Assert.False(Directory.Exists(bed.DestinationDir));   // not even the folder
    }

    [Fact]
    public void A_consented_repair_keeps_the_untouched_original_next_to_the_new_save()
    {
        using var bed = new ImportBed();
        byte[] source = BrokenGold();
        bed.WriteSource(ImportOrigin.PartsLife, source);
        var now = new DateTime(2026, 11, 26, 12, 34, 56, DateTimeKind.Utc);
        bed.Engine.UtcNow = () => now;

        var plan = bed.Engine.Prepare();
        var result = bed.Engine.Commit(plan, consentToRepair: true);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.True(result.Repaired);
        Assert.Contains("Adventure.Gold", result.RepairedFields);
        Assert.Equal(Path.Combine(bed.DestinationDir, "imported-original-20261126T123456Z.json"), result.OriginalCopy);
        Assert.Equal(source, File.ReadAllBytes(result.OriginalCopy!));   // the very bytes that were read, repairs or not
        Assert.Equal(new[] { "imported-original-20261126T123456Z.json", "state.json" }, bed.DestinationFiles());

        var loaded = PartyState.Load(bed.DestinationDir);
        Assert.Equal(PcMieru.Party.Adventure.AdventureState.StartingGold, loaded.Adventure!.Gold);   // the repaired field has its default, not the old 1234
        AssertSameProgress(Read(StateImport.Inspect(source)), loaded);   // and the rest is what the converter said
        Assert.Equal(Read(Inspect(V1())).Journey, loaded.Journey);
    }

    [Fact]
    public void Two_repaired_imports_in_the_same_second_do_not_overwrite_the_first_original()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        bed.Engine.UtcNow = () => new DateTime(2026, 11, 26, 12, 34, 56, DateTimeKind.Utc);
        Directory.CreateDirectory(bed.DestinationDir);
        string earlier = Path.Combine(bed.DestinationDir, "imported-original-20261126T123456Z.json");
        File.WriteAllText(earlier, "an earlier original");

        var result = bed.Engine.Commit(bed.Engine.Prepare(), consentToRepair: true);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal(Path.Combine(bed.DestinationDir, "imported-original-20261126T123456Z-2.json"), result.OriginalCopy);
        Assert.Equal("an earlier original", File.ReadAllText(earlier));
    }

    [Fact]
    public void Consent_does_not_matter_when_nothing_needs_repair_and_no_copy_is_made()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        var result = bed.Engine.Commit(bed.Engine.Prepare(), consentToRepair: true);
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.False(result.Repaired);
        Assert.Null(result.OriginalCopy);
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());
    }

    [Fact]
    public void An_earlier_original_copy_is_not_a_destination_and_is_never_deleted_by_a_failed_call()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, BrokenGold());
        bed.Engine.UtcNow = () => new DateTime(2026, 11, 26, 12, 34, 56, DateTimeKind.Utc);
        Directory.CreateDirectory(bed.DestinationDir);
        string earlier = Path.Combine(bed.DestinationDir, "imported-original-20261126T123456Z.json");
        File.WriteAllText(earlier, "an earlier original");
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.NeedsRepair, plan.Status);
        bed.Engine.BeforeMove = _ => throw new IOException("boom");

        Assert.Equal(ImportOutcome.Failed, bed.Engine.Commit(plan, true).Outcome);

        Assert.Equal("an earlier original", File.ReadAllText(earlier));
        Assert.Equal(new[] { "imported-original-20261126T123456Z.json" }, bed.DestinationFiles());   // only this call's own files were removed
    }
}
