using System.Text;
using System.Text.Json.Nodes;
using PcMieru.Party.Game;
using PcMieru.Party.Import;
using static PcMieru.Party.Core.Tests.Import.ImportRig;

namespace PcMieru.Party.Core.Tests.Import;

/// <summary>
/// ImportEngine.Prepare: which candidate, what the destination says, whether the old product runs, what the source is. It writes nothing:
/// every test ends with the bed's check that the old saves are untouched, and the ones that say "nothing written" look at the destination.
/// </summary>
public class ImportEnginePrepareTests
{
    /// <summary>A save that passes every rule; the number tells one save from another (the same length for the same digit count).</summary>
    internal static byte[] Save(double memory)
    {
        var o = Minimal();
        o["Experience"] = new JsonObject { ["memory"] = memory };
        return Bytes(o);
    }

    private static string Memory(string destinationDir) => PartyState.Load(destinationDir).Experience.Values.Single().ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- the candidates ----

    [Fact]
    public void PartsLife_wins_over_PartsParty()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        Assert.Equal("accept", plan.Reason);
        Assert.Equal(ImportOrigin.PartsLife, plan.Candidate!.Origin);
        Assert.Equal(bed.SourcePath(ImportOrigin.PartsLife), plan.Candidate.Path);
        Assert.True(plan.CanCommit);

        var result = bed.Engine.Commit(plan, consentToRepair: false);
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal("111.5", Memory(bed.DestinationDir));
    }

    [Fact]
    public void PartsParty_is_used_only_when_PartsLife_is_absent()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));   // no PartsLife folder at all: DirectoryNotFound

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        Assert.Equal(ImportOrigin.PartsParty, plan.Candidate!.Origin);

        Assert.Equal(ImportOutcome.Imported, bed.Engine.Commit(plan, false).Outcome);
        Assert.Equal("222.5", Memory(bed.DestinationDir));
    }

    [Fact]
    public void An_empty_PartsLife_folder_is_absent_too_FileNotFound()
    {
        using var bed = new ImportBed();
        Directory.CreateDirectory(Path.GetDirectoryName(bed.SourcePath(ImportOrigin.PartsLife))!);
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        Assert.Equal(ImportOrigin.PartsParty, plan.Candidate!.Origin);
    }

    [Fact]
    public void A_folder_named_state_json_is_a_deny_and_does_not_fall_through()
    {
        using var bed = new ImportBed();
        Directory.CreateDirectory(bed.SourcePath(ImportOrigin.PartsLife));   // a folder where the file should be
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.SourceUnreadable, plan.Status);
        Assert.Equal("source-denied", plan.Reason);
        Assert.Equal(ImportOrigin.PartsLife, plan.Candidate!.Origin);
        Assert.False(plan.CanCommit);
        Assert.Equal(0, bed.Probe.Calls);                       // it stopped before the old product was asked
        Assert.False(Directory.Exists(bed.DestinationDir));     // and wrote nothing

        var result = bed.Engine.Commit(plan, true);             // a plan that cannot be committed is answered as it is
        Assert.Equal(ImportOutcome.SourceUnreadable, result.Outcome);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_holder_without_sharing_is_a_sharing_violation_and_does_not_fall_through()
    {
        using var bed = new ImportBed();
        string path = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.WriteSource(ImportOrigin.PartsParty, Save(222.5));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plan = bed.Engine.Prepare();
            Assert.Equal(ImportOutcome.SourceUnreadable, plan.Status);
            Assert.Equal("source-sharing", plan.Reason);
            Assert.Equal(ImportOrigin.PartsLife, plan.Candidate!.Origin);
            Assert.False(Directory.Exists(bed.DestinationDir));
        }

        // The holder is gone: the same call now goes through.
        Assert.Equal(ImportOutcome.Ready, bed.Engine.Prepare().Status);
    }

    [Fact]
    public void A_holder_that_shares_reading_and_writing_does_not_stop_the_read()
    {
        using var bed = new ImportBed();
        string path = bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(ImportOutcome.Ready, bed.Engine.Prepare().Status);
    }

    [Fact]
    public void No_candidate_at_all_is_NoCandidate()
    {
        using var bed = new ImportBed();
        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.NoCandidate, plan.Status);
        Assert.Equal("no-candidate", plan.Reason);
        Assert.Null(plan.Candidate);
        Assert.False(plan.CanCommit);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    // ---- the destination ----

    [Fact]
    public void A_valid_destination_is_AlreadyHave_and_is_not_touched()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.WriteDestination(Save(5.5));
        byte[] before = bed.DestinationBytes();

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.AlreadyHave, plan.Status);
        Assert.Equal("already-have", plan.Reason);
        Assert.Equal(0, bed.Probe.Calls);   // the destination is asked first
        Assert.Equal(before, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());

        Assert.Equal(ImportOutcome.AlreadyHave, bed.Engine.Commit(plan, true).Outcome);
        Assert.Equal(before, bed.DestinationBytes());
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Experience\": ")]
    public void A_destination_that_cannot_be_read_as_a_save_is_blocked_and_kept(string text)
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.WriteDestination(Encoding.UTF8.GetBytes(text));
        byte[] before = bed.DestinationBytes();

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.DestinationBlocked, plan.Status);
        Assert.Equal("destination-broken", plan.Reason);
        Assert.False(plan.CanCommit);
        Assert.Equal(before, bed.DestinationBytes());
        Assert.Equal(new[] { "state.json" }, bed.DestinationFiles());   // no .bad copy, no temp file
    }

    [Fact]
    public void A_destination_held_without_sharing_is_blocked_and_a_folder_in_its_place_is_a_deny()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.WriteDestination(Save(5.5));

        using (new FileStream(bed.Destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plan = bed.Engine.Prepare();
            Assert.Equal(ImportOutcome.DestinationBlocked, plan.Status);
            Assert.Equal("destination-sharing", plan.Reason);
        }

        using var bed2 = new ImportBed();
        bed2.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        Directory.CreateDirectory(bed2.Destination);
        var plan2 = bed2.Engine.Prepare();
        Assert.Equal(ImportOutcome.DestinationBlocked, plan2.Status);
        Assert.Equal("destination-denied", plan2.Reason);
        Assert.True(Directory.Exists(bed2.Destination));
    }

    [Fact]
    public void An_absent_destination_goes_on_and_Prepare_writes_nothing()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        Assert.False(Directory.Exists(bed.DestinationDir));   // not even the folder
        Assert.False(Directory.Exists(Path.Combine(bed.Paths.RoamingRoot, "PCMieru")));
    }

    // ---- the old product ----

    [Theory]
    [InlineData(LegacyRunningState.Running, "old-app-running")]
    [InlineData(LegacyRunningState.Unknown, "old-app-unknown")]
    public void The_old_product_running_or_unknown_holds_the_import(LegacyRunningState state, string reason)
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        bed.Probe.State = state;

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.OldAppRunning, plan.Status);
        Assert.Equal(reason, plan.Reason);
        Assert.Equal(ImportOrigin.PartsLife, plan.Candidate!.Origin);
        Assert.False(plan.CanCommit);
        Assert.Equal(1, bed.Probe.Calls);
        Assert.False(Directory.Exists(bed.DestinationDir));
    }

    [Fact]
    public void A_probe_that_throws_is_Unknown()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Save(111.5));
        var engine = new ImportEngine(bed.Paths, new ThrowingProbe(), bed.LockName, TimeSpan.FromMilliseconds(500));

        var plan = engine.Prepare();
        Assert.Equal(ImportOutcome.OldAppRunning, plan.Status);
        Assert.Equal("old-app-unknown", plan.Reason);
    }

    private sealed class ThrowingProbe : ILegacyRunningProbe
    {
        public LegacyRunningState Check() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void No_candidate_is_said_before_the_old_product_is_asked()
    {
        using var bed = new ImportBed();
        bed.Probe.State = LegacyRunningState.Running;
        Assert.Equal(ImportOutcome.NoCandidate, bed.Engine.Prepare().Status);
        Assert.Equal(0, bed.Probe.Calls);
    }

    // ---- the source's own verdict ----

    [Fact]
    public void A_source_that_must_be_held_is_a_Hold_with_the_importers_reason()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Encoding.UTF8.GetBytes("{\"Experience\": {"));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Hold, plan.Status);
        Assert.Equal(ImportReason.BrokenJson, plan.Reason);
        Assert.NotNull(plan.Source);
        Assert.False(plan.CanCommit);
        Assert.False(Directory.Exists(bed.DestinationDir));

        var result = bed.Engine.Commit(plan, true);
        Assert.Equal(ImportOutcome.Hold, result.Outcome);
        Assert.Equal(ImportReason.BrokenJson, result.Reason);
    }

    [Fact]
    public void A_future_version_is_a_Hold()
    {
        using var bed = new ImportBed();
        var o = Minimal(saveVersion: 99);
        bed.WriteSource(ImportOrigin.PartsLife, Bytes(o));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Hold, plan.Status);
        Assert.Equal(ImportReason.VersionFuture, plan.Reason);
    }

    [Fact]
    public void A_source_over_the_cap_is_a_Hold_too_large_and_only_the_cap_plus_one_is_read()
    {
        using var bed = new ImportBed();
        var big = new byte[StateImport.MaxBytes + 1000];
        Array.Fill(big, (byte)' ');
        bed.WriteSource(ImportOrigin.PartsLife, big);

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Hold, plan.Status);
        Assert.Equal(ImportReason.TooLarge, plan.Reason);
        Assert.Equal(big.Length, plan.Source!.Size);   // the real size, from the handle
    }

    [Fact]
    public void An_acceptable_plan_carries_the_fingerprint_of_what_was_read()
    {
        using var bed = new ImportBed();
        byte[] bytes = Save(111.5);
        bed.WriteSource(ImportOrigin.PartsLife, bytes);

        var plan = bed.Engine.Prepare();
        Assert.Equal(bytes.Length, plan.Source!.Size);
        Assert.Equal(Sha(bytes), plan.Source.Sha256);
        Assert.NotEqual(0UL, plan.Source.FileIndex);
        Assert.NotEqual(0u, plan.Source.VolumeSerial);
        Assert.Equal(1, plan.SourceVersion);
        Assert.Empty(plan.RepairedFields);
    }

    [Fact]
    public void An_UpdateMode_in_the_source_is_listed_as_dropped()
    {
        using var bed = new ImportBed();
        bed.WriteSource(ImportOrigin.PartsLife, Bytes(V1()));

        var plan = bed.Engine.Prepare();
        Assert.Equal(ImportOutcome.Ready, plan.Status);
        Assert.Contains("UpdateMode", plan.DroppedFields);
    }
}
